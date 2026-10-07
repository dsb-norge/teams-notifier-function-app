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
    private const string ChannelConversation = "19:channel-1@thread.tacv2";

    private readonly Mock<IBotService> _botService = new();
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<IDeliveryRecords> _records = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly Mock<IDeliveryEvents> _events = new();
    private readonly QueueProcessorFunction _function;

    public QueueProcessorFunctionTests()
    {
        _function = new QueueProcessorFunction(
            _botService.Object,
            _aliasService.Object,
            _records.Object,
            _events.Object,
            NullLogger<QueueProcessorFunction>.Instance);

        // Every send lands; tests that need a failure override this.
        _botService
            .Setup(b => b.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((string _, string _, string _, string _, string? thread) =>
                new SentActivity(thread == null ? ChannelConversation : $"{ChannelConversation};messageid={thread}", "activity-new"));

        // Clean env
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", null);
    }

    private void SetUpChannelAlias(string name = "test") =>
        _aliasService.Setup(s => s.GetAliasAsync(name)).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });

    private static QueueMessage NewMessage(string format = "text", string message = "Hello", string alias = "test") => new()
    {
        MessageId = "msg-test-123",
        Alias = alias,
        Message = message,
        Format = format,
        EnqueuedAt = DateTimeOffset.UtcNow
    };

    private static string CreateQueueMessageJson(string format = "text", string message = "Hello", string alias = "test") =>
        JsonSerializer.Serialize(NewMessage(format, message, alias));

    private Task RunAsync(QueueMessage message) => _function.Run(JsonSerializer.Serialize(message), _functionContext.Object);

    private void SetUpRecord(DeliveryRecordEntity record) =>
        _records.Setup(r => r.GetAsync(record.PartitionKey)).ReturnsAsync(record);

    // --- Plain posts ---

    [Fact]
    public async Task T4_TextMessage_IsSentToTheAliasConversation()
    {
        SetUpChannelAlias();

        await _function.Run(CreateQueueMessageJson(format: "text", message: "Hello World"), _functionContext.Object);

        _botService.Verify(b => b.SendAsync("team-1", "channel-1", "text", "Hello World", null), Times.Once);
    }

    [Fact]
    public async Task T5_AdaptiveCard_IsSentAsACard()
    {
        SetUpChannelAlias();
        var cardJson = """{"type":"AdaptiveCard","version":"1.4","body":[{"type":"TextBlock","text":"Hi"}]}""";

        await _function.Run(CreateQueueMessageJson(format: "adaptive-card", message: cardJson), _functionContext.Object);

        _botService.Verify(b => b.SendAsync("team-1", "channel-1", "adaptive-card", cardJson, null), Times.Once);
    }

    [Fact]
    public async Task Post_IsRecordedAsDelivered_WithItsActivityAsThreadRoot()
    {
        SetUpChannelAlias();
        DeliveryOutcome? outcome = null;
        _records.Setup(r => r.MarkDeliveredAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryOutcome>()))
            .Callback<QueueMessage, DeliveryOutcome>((_, o) => outcome = o);

        await RunAsync(NewMessage());

        Assert.NotNull(outcome);
        Assert.Equal(PostedAs.Post, outcome.PostedAs);
        Assert.Equal(("team-1", "channel-1"), outcome.ConversationKey);
        Assert.Equal(ChannelConversation, outcome.ConversationId);
        Assert.Equal("activity-new", outcome.ActivityId);
        Assert.Equal("activity-new", outcome.ThreadActivityId);
    }

    [Fact]
    public async Task AlreadyDelivered_IsSkipped_SoAQueueRetryDoesNotPostTwice()
    {
        SetUpChannelAlias();
        SetUpRecord(new DeliveryRecordEntity { PartitionKey = "msg-test-123", Status = DeliveryStatus.Delivered });

        await RunAsync(NewMessage());

        _botService.VerifyNoOtherCalls();
        _records.Verify(r => r.MarkDeliveredAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryOutcome>()), Times.Never);
    }

    [Fact]
    public async Task PreviouslyFailed_IsDelivered_WhenRetriedFromThePoisonQueue()
    {
        SetUpChannelAlias();
        SetUpRecord(new DeliveryRecordEntity { PartitionKey = "msg-test-123", Status = DeliveryStatus.Failed });

        await RunAsync(NewMessage());

        _botService.Verify(b => b.SendAsync("team-1", "channel-1", "text", "Hello", null), Times.Once);
    }

    [Fact]
    public async Task T6_DeliveryFailure_RethrowsException()
    {
        SetUpChannelAlias();
        _botService.Setup(b => b.SendAsync("team-1", "channel-1", It.IsAny<string>(), It.IsAny<string>(), null))
            .ThrowsAsync(new Exception("Delivery failed"));

        var ex = await Assert.ThrowsAsync<Exception>(() => RunAsync(NewMessage()));

        Assert.Equal("Delivery failed", ex.Message);
        _records.Verify(r => r.MarkDeliveredAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryOutcome>()), Times.Never);
        _records.Verify(r => r.MarkFailedAsync(It.IsAny<QueueMessage>(), It.IsAny<string>()), Times.Never); // retried
    }

    [Fact]
    public async Task TeamsDisabled_SkipsSending()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", "true");
        SetUpChannelAlias();

        await RunAsync(NewMessage());

        _botService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvalidJson_ReturnsWithoutProcessing()
    {
        await _function.Run("not-json{{{", _functionContext.Object);

        _botService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnknownAlias_FailsForGood_WithoutRetry()
    {
        _aliasService.Setup(s => s.GetAliasAsync("unknown")).ReturnsAsync((AliasEntity?)null);

        await RunAsync(NewMessage(alias: "unknown"));

        _botService.VerifyNoOtherCalls();
        _records.Verify(r => r.MarkFailedAsync(It.IsAny<QueueMessage>(), It.Is<string>(e => e.Contains("unknown"))), Times.Once);
    }

    [Fact]
    public async Task DirectTarget_Channel_IsSentToTheTarget()
    {
        var queueMessage = new QueueMessage
        {
            MessageId = "msg-direct-1",
            Target = new MessageTarget { Type = "channel", TeamId = "team-1", ChannelId = "channel-1" },
            Message = "Direct message",
            Format = "text",
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        await RunAsync(queueMessage);

        _botService.Verify(b => b.SendAsync("team-1", "channel-1", "text", "Direct message", null), Times.Once);
    }

    [Fact]
    public async Task DirectTarget_Personal_IsSentToTheUser()
    {
        var queueMessage = new QueueMessage
        {
            MessageId = "msg-direct-2",
            Target = new MessageTarget { Type = "personal", UserId = "user-abc" },
            Message = "Personal message",
            Format = "text",
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        await RunAsync(queueMessage);

        _botService.Verify(b => b.SendAsync("user", "user-abc", "text", "Personal message", null), Times.Once);
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
        SetUpChannelAlias();
        SetDequeueCount(2);

        await RunAsync(NewMessage());

        _events.Verify(e => e.Delivered(
            It.Is<QueueMessage>(m => m.MessageId == "msg-test-123"), "team-1", "channel-1", 2), Times.Once);
        _events.Verify(e => e.DeliveryFailed(
            It.IsAny<QueueMessage>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeliveryFailure_EmitsFailedEvent_ThenRethrows()
    {
        SetUpChannelAlias();
        _botService.Setup(b => b.SendAsync("team-1", "channel-1", It.IsAny<string>(), It.IsAny<string>(), null))
            .ThrowsAsync(new InvalidOperationException("No conversation reference"));
        SetDequeueCount(5);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(NewMessage()));

        _events.Verify(e => e.DeliveryFailed(
            It.IsAny<QueueMessage>(), 5, "InvalidOperationException", "No conversation reference"), Times.Once);
        _events.Verify(e => e.Delivered(
            It.IsAny<QueueMessage>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task UnknownAlias_EmitsFailedEvent_BecauseTheMessageIsDropped()
    {
        _aliasService.Setup(s => s.GetAliasAsync("gone")).ReturnsAsync((AliasEntity?)null);

        await RunAsync(NewMessage(alias: "gone"));

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
            It.IsAny<QueueMessage>(), It.IsAny<long>(), "InvalidOperationException", It.Is<string>(m => m.Contains("no valid target"))), Times.Once);
        _botService.Verify(b => b.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
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
        SetUpChannelAlias();

        await RunAsync(NewMessage());

        _events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingBindingData_ReportsDequeueCountZero()
    {
        SetUpChannelAlias();

        await RunAsync(NewMessage());

        _events.Verify(e => e.Delivered(It.IsAny<QueueMessage>(), "team-1", "channel-1", 0), Times.Once);
    }
}
