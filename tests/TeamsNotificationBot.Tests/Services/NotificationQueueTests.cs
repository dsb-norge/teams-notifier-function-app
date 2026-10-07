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
    public async Task Enqueue_WhenQueueFails_RemovesTheRecord_Throws_AndEmitsNoQueuedEvent()
    {
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ThrowsAsync(new RequestFailedException(503, "unavailable"));

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            _queue.EnqueueAsync(new QueueMessage { MessageId = "msg-2" }));

        _records.Verify(r => r.DeleteAsync("msg-2"), Times.Once);
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
}
