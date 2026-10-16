using System.Text.Json;
using Azure;
using Azure.Storage.Queues;
using Moq;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;
using SendReceipt = Azure.Storage.Queues.Models.SendReceipt;

namespace TeamsNotificationBot.Tests.Services;

public class NotificationQueueTests
{
    private readonly Mock<QueueClient> _queueClient = new();
    private readonly Mock<IDeliveryRecords> _records = new();
    private readonly Mock<IDeliveryEvents> _events = new();
    private readonly NotificationQueue _queue;

    public NotificationQueueTests()
    {
        _queue = new NotificationQueue(_queueClient.Object, _records.Object, _events.Object);
    }

    [Fact]
    public async Task Enqueue_RecordsFirst_ThenSends_ThenEmitsQueuedEvent()
    {
        var order = new List<string>();
        string? sent = null;
        _records.Setup(r => r.CreateAsync(It.IsAny<QueueMessage>())).Callback(() => order.Add("record"));
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .Callback<string>(s => { sent = s; order.Add("send"); })
            .ReturnsAsync(Mock.Of<Response<SendReceipt>>());
        _events.Setup(e => e.Queued(It.IsAny<QueueMessage>())).Callback(() => order.Add("event"));
        var message = new QueueMessage { MessageId = "msg-1", Alias = "ops", Message = "hi", Source = "notify" };

        await _queue.EnqueueAsync(message);

        Assert.Equal(["record", "send", "event"], order);
        var roundTripped = JsonSerializer.Deserialize<QueueMessage>(sent!);
        Assert.Equal("msg-1", roundTripped!.MessageId);
        Assert.Equal("notify", roundTripped.Source);
    }

    [Fact]
    public async Task Enqueue_EscapesOnlyWhatJsonRequires()
    {
        string? sent = null;
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .Callback<string>(s => sent = s)
            .ReturnsAsync(Mock.Of<Response<SendReceipt>>());
        var card = "{\"text\":\"Hei på deg <3\"}";
        var message = new QueueMessage { MessageId = "msg-5", Message = card, Format = "adaptive-card" };

        await _queue.EnqueueAsync(message);

        // Quotes as \", not \u0022; æøå and <, & as themselves, not \uXXXX.
        Assert.Contains("\\\"text\\\"", sent);
        Assert.Contains("Hei på deg <3", sent);
        Assert.DoesNotContain("\\u", sent);
        Assert.Equal(card, JsonSerializer.Deserialize<QueueMessage>(sent!)!.Message);
    }

    [Fact]
    public async Task Enqueue_ACardWithinTheRequestLimit_FitsTheQueue()
    {
        // Many short values: the shape that grows the most. At the 28 KB request limit, this
        // card was an 86,700-byte queue message with the default encoder, over Storage's 64 KB.
        string? sent = null;
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .Callback<string>(s => sent = s)
            .ReturnsAsync(Mock.Of<Response<SendReceipt>>());
        var card = DenseCard(28 * 1024);

        await _queue.EnqueueAsync(new QueueMessage { MessageId = "msg-6", Message = card, Format = "adaptive-card" });

        Assert.True(Base64Length(sent!) <= 64 * 1024, $"queue message is {Base64Length(sent!)} bytes");
    }

    /// <summary>An Adaptive Card of short facts, as close to <paramref name="bytes"/> as it gets.</summary>
    internal static string DenseCard(int bytes)
    {
        var facts = new List<object>();
        var card = "";
        for (var i = 0; ; i++)
        {
            facts.Add(new { title = $"t{i}", value = $"v{i}" });
            var next = JsonSerializer.Serialize(new { type = "AdaptiveCard", version = "1.4", body = new object[] { new { type = "FactSet", facts } } });
            if (System.Text.Encoding.UTF8.GetByteCount(next) > bytes)
                return card;
            card = next;
        }
    }

    private static int Base64Length(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)).Length;

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 4)]
    [InlineData("abc", 4)]
    [InlineData("abcd", 8)]
    [InlineData("\u00e6\u00f8", 8)] // four UTF-8 bytes
    public void EncodedSize_IsTheBase64LengthOfTheUtf8(string json, int expected)
    {
        Assert.Equal(expected, NotificationQueue.EncodedSize(json));
        Assert.Equal(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).Length, expected);
    }

    [Fact]
    public async Task Enqueue_AMessageAtTheLimit_IsQueued()
    {
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Response<SendReceipt>>());
        var message = MessageOfEncodedSize(NotificationQueue.MaxEncodedMessageBytes);

        await _queue.EnqueueAsync(message);

        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Enqueue_AMessageOverTheLimit_Throws_BeforeRecordingOrQueuingAnything()
    {
        var message = MessageOfEncodedSize(NotificationQueue.MaxEncodedMessageBytes + 4);

        var ex = await Assert.ThrowsAsync<QueueMessageTooLargeException>(() => _queue.EnqueueAsync(message));

        Assert.Equal(NotificationQueue.MaxEncodedMessageBytes + 4, ex.EncodedBytes);
        _records.VerifyNoOtherCalls();
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
        _events.VerifyNoOtherCalls();
    }

    /// <summary>A text message whose queue message, Base64-encoded, is exactly <paramref name="encoded"/> bytes.</summary>
    private static QueueMessage MessageOfEncodedSize(int encoded)
    {
        var message = new QueueMessage { MessageId = "msg-7", Message = "" };
        var empty = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(message, NotificationQueue.EnvelopeOptions));
        message.Message = new string('x', encoded / 4 * 3 - empty);
        Assert.Equal(encoded, NotificationQueue.EncodedSize(JsonSerializer.Serialize(message, NotificationQueue.EnvelopeOptions)));
        return message;
    }

    [Fact]
    public async Task Enqueue_WhenQueueFails_RemovesTheRecord_Throws_AndEmitsNoQueuedEvent()
    {
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ThrowsAsync(new RequestFailedException(503, "unavailable"));

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            _queue.EnqueueAsync(new QueueMessage { MessageId = "msg-2" }));

        _records.Verify(r => r.DeleteAsync("msg-2", It.IsAny<Azure.ETag>()), Times.Once);
        _events.Verify(e => e.Queued(It.IsAny<QueueMessage>()), Times.Never);
    }

    [Fact]
    public async Task Enqueue_WhenTheRecordCannotBeWritten_QueuesNothing()
    {
        _records.Setup(r => r.CreateAsync(It.IsAny<QueueMessage>()))
            .ThrowsAsync(new RequestFailedException(503, "unavailable"));

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            _queue.EnqueueAsync(new QueueMessage { MessageId = "msg-3" }));

        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Requeue_SendsWithTheDelay_WithoutANewRecordOrEvent()
    {
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), default))
            .ReturnsAsync(Mock.Of<Response<SendReceipt>>());

        await _queue.RequeueAsync(new QueueMessage { MessageId = "msg-4" }, TimeSpan.FromSeconds(20));

        _queueClient.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => s.Contains("msg-4")), TimeSpan.FromSeconds(20), It.IsAny<TimeSpan?>(), default), Times.Once);
        _records.VerifyNoOtherCalls();
        _events.VerifyNoOtherCalls();
    }
}
