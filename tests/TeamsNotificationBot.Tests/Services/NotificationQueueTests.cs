using System.Text.Json;
using Azure;
using Azure.Storage.Queues;
using SendReceipt = Azure.Storage.Queues.Models.SendReceipt;
using Moq;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class NotificationQueueTests
{
    private readonly Mock<QueueClient> _queueClient = new();
    private readonly Mock<IDeliveryEvents> _events = new();

    [Fact]
    public async Task Enqueue_SendsSerializedMessage_ThenEmitsQueuedEvent()
    {
        string? sent = null;
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .Callback<string>(s => sent = s)
            .ReturnsAsync(Mock.Of<Response<SendReceipt>>());
        var message = new QueueMessage { MessageId = "msg-1", Alias = "ops", Message = "hi", Source = "notify" };

        await new NotificationQueue(_queueClient.Object, _events.Object).EnqueueAsync(message);

        var roundTripped = JsonSerializer.Deserialize<QueueMessage>(sent!);
        Assert.Equal("msg-1", roundTripped!.MessageId);
        Assert.Equal("notify", roundTripped.Source);
        _events.Verify(e => e.Queued(message), Times.Once);
    }

    [Fact]
    public async Task Enqueue_WhenQueueFails_Throws_AndEmitsNoQueuedEvent()
    {
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ThrowsAsync(new RequestFailedException(503, "unavailable"));

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            new NotificationQueue(_queueClient.Object, _events.Object).EnqueueAsync(new QueueMessage()));

        _events.Verify(e => e.Queued(It.IsAny<QueueMessage>()), Times.Never);
    }
}
