using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Agents.Core.Models;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Tests.Integration.Fixtures;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.Storage;

[Collection("Azurite")]
public class QueueMessageRoundTripTests
{
    private readonly QueueClient _queueClient;

    public QueueMessageRoundTripTests(AzuriteFixture azurite)
    {
        _queueClient = azurite.CreateQueueClient("notifications");
    }

    [Fact]
    public async Task QueueMessage_TextFormat_RoundTrips()
    {
        var original = new QueueMessage
        {
            MessageId = "msg-001",
            Alias = "test-alias",
            Message = "Hello from integration test",
            Format = "text",
            EnqueuedAt = new DateTimeOffset(2026, 2, 25, 12, 0, 0, TimeSpan.Zero),
            Metadata = new Dictionary<string, string> { ["source"] = "test" }
        };

        var json = JsonSerializer.Serialize(original);
        await _queueClient.SendMessageAsync(json);

        var received = await _queueClient.ReceiveMessageAsync();
        Assert.NotNull(received.Value);

        var deserialized = JsonSerializer.Deserialize<QueueMessage>(received.Value.MessageText);
        Assert.NotNull(deserialized);
        Assert.Equal("msg-001", deserialized.MessageId);
        Assert.Equal("test-alias", deserialized.Alias);
        Assert.Equal("Hello from integration test", deserialized.Message);
        Assert.Equal("text", deserialized.Format);
        Assert.Equal("test", deserialized.Metadata?["source"]);
    }

    [Fact]
    public async Task QueueMessage_AdaptiveCard_NestedJsonSurvives()
    {
        var cardJson = """
        {
            "type": "AdaptiveCard",
            "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
            "version": "1.5",
            "body": [
                {"type": "TextBlock", "text": "Hello \"World\"!", "size": "Large"},
                {"type": "TextBlock", "text": "Special chars: <>&'\n\ttabs"}
            ]
        }
        """;

        var original = new QueueMessage
        {
            MessageId = "msg-card-001",
            Alias = "test-channel",
            Message = cardJson,
            Format = "adaptive-card",
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(original);
        await _queueClient.SendMessageAsync(json);

        var received = await _queueClient.ReceiveMessageAsync();
        Assert.NotNull(received.Value);

        var deserialized = JsonSerializer.Deserialize<QueueMessage>(received.Value.MessageText);
        Assert.NotNull(deserialized);
        Assert.Equal("adaptive-card", deserialized.Format);

        // Prove the nested JSON is still valid
        var cardDoc = JsonDocument.Parse(deserialized.Message);
        Assert.Equal("AdaptiveCard", cardDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal("1.5", cardDoc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task BotOperationMessage_WithSerializedReference_RoundTrips()
    {
        var convRef = new ConversationReference
        {
            ServiceUrl = "https://smba.trafficmanager.net/emea/",
            ChannelId = "msteams",
            Conversation = new ConversationAccount
            {
                Id = "19:test@thread.tacv2",
                IsGroup = true,
                ConversationType = "channel",
                TenantId = "tenant-123"
            }
        };

        var original = new BotOperationMessage
        {
            Operation = "enumerate_channels",
            TeamGuid = "team-guid-123",
            TeamName = "Test Team",
            TeamThreadId = "19:team-thread@thread.tacv2",
            SerializedReference = JsonSerializer.Serialize(convRef),
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(original);
        await _queueClient.SendMessageAsync(json);

        var received = await _queueClient.ReceiveMessageAsync();
        Assert.NotNull(received.Value);

        var deserialized = JsonSerializer.Deserialize<BotOperationMessage>(received.Value.MessageText);
        Assert.NotNull(deserialized);
        Assert.Equal("enumerate_channels", deserialized.Operation);
        Assert.Equal("team-guid-123", deserialized.TeamGuid);

        // Prove JSON-in-JSON survives Base64 queue round-trip
        var innerRef = JsonSerializer.Deserialize<ConversationReference>(
            deserialized.SerializedReference!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(innerRef);
        Assert.Equal("19:test@thread.tacv2", innerRef.Conversation?.Id);
        Assert.Equal("tenant-123", innerRef.Conversation?.TenantId);
    }
}
