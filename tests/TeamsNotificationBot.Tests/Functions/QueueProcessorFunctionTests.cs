using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

// Joins the "Azurite" collection ONLY to serialize with the other classes that read or mutate
// the process-global TEAMS_INTEGRATION_DISABLED env var — this class sets it mid-test, which
// races against parallel classes that construct BotService (its ctor captures the var).
[Collection("Azurite")]
public class QueueProcessorFunctionTests : IDisposable
{
    private readonly Mock<IBotService> _botService = new();
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly Mock<IDeliveryEvents> _events = new();
    private readonly QueueProcessorFunction _function;

    public QueueProcessorFunctionTests()
    {
        _function = new QueueProcessorFunction(
            _botService.Object,
            _aliasService.Object,
            _events.Object,
            NullLogger<QueueProcessorFunction>.Instance);

        // Clean env
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", null);
    }

    private static string CreateQueueMessageJson(string format = "text", string message = "Hello", string alias = "test")
    {
        var queueMessage = new QueueMessage
        {
            MessageId = "msg-test-123",
            Alias = alias,
            Message = message,
            Format = format,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        return JsonSerializer.Serialize(queueMessage);
    }

    [Fact]
    public async Task T4_TextMessage_CallsSendMessageAsync()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });

        var messageJson = CreateQueueMessageJson(format: "text", message: "Hello World");

        await _function.Run(messageJson, _functionContext.Object);

        _botService.Verify(b => b.SendMessageAsync("team-1", "channel-1", "Hello World"), Times.Once);
    }

    [Fact]
    public async Task T5_AdaptiveCard_CallsSendAdaptiveCardAsync()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });

        var cardJson = """{"type":"AdaptiveCard","version":"1.4","body":[{"type":"TextBlock","text":"Hi"}]}""";
        var messageJson = CreateQueueMessageJson(format: "adaptive-card", message: cardJson);

        await _function.Run(messageJson, _functionContext.Object);

        _botService.Verify(b => b.SendAdaptiveCardAsync(
            "team-1", "channel-1",
            It.IsAny<JsonElement>()), Times.Once);
    }

    [Fact]
    public async Task T6_DeliveryFailure_RethrowsException()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });

        _botService.Setup(b => b.SendMessageAsync("team-1", "channel-1", It.IsAny<string>()))
            .ThrowsAsync(new Exception("Delivery failed"));

        var messageJson = CreateQueueMessageJson();

        var ex = await Assert.ThrowsAsync<Exception>(() =>
            _function.Run(messageJson, _functionContext.Object));

        Assert.Equal("Delivery failed", ex.Message);
    }

    [Fact]
    public async Task TeamsDisabled_SkipsSending()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", "true");
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });

        var messageJson = CreateQueueMessageJson();

        await _function.Run(messageJson, _functionContext.Object);

        _botService.Verify(b => b.SendMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task InvalidJson_ReturnsWithoutProcessing()
    {
        await _function.Run("not-json{{{", _functionContext.Object);

        _botService.Verify(b => b.SendMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UnknownAlias_ReturnsWithoutProcessing()
    {
        _aliasService.Setup(s => s.GetAliasAsync("unknown")).ReturnsAsync((AliasEntity?)null);

        var messageJson = CreateQueueMessageJson(alias: "unknown");

        await _function.Run(messageJson, _functionContext.Object);

        _botService.Verify(b => b.SendMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // --- Delivery events ---

    private void SetDequeueCount(long count)
    {
        var bindingContext = new Mock<BindingContext>();
        bindingContext.Setup(b => b.BindingData).Returns(
            new Dictionary<string, object?> { ["DequeueCount"] = count.ToString() });
        _functionContext.Setup(c => c.BindingContext).Returns(bindingContext.Object);
    }

    [Fact]
    public async Task Delivered_EmitsDeliveredEvent_WithConversationAndDequeueCount()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });
        SetDequeueCount(2);

        await _function.Run(CreateQueueMessageJson(), _functionContext.Object);

        _events.Verify(e => e.Delivered(
            It.Is<QueueMessage>(m => m.MessageId == "msg-test-123"), "team-1", "channel-1", 2), Times.Once);
        _events.Verify(e => e.DeliveryFailed(
            It.IsAny<QueueMessage>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeliveryFailure_EmitsFailedEvent_ThenRethrows()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });
        _botService.Setup(b => b.SendMessageAsync("team-1", "channel-1", It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("No conversation reference"));
        SetDequeueCount(5);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _function.Run(CreateQueueMessageJson(), _functionContext.Object));

        _events.Verify(e => e.DeliveryFailed(
            It.IsAny<QueueMessage>(), 5, "InvalidOperationException", "No conversation reference"), Times.Once);
        _events.Verify(e => e.Delivered(
            It.IsAny<QueueMessage>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task UnknownAlias_EmitsFailedEvent_BecauseTheMessageIsDropped()
    {
        _aliasService.Setup(s => s.GetAliasAsync("gone")).ReturnsAsync((AliasEntity?)null);

        await _function.Run(CreateQueueMessageJson(alias: "gone"), _functionContext.Object);

        _events.Verify(e => e.DeliveryFailed(
            It.IsAny<QueueMessage>(), It.IsAny<long>(), "UnknownAlias", It.Is<string>(s => s.Contains("gone"))), Times.Once);
    }

    [Fact]
    public async Task AliasLookupFailure_EmitsFailedEvent_ThenRethrows()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test"))
            .ThrowsAsync(new Azure.RequestFailedException(503, "storage unavailable"));

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() =>
            _function.Run(CreateQueueMessageJson(), _functionContext.Object));

        _events.Verify(e => e.DeliveryFailed(
            It.IsAny<QueueMessage>(), It.IsAny<long>(), "RequestFailedException", "storage unavailable"), Times.Once);
    }

    [Fact]
    public async Task MalformedAlias_EmitsFailedEvent_ThenRethrows()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { RowKey = "test", TargetType = "channel", TeamId = "team-1" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _function.Run(CreateQueueMessageJson(), _functionContext.Object));

        _events.Verify(e => e.DeliveryFailed(
            It.IsAny<QueueMessage>(), It.IsAny<long>(), "InvalidOperationException", It.IsAny<string>()), Times.Once);
        _botService.Verify(b => b.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UnknownAlias_EmitsExactlyOneFailedEvent()
    {
        _aliasService.Setup(s => s.GetAliasAsync("gone")).ReturnsAsync((AliasEntity?)null);

        await _function.Run(CreateQueueMessageJson(alias: "gone"), _functionContext.Object);

        _events.Verify(e => e.DeliveryFailed(
            It.IsAny<QueueMessage>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task TeamsDisabled_EmitsNoDeliveryEvent()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", "true");
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });

        await _function.Run(CreateQueueMessageJson(), _functionContext.Object);

        _events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingBindingData_ReportsDequeueCountZero()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });

        await _function.Run(CreateQueueMessageJson(), _functionContext.Object);

        _events.Verify(e => e.Delivered(It.IsAny<QueueMessage>(), "team-1", "channel-1", 0), Times.Once);
    }

    [Fact]
    public async Task DirectTarget_Channel_CallsSendMessageAsync()
    {
        var queueMessage = new QueueMessage
        {
            MessageId = "msg-direct-1",
            Target = new MessageTarget { Type = "channel", TeamId = "team-1", ChannelId = "channel-1" },
            Message = "Direct message",
            Format = "text",
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        var messageJson = JsonSerializer.Serialize(queueMessage);

        await _function.Run(messageJson, _functionContext.Object);

        _botService.Verify(b => b.SendMessageAsync("team-1", "channel-1", "Direct message"), Times.Once);
    }

    [Fact]
    public async Task DirectTarget_Personal_CallsSendMessageAsync()
    {
        var queueMessage = new QueueMessage
        {
            MessageId = "msg-direct-2",
            Target = new MessageTarget { Type = "personal", UserId = "user-abc" },
            Message = "Personal message",
            Format = "text",
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        var messageJson = JsonSerializer.Serialize(queueMessage);

        await _function.Run(messageJson, _functionContext.Object);

        _botService.Verify(b => b.SendMessageAsync("user", "user-abc", "Personal message"), Times.Once);
    }
}
