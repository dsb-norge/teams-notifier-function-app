using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

// Tests mutate Environment.SetEnvironmentVariable — must not run in parallel
[Collection("PoisonQueueMonitor")]
public class PoisonQueueMonitorFunctionTests : IDisposable
{
    private readonly Mock<IBotService> _botService = new();
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly PoisonQueueMonitorFunction _function;

    public PoisonQueueMonitorFunctionTests()
    {
        _function = new PoisonQueueMonitorFunction(
            _botService.Object,
            _aliasService.Object,
            NullLogger<PoisonQueueMonitorFunction>.Instance);

        Environment.SetEnvironmentVariable("PoisonAlertAlias", "alert-channel");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PoisonAlertAlias", null);
    }

    [Fact]
    public async Task NotificationsPoisonTrigger_SendsAlertCard()
    {
        _aliasService.Setup(s => s.GetAliasAsync("alert-channel")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "t1", ChannelId = "c1" });

        var messageJson = """{"alias":"test","message":"Hello"}""";

        await _function.RunNotifications(messageJson, _functionContext.Object);

        _botService.Verify(b => b.SendAdaptiveCardAsync("t1", "c1", It.IsAny<JsonElement>()), Times.Once);
    }

    [Fact]
    public async Task BotOperationsPoisonTrigger_SendsAlertCard()
    {
        _aliasService.Setup(s => s.GetAliasAsync("alert-channel")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "t1", ChannelId = "c1" });

        var messageJson = """{"operation":"send"}""";

        await _function.RunBotOperations(messageJson, _functionContext.Object);

        _botService.Verify(b => b.SendAdaptiveCardAsync("t1", "c1", It.IsAny<JsonElement>()), Times.Once);
    }

    [Fact]
    public async Task PersonalAlias_ResolvesToUserTarget()
    {
        _aliasService.Setup(s => s.GetAliasAsync("alert-channel")).ReturnsAsync(
            new AliasEntity { TargetType = "personal", UserId = "user-123" });

        await _function.RunNotifications("""{"test":true}""", _functionContext.Object);

        _botService.Verify(b => b.SendAdaptiveCardAsync("user", "user-123", It.IsAny<JsonElement>()), Times.Once);
    }

    [Fact]
    public async Task GroupChatAlias_ResolvesToChatTarget()
    {
        _aliasService.Setup(s => s.GetAliasAsync("alert-channel")).ReturnsAsync(
            new AliasEntity { TargetType = "groupChat", ChatId = "chat-abc" });

        await _function.RunNotifications("""{"test":true}""", _functionContext.Object);

        _botService.Verify(b => b.SendAdaptiveCardAsync("chat", "chat-abc", It.IsAny<JsonElement>()), Times.Once);
    }

    [Fact]
    public async Task NoPoisonAlertAlias_SkipsSilently()
    {
        Environment.SetEnvironmentVariable("PoisonAlertAlias", null);

        await _function.RunNotifications("""{"test":true}""", _functionContext.Object);

        _botService.Verify(b => b.SendAdaptiveCardAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>()), Times.Never);
    }

    [Fact]
    public async Task AliasNotFound_SkipsSilently()
    {
        _aliasService.Setup(s => s.GetAliasAsync("alert-channel")).ReturnsAsync((AliasEntity?)null);

        await _function.RunNotifications("""{"test":true}""", _functionContext.Object);

        _botService.Verify(b => b.SendAdaptiveCardAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>()), Times.Never);
    }

    [Fact]
    public async Task SendFailure_DoesNotThrow()
    {
        _aliasService.Setup(s => s.GetAliasAsync("alert-channel")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "t1", ChannelId = "c1" });
        _botService.Setup(b => b.SendAdaptiveCardAsync("t1", "c1", It.IsAny<JsonElement>()))
            .ThrowsAsync(new Exception("Send failed"));

        // CRITICAL: Must not throw — prevents *-poison-poison queues
        await _function.RunNotifications("""{"test":true}""", _functionContext.Object);
    }

    [Fact]
    public async Task AliasServiceFailure_DoesNotThrow()
    {
        _aliasService.Setup(s => s.GetAliasAsync("alert-channel"))
            .ThrowsAsync(new Exception("Table storage down"));

        // CRITICAL: Must not throw — prevents *-poison-poison queues
        await _function.RunNotifications("""{"test":true}""", _functionContext.Object);
    }

    [Fact]
    public async Task MalformedMessageJson_DoesNotThrow()
    {
        _aliasService.Setup(s => s.GetAliasAsync("alert-channel")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "t1", ChannelId = "c1" });

        // Malformed JSON — best-effort parse should not crash
        await _function.RunNotifications("not-valid-json{{{", _functionContext.Object);

        // Should still attempt to send alert (with null enqueuedTime)
        _botService.Verify(b => b.SendAdaptiveCardAsync("t1", "c1", It.IsAny<JsonElement>()), Times.Once);
    }

    [Fact]
    public async Task MessageWithEnqueuedAt_ParsesTimestamp()
    {
        _aliasService.Setup(s => s.GetAliasAsync("alert-channel")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "t1", ChannelId = "c1" });

        JsonElement? capturedCard = null;
        _botService.Setup(b => b.SendAdaptiveCardAsync("t1", "c1", It.IsAny<JsonElement>()))
            .Callback<string, string, JsonElement>((_, _, card) => capturedCard = card)
            .Returns(Task.CompletedTask);

        var messageJson = """{"alias":"test","enqueuedAt":"2026-02-27T10:00:00Z"}""";
        await _function.RunNotifications(messageJson, _functionContext.Object);

        Assert.NotNull(capturedCard);
        var cardJson = capturedCard.Value.ToString();
        Assert.Contains("Originally Enqueued", cardJson);
    }

    [Fact]
    public async Task UnknownAliasTargetType_DoesNotThrow()
    {
        _aliasService.Setup(s => s.GetAliasAsync("alert-channel")).ReturnsAsync(
            new AliasEntity { TargetType = "unknownType", TeamId = "t1", ChannelId = "c1" });

        // ResolveAliasTarget throws InvalidOperationException for unknown type,
        // but the outer try/catch should swallow it
        await _function.RunNotifications("""{"test":true}""", _functionContext.Object);

        _botService.Verify(b => b.SendAdaptiveCardAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>()), Times.Never);
    }

    [Fact]
    public async Task EmptyPoisonAlertAlias_SkipsSilently()
    {
        Environment.SetEnvironmentVariable("PoisonAlertAlias", "");

        await _function.RunNotifications("""{"test":true}""", _functionContext.Object);

        _botService.Verify(b => b.SendAdaptiveCardAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>()), Times.Never);
    }

    [Fact]
    public async Task BotOperationsTrigger_MalformedJson_DoesNotThrow()
    {
        _aliasService.Setup(s => s.GetAliasAsync("alert-channel")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "t1", ChannelId = "c1" });

        // Malformed JSON on the botoperations path
        await _function.RunBotOperations("{{bad json}}", _functionContext.Object);

        // Should still attempt to send alert
        _botService.Verify(b => b.SendAdaptiveCardAsync("t1", "c1", It.IsAny<JsonElement>()), Times.Once);
    }

    [Fact]
    public async Task EmptyMessageString_DoesNotThrow()
    {
        _aliasService.Setup(s => s.GetAliasAsync("alert-channel")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "t1", ChannelId = "c1" });

        await _function.RunNotifications("", _functionContext.Object);

        // Should still attempt to send alert
        _botService.Verify(b => b.SendAdaptiveCardAsync("t1", "c1", It.IsAny<JsonElement>()), Times.Once);
    }
}
