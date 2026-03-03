using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class QueueManagementServiceTests
{
    private readonly Mock<QueueClient> _notificationsQueue = new();
    private readonly Mock<QueueClient> _botOperationsQueue = new();
    private readonly Mock<QueueClient> _notificationsPoisonQueue = new();
    private readonly Mock<QueueClient> _botOperationsPoisonQueue = new();
    private readonly QueueManagementService _service;

    public QueueManagementServiceTests()
    {
        _service = new QueueManagementService(
            _notificationsQueue.Object,
            _botOperationsQueue.Object,
            _notificationsPoisonQueue.Object,
            _botOperationsPoisonQueue.Object,
            NullLogger<QueueManagementService>.Instance);
    }

    // --- GetQueueStatusAsync ---

    [Fact]
    public async Task GetQueueStatus_ReturnsCountsForAllQueues()
    {
        SetupQueueProperties(_notificationsQueue, 5);
        SetupQueueProperties(_botOperationsQueue, 3);
        SetupQueueProperties(_notificationsPoisonQueue, 2);
        SetupQueueProperties(_botOperationsPoisonQueue, 0);

        var result = await _service.GetQueueStatusAsync();

        Assert.Equal(4, result.Count);
        Assert.Equal(5, result["notifications"]);
        Assert.Equal(3, result["botoperations"]);
        Assert.Equal(2, result["notifications-poison"]);
        Assert.Equal(0, result["botoperations-poison"]);
    }

    [Fact]
    public async Task GetQueueStatus_OnError_ReturnsMinusOne()
    {
        SetupQueueProperties(_notificationsQueue, 5);
        SetupQueueProperties(_botOperationsQueue, 3);
        _notificationsPoisonQueue.Setup(q => q.GetPropertiesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException("error"));
        SetupQueueProperties(_botOperationsPoisonQueue, 0);

        var result = await _service.GetQueueStatusAsync();

        Assert.Equal(-1, result["notifications-poison"]);
        Assert.Equal(5, result["notifications"]);
    }

    // --- PeekMessagesAsync ---

    [Fact]
    public async Task PeekMessages_NotificationsPoisonQueue_ReturnsPeekedMessages()
    {
        var peekedMessages = CreatePeekedMessages(3);
        _notificationsPoisonQueue.Setup(q => q.PeekMessagesAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(peekedMessages, Mock.Of<Response>()));

        var result = await _service.PeekMessagesAsync("notifications-poison", 3);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task PeekMessages_CapsAt32()
    {
        var peekedMessages = CreatePeekedMessages(32);
        _notificationsPoisonQueue.Setup(q => q.PeekMessagesAsync(32, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(peekedMessages, Mock.Of<Response>()));

        await _service.PeekMessagesAsync("notifications-poison", 100);

        _notificationsPoisonQueue.Verify(q => q.PeekMessagesAsync(32, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PeekMessages_InvalidQueueName_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.PeekMessagesAsync("invalid-queue", 5));
    }

    // --- RetryMessagesAsync ---

    [Fact]
    public async Task RetryMessages_MovesFromPoisonToMain()
    {
        var receivedMessages = CreateReceivedMessages(2);
        _notificationsPoisonQueue.Setup(q => q.ReceiveMessagesAsync(1, TimeSpan.FromSeconds(30), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReceiveResponse(receivedMessages));

        var result = await _service.RetryMessagesAsync("notifications-poison", 2);

        Assert.Equal(2, result);
        _notificationsQueue.Verify(q => q.SendMessageAsync(
            It.IsAny<string>()),
            Times.Exactly(2));
        _notificationsPoisonQueue.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RetryMessages_EmptyQueue_ReturnsZero()
    {
        _notificationsPoisonQueue.Setup(q => q.ReceiveMessagesAsync(1, TimeSpan.FromSeconds(30), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReceiveResponse([]));

        var result = await _service.RetryMessagesAsync("notifications-poison", 5);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task RetryMessages_BotOperationsPoisonQueue_UsesCorrectClients()
    {
        var receivedMessages = CreateReceivedMessages(1);
        _botOperationsPoisonQueue.Setup(q => q.ReceiveMessagesAsync(1, TimeSpan.FromSeconds(30), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReceiveResponse(receivedMessages));

        await _service.RetryMessagesAsync("botoperations-poison", 1);

        _botOperationsQueue.Verify(q => q.SendMessageAsync(
            It.IsAny<string>()),
            Times.Once);
        _botOperationsPoisonQueue.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetryMessages_InvalidQueueName_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.RetryMessagesAsync("invalid-queue", 1));
    }

    // --- RetryAllMessagesAsync ---

    [Fact]
    public async Task RetryAll_ProcessesAllMessages()
    {
        var batch1 = CreateReceivedMessages(2);
        var callCount = 0;
        _notificationsPoisonQueue.Setup(q => q.ReceiveMessagesAsync(32, TimeSpan.FromSeconds(30), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? CreateReceiveResponse(batch1)
                    : CreateReceiveResponse([]);
            });

        var result = await _service.RetryAllMessagesAsync("notifications-poison");

        Assert.Equal(2, result);
        _notificationsQueue.Verify(q => q.SendMessageAsync(
            It.IsAny<string>()),
            Times.Exactly(2));
    }

    // --- Helpers ---

    private static void SetupQueueProperties(Mock<QueueClient> mock, int messageCount)
    {
        var properties = QueuesModelFactory.QueueProperties(
            metadata: new Dictionary<string, string>(),
            approximateMessagesCount: messageCount);
        mock.Setup(q => q.GetPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(properties, Mock.Of<Response>()));
    }

    private static PeekedMessage[] CreatePeekedMessages(int count)
    {
        var messages = new PeekedMessage[count];
        for (var i = 0; i < count; i++)
        {
            messages[i] = QueuesModelFactory.PeekedMessage(
                messageId: $"msg-{i}",
                messageText: $"{{\"test\":{i}}}",
                dequeueCount: 0);
        }
        return messages;
    }

    private static QueueMessage[] CreateReceivedMessages(int count)
    {
        var messages = new QueueMessage[count];
        for (var i = 0; i < count; i++)
        {
            messages[i] = QueuesModelFactory.QueueMessage(
                messageId: $"msg-{i}",
                popReceipt: $"pop-{i}",
                body: BinaryData.FromString($"{{\"test\":{i}}}"),
                dequeueCount: 1);
        }
        return messages;
    }

    private static Response<QueueMessage[]> CreateReceiveResponse(QueueMessage[] messages)
    {
        return Response.FromValue(messages, Mock.Of<Response>());
    }
}
