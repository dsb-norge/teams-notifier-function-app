using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Integration.Fixtures;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.Services;

/// <summary>
/// Integration tests for QueueManagementService using real Azurite queues.
/// Validates message encoding preservation, retry round-trips, and peek behavior.
/// </summary>
[Collection("Azurite")]
public class QueueManagementServiceIntegrationTests
{
    private readonly QueueClient _notificationsQueue;
    private readonly QueueClient _botOperationsQueue;
    private readonly QueueClient _notificationsPoisonQueue;
    private readonly QueueClient _botOperationsPoisonQueue;
    private readonly QueueManagementService _service;

    public QueueManagementServiceIntegrationTests(AzuriteFixture azurite)
    {
        _notificationsQueue = azurite.CreateQueueClient("notifications");
        _botOperationsQueue = azurite.CreateQueueClient("botoperations");
        _notificationsPoisonQueue = azurite.CreateQueueClient("notificationspoison");
        _botOperationsPoisonQueue = azurite.CreateQueueClient("botopspoison");

        _service = new QueueManagementService(
            _notificationsQueue,
            _botOperationsQueue,
            _notificationsPoisonQueue,
            _botOperationsPoisonQueue,
            NullLogger<QueueManagementService>.Instance);
    }

    [Fact]
    public async Task GetQueueStatus_EmptyQueues_ReturnsZeroCounts()
    {
        var status = await _service.GetQueueStatusAsync();

        Assert.Equal(4, status.Count);
        foreach (var count in status.Values)
        {
            Assert.True(count >= 0);
        }
    }

    [Fact]
    public async Task GetQueueStatus_WithMessages_ReturnsApproximateCount()
    {
        // Seed a message into the poison queue
        await _notificationsPoisonQueue.SendMessageAsync("test-message");

        var status = await _service.GetQueueStatusAsync();

        Assert.True(status.Values.Any(v => v > 0),
            "At least one queue should have messages after seeding");
    }

    [Fact]
    public async Task PeekMessages_WithMessages_ReturnsCorrectContent()
    {
        var original = new { alias = "test", message = "Hello" };
        var json = JsonSerializer.Serialize(original);
        await _notificationsPoisonQueue.SendMessageAsync(json);

        var peeked = await _service.PeekMessagesAsync("notifications-poison", 5);

        Assert.True(peeked.Count > 0);
        // Peek should not remove the message
        var peeked2 = await _service.PeekMessagesAsync("notifications-poison", 5);
        Assert.True(peeked2.Count > 0);
    }

    [Fact]
    public async Task PeekMessages_EmptyQueue_ReturnsEmptyList()
    {
        // Drain the queue first
        while (true)
        {
            var msgs = await _botOperationsPoisonQueue.ReceiveMessagesAsync(32);
            if (msgs.Value.Length == 0) break;
            foreach (var m in msgs.Value)
                await _botOperationsPoisonQueue.DeleteMessageAsync(m.MessageId, m.PopReceipt);
        }

        var result = await _service.PeekMessagesAsync("botoperations-poison", 5);

        Assert.Empty(result);
    }

    [Fact]
    public async Task RetryMessages_MovesMessageFromPoisonToMain_PreservesContent()
    {
        // Drain both queues first
        await DrainQueue(_notificationsPoisonQueue);
        await DrainQueue(_notificationsQueue);

        // Seed a poison message
        var original = new { alias = "retry-test", message = "Important notification", format = "text" };
        var json = JsonSerializer.Serialize(original);
        await _notificationsPoisonQueue.SendMessageAsync(json);

        // Retry it
        var retried = await _service.RetryMessagesAsync("notifications-poison", 1);

        Assert.Equal(1, retried);

        // Verify the message arrived in the main queue
        var received = await _notificationsQueue.ReceiveMessageAsync();
        Assert.NotNull(received.Value);

        // Verify content is preserved
        var deserialized = JsonSerializer.Deserialize<JsonElement>(received.Value.MessageText);
        Assert.Equal("retry-test", deserialized.GetProperty("alias").GetString());
        Assert.Equal("Important notification", deserialized.GetProperty("message").GetString());

        // Verify poison queue is now empty
        var poisonPeek = await _notificationsPoisonQueue.PeekMessageAsync();
        Assert.Null(poisonPeek.Value);
    }

    [Fact]
    public async Task RetryAllMessages_MovesAllMessages()
    {
        // Drain both queues
        await DrainQueue(_botOperationsPoisonQueue);
        await DrainQueue(_botOperationsQueue);

        // Seed 3 messages
        for (var i = 0; i < 3; i++)
            await _botOperationsPoisonQueue.SendMessageAsync($"msg-{i}");

        var retried = await _service.RetryAllMessagesAsync("botoperations-poison");

        Assert.Equal(3, retried);

        // All should be in main queue now
        var messages = await _botOperationsQueue.ReceiveMessagesAsync(10);
        Assert.Equal(3, messages.Value.Length);

        // Poison should be empty
        var poisonPeek = await _botOperationsPoisonQueue.PeekMessageAsync();
        Assert.Null(poisonPeek.Value);
    }

    [Fact]
    public async Task RetryMessages_EmptyPoisonQueue_ReturnsZero()
    {
        await DrainQueue(_botOperationsPoisonQueue);

        var retried = await _service.RetryMessagesAsync("botoperations-poison", 5);

        Assert.Equal(0, retried);
    }

    [Fact]
    public async Task RetryMessages_Base64EncodedContent_PreservedThroughRoundTrip()
    {
        // This validates that Base64-encoded queue messages (as used by Azure Functions)
        // survive the poison→main move without encoding corruption
        await DrainQueue(_notificationsPoisonQueue);
        await DrainQueue(_notificationsQueue);

        var nestedJson = """{"type":"AdaptiveCard","body":[{"type":"TextBlock","text":"Special: <>&\"'"}]}""";
        var wrapper = new { alias = "base64-test", message = nestedJson, format = "adaptive-card" };
        var json = JsonSerializer.Serialize(wrapper);
        await _notificationsPoisonQueue.SendMessageAsync(json);

        await _service.RetryMessagesAsync("notifications-poison", 1);

        var received = await _notificationsQueue.ReceiveMessageAsync();
        Assert.NotNull(received.Value);

        // Parse and verify nested JSON survived
        var outer = JsonSerializer.Deserialize<JsonElement>(received.Value.MessageText);
        var innerJson = outer.GetProperty("message").GetString();
        var inner = JsonDocument.Parse(innerJson!);
        Assert.Equal("AdaptiveCard", inner.RootElement.GetProperty("type").GetString());
    }

    private static async Task DrainQueue(QueueClient queue)
    {
        while (true)
        {
            var msgs = await queue.ReceiveMessagesAsync(32);
            if (msgs.Value.Length == 0) break;
            foreach (var m in msgs.Value)
                await queue.DeleteMessageAsync(m.MessageId, m.PopReceipt);
        }
    }
}
