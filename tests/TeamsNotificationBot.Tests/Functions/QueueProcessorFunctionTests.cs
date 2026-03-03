using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

public class QueueProcessorFunctionTests : IDisposable
{
    private readonly Mock<IBotService> _botService = new();
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly QueueProcessorFunction _function;

    public QueueProcessorFunctionTests()
    {
        _function = new QueueProcessorFunction(
            _botService.Object,
            _aliasService.Object,
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
