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
    private readonly Mock<INotificationQueue> _queue = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly Mock<IDeliveryEvents> _events = new();
    private readonly QueueProcessorFunction _function;

    public QueueProcessorFunctionTests()
    {
        _function = new QueueProcessorFunction(
            _botService.Object,
            _aliasService.Object,
            _records.Object,
            _queue.Object,
            _events.Object,
            NullLogger<QueueProcessorFunction>.Instance);

        // Every send lands; tests that need a failure override this.
        _botService
            .Setup(b => b.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<MessageMention>?>()))
            .ReturnsAsync((string _, string _, string _, string _, string? thread, IReadOnlyList<MessageMention>? _) =>
                new SentActivity(thread == null ? ChannelConversation : $"{ChannelConversation};messageid={thread}", "activity-new"));

        // Every update lands with no unresolved mentions.
        _botService
            .Setup(b => b.UpdateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MessageMention>?>()))
            .ReturnsAsync([]);

        // Every send claim is granted; tests about racing copies override this.
        _records.Setup(r => r.ClaimSendAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryRecordEntity?>()))
            .ReturnsAsync(new SendClaim(SendClaimStatus.Claimed, new Azure.ETag("W/\"claim\"")));

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

    private static QueueMessage NewMessage(
        string format = "text", string message = "Hello", string alias = "test",
        string? replyTo = null, string? update = null, DateTimeOffset? enqueuedAt = null) => new()
    {
        MessageId = "msg-test-123",
        Alias = alias,
        Message = message,
        Format = format,
        ReplyTo = replyTo,
        Update = update,
        EnqueuedAt = enqueuedAt ?? DateTimeOffset.UtcNow
    };

    private static string CreateQueueMessageJson(string format = "text", string message = "Hello", string alias = "test") =>
        JsonSerializer.Serialize(NewMessage(format, message, alias));

    private Task RunAsync(QueueMessage message) => _function.Run(JsonSerializer.Serialize(message), _functionContext.Object);

    private static DeliveryRecordEntity DeliveredParent(
        string messageId = "msg-parent", string targetType = "channel", string activityId = "activity-root",
        string? threadActivityId = "activity-root") => new()
    {
        PartitionKey = messageId,
        Status = DeliveryStatus.Delivered,
        Alias = "test",
        TargetType = targetType,
        TeamId = targetType == "channel" ? "team-old" : null,
        ChannelId = targetType == "channel" ? "channel-old" : null,
        ChatId = targetType == "groupChat" ? "19:chat@thread.v2" : null,
        ConversationId = targetType == "channel" ? "19:channel-old@thread.tacv2" : "19:chat@thread.v2",
        ActivityId = activityId,
        ThreadActivityId = threadActivityId
    };

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
    public async Task DirectTarget_Personal_IsSentToTheConversationFoundForThePerson()
    {
        _botService.Setup(b => b.FindPersonalConversationAsync("jane.doe@example.com", null))
            .ReturnsAsync(("user", "oid-jane"));

        await RunAsync(PersonalMessage("jane.doe@example.com"));

        _botService.Verify(b => b.SendAsync("user", "oid-jane", "text", "Personal message", null), Times.Once);
    }

    [Fact]
    public async Task DirectTarget_Personal_SearchesOnlyTheTeamGiven()
    {
        _botService.Setup(b => b.FindPersonalConversationAsync("jane.doe@example.com", "team-1"))
            .ReturnsAsync(("user", "oid-jane"));

        await RunAsync(PersonalMessage("jane.doe@example.com", teamId: "team-1"));

        _botService.Verify(b => b.SendAsync("user", "oid-jane", "text", "Personal message", null), Times.Once);
    }

    [Fact]
    public async Task DirectTarget_PersonInNoRoster_FailsForGood_WithoutRetry()
    {
        _botService.Setup(b => b.FindPersonalConversationAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(((string, string)?)null);

        await RunAsync(PersonalMessage("stranger@example.com"));

        _botService.Verify(b => b.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _records.Verify(r => r.MarkFailedAsync(It.IsAny<QueueMessage>(), It.Is<string>(e => e.Contains("no team"))), Times.Once);
        _events.Verify(e => e.DeliveryFailed(It.IsAny<QueueMessage>(), It.IsAny<long>(), "RecipientNotFound", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task DirectTarget_PersonLookupFailing_IsRetried()
    {
        _botService.Setup(b => b.FindPersonalConversationAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new HttpRequestException("roster read failed"));

        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(PersonalMessage("jane.doe@example.com")));
        _records.Verify(r => r.MarkFailedAsync(It.IsAny<QueueMessage>(), It.IsAny<string>()), Times.Never);
    }

    private static QueueMessage PersonalMessage(string userId, string? teamId = null) => new()
    {
        MessageId = "msg-direct-2",
        Target = new MessageTarget { Type = "personal", UserId = userId, TeamId = teamId },
        Message = "Personal message",
        Format = "text",
        Source = "send",
        EnqueuedAt = DateTimeOffset.UtcNow
    };

    // --- Replies ---

    [Fact]
    public async Task Reply_ToDeliveredChannelPost_GoesIntoItsThread_WhereTheParentWent()
    {
        SetUpChannelAlias(); // the alias now points elsewhere than the parent's conversation
        SetUpRecord(DeliveredParent());
        DeliveryOutcome? outcome = null;
        _records.Setup(r => r.MarkDeliveredAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryOutcome>()))
            .Callback<QueueMessage, DeliveryOutcome>((_, o) => outcome = o);

        await RunAsync(NewMessage(replyTo: "msg-parent"));

        _botService.Verify(b => b.SendAsync("team-old", "channel-old", "text", "Hello", "activity-root"), Times.Once);
        Assert.Equal(PostedAs.Reply, outcome!.PostedAs);
        Assert.Equal("activity-root", outcome.ThreadActivityId);
    }

    [Fact]
    public async Task Reply_ToAReply_StaysInTheSameThread()
    {
        SetUpChannelAlias();
        SetUpRecord(DeliveredParent(activityId: "activity-reply", threadActivityId: "activity-root"));

        await RunAsync(NewMessage(replyTo: "msg-parent"));

        _botService.Verify(b => b.SendAsync("team-old", "channel-old", "text", "Hello", "activity-root"), Times.Once);
    }

    [Fact]
    public async Task Reply_ToAChatMessage_IsAnOrdinaryPostInThatChat()
    {
        SetUpChannelAlias();
        SetUpRecord(DeliveredParent(targetType: "groupChat"));
        DeliveryOutcome? outcome = null;
        _records.Setup(r => r.MarkDeliveredAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryOutcome>()))
            .Callback<QueueMessage, DeliveryOutcome>((_, o) => outcome = o);

        await RunAsync(NewMessage(replyTo: "msg-parent"));

        _botService.Verify(b => b.SendAsync("chat", "19:chat@thread.v2", "text", "Hello", null), Times.Once);
        Assert.Equal(PostedAs.Post, outcome!.PostedAs);
    }

    [Theory]
    [InlineData(DeliveryStatus.Failed)]
    [InlineData(null)] // unknown or expired
    public async Task Reply_ToAParentThatWasNeverDelivered_IsANewPost(string? parentStatus)
    {
        SetUpChannelAlias();
        if (parentStatus != null)
            SetUpRecord(new DeliveryRecordEntity { PartitionKey = "msg-parent", Status = parentStatus, Alias = "test" });

        await RunAsync(NewMessage(replyTo: "msg-parent"));

        _botService.Verify(b => b.SendAsync("team-1", "channel-1", "text", "Hello", null), Times.Once);
    }

    [Fact]
    public async Task Reply_ToAParentStillQueued_IsHeld_WithoutSending()
    {
        SetUpChannelAlias();
        SetUpRecord(new DeliveryRecordEntity { PartitionKey = "msg-parent", Status = DeliveryStatus.Queued });

        await RunAsync(NewMessage(replyTo: "msg-parent"));

        _queue.Verify(q => q.RequeueAsync(It.Is<QueueMessage>(m => m.ReplyTo == "msg-parent"),
            QueueProcessorFunction.HoldDelay), Times.Once);
        _botService.VerifyNoOtherCalls();
        _records.Verify(r => r.MarkFailedAsync(It.IsAny<QueueMessage>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Reply_AfterWaitingTooLong_IsANewPost()
    {
        SetUpChannelAlias();
        SetUpRecord(new DeliveryRecordEntity { PartitionKey = "msg-parent", Status = DeliveryStatus.Queued });

        await RunAsync(NewMessage(replyTo: "msg-parent",
            enqueuedAt: DateTimeOffset.UtcNow - QueueProcessorFunction.MaxParentWait - TimeSpan.FromSeconds(1)));

        _queue.VerifyNoOtherCalls();
        _botService.Verify(b => b.SendAsync("team-1", "channel-1", "text", "Hello", null), Times.Once);
    }

    // --- Updates ---

    [Fact]
    public async Task Update_ToDeliveredMessage_ReplacesItInPlace()
    {
        SetUpRecord(DeliveredParent());
        DeliveryOutcome? outcome = null;
        _records.Setup(r => r.MarkDeliveredAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryOutcome>()))
            .Callback<QueueMessage, DeliveryOutcome>((_, o) => outcome = o);

        await RunAsync(NewMessage(message: "Resolved", update: "msg-parent"));

        _botService.Verify(b => b.UpdateAsync("team-old", "channel-old", "19:channel-old@thread.tacv2",
            "activity-root", "text", "Resolved"), Times.Once);
        _botService.Verify(b => b.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        Assert.Equal(PostedAs.Update, outcome!.PostedAs);
        Assert.Equal("activity-root", outcome.ActivityId);
    }

    [Fact]
    public async Task Update_ToAMessageThatFailed_FailsForGood_WithoutPosting()
    {
        SetUpRecord(new DeliveryRecordEntity { PartitionKey = "msg-parent", Status = DeliveryStatus.Failed });

        await RunAsync(NewMessage(update: "msg-parent"));

        _botService.VerifyNoOtherCalls();
        _records.Verify(r => r.MarkFailedAsync(It.IsAny<QueueMessage>(), It.Is<string>(e => e.Contains("never delivered"))), Times.Once);
        _events.Verify(e => e.DeliveryFailed(It.IsAny<QueueMessage>(), It.IsAny<long>(), "UpdateTargetNotDelivered", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Update_ToAMessageStillQueued_IsHeld()
    {
        SetUpRecord(new DeliveryRecordEntity { PartitionKey = "msg-parent", Status = DeliveryStatus.Queued });

        await RunAsync(NewMessage(update: "msg-parent"));

        _queue.Verify(q => q.RequeueAsync(It.IsAny<QueueMessage>(), QueueProcessorFunction.HoldDelay), Times.Once);
        _botService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Update_AfterWaitingTooLong_FailsForGood()
    {
        SetUpRecord(new DeliveryRecordEntity { PartitionKey = "msg-parent", Status = DeliveryStatus.Queued });

        await RunAsync(NewMessage(update: "msg-parent",
            enqueuedAt: DateTimeOffset.UtcNow - QueueProcessorFunction.MaxParentWait - TimeSpan.FromSeconds(1)));

        _botService.VerifyNoOtherCalls();
        _records.Verify(r => r.MarkFailedAsync(It.IsAny<QueueMessage>(), It.IsAny<string>()), Times.Once);
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

    // --- Copies of one message racing, and failures before the send ---

    [Fact]
    public async Task AnotherCopyIsSending_Holds_WithoutSendingOrReportingAFailure()
    {
        SetUpChannelAlias();
        _records.Setup(r => r.ClaimSendAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryRecordEntity?>()))
            .ReturnsAsync(new SendClaim(SendClaimStatus.InProgress));

        await RunAsync(NewMessage());

        _queue.Verify(q => q.RequeueAsync(It.IsAny<QueueMessage>(), QueueProcessorFunction.HoldDelay), Times.Once);
        _botService.Verify(b => b.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _events.Verify(e => e.DeliveryFailed(It.IsAny<QueueMessage>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AnotherCopyDeliveredIt_Skips()
    {
        SetUpChannelAlias();
        _records.Setup(r => r.ClaimSendAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryRecordEntity?>()))
            .ReturnsAsync(new SendClaim(SendClaimStatus.AlreadyDelivered));

        await RunAsync(NewMessage());

        _botService.Verify(b => b.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _queue.VerifyNoOtherCalls();
        _records.Verify(r => r.MarkDeliveredAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryOutcome>()), Times.Never);
    }

    [Fact]
    public async Task SendFailure_GivesTheClaimBack_AndReportsOnce()
    {
        SetUpChannelAlias();
        var claim = new SendClaim(SendClaimStatus.Claimed, new Azure.ETag("W/\"mine\""));
        _records.Setup(r => r.ClaimSendAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryRecordEntity?>())).ReturnsAsync(claim);
        _botService.Setup(b => b.SendAsync("team-1", "channel-1", It.IsAny<string>(), It.IsAny<string>(), null))
            .ThrowsAsync(new InvalidOperationException("teams down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(NewMessage()));

        _records.Verify(r => r.ReleaseSendAsync(It.IsAny<QueueMessage>(), claim), Times.Once);
        _events.Verify(e => e.DeliveryFailed(It.IsAny<QueueMessage>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RecordReadFailure_IsReported_ThenRethrown()
    {
        _records.Setup(r => r.GetAsync("msg-test-123")).ThrowsAsync(new Azure.RequestFailedException(503, "storage down"));

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() => RunAsync(NewMessage()));

        _events.Verify(e => e.DeliveryFailed(It.IsAny<QueueMessage>(), It.IsAny<long>(), "RequestFailedException", "storage down"), Times.Once);
    }

    [Fact]
    public async Task HoldFailure_IsReported_ThenRethrown()
    {
        SetUpChannelAlias();
        SetUpRecord(new DeliveryRecordEntity { PartitionKey = "msg-parent", Status = DeliveryStatus.Queued });
        _queue.Setup(q => q.RequeueAsync(It.IsAny<QueueMessage>(), It.IsAny<TimeSpan>()))
            .ThrowsAsync(new Azure.RequestFailedException(503, "queue down"));

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() => RunAsync(NewMessage(replyTo: "msg-parent")));

        _events.Verify(e => e.DeliveryFailed(It.IsAny<QueueMessage>(), It.IsAny<long>(), "RequestFailedException", "queue down"), Times.Once);
    }

    [Fact]
    public async Task SuccessfulHold_ReportsNoFailure()
    {
        SetUpChannelAlias();
        SetUpRecord(new DeliveryRecordEntity { PartitionKey = "msg-parent", Status = DeliveryStatus.Queued });

        await RunAsync(NewMessage(replyTo: "msg-parent"));

        _events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reply_ToAParentBeingSent_IsHeld()
    {
        SetUpChannelAlias();
        SetUpRecord(new DeliveryRecordEntity { PartitionKey = "msg-parent", Status = DeliveryStatus.Sending });

        await RunAsync(NewMessage(replyTo: "msg-parent"));

        _queue.Verify(q => q.RequeueAsync(It.IsAny<QueueMessage>(), QueueProcessorFunction.HoldDelay), Times.Once);
        _botService.Verify(b => b.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task NoTargetAndNoAlias_FailsForGood_WithoutRetry()
    {
        var message = new QueueMessage { MessageId = "msg-test-123", Message = "hi", EnqueuedAt = DateTimeOffset.UtcNow };

        await RunAsync(message);

        _records.Verify(r => r.MarkFailedAsync(It.IsAny<QueueMessage>(), It.Is<string>(e => e.Contains("neither"))), Times.Once);
        _events.Verify(e => e.DeliveryFailed(It.IsAny<QueueMessage>(), It.IsAny<long>(), "NoTarget", It.IsAny<string>()), Times.Once);
        _botService.Verify(b => b.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    // --- Mentions ---

    private static readonly List<MessageMention> Mentions =
        [new() { Key = "jane", Id = "jane.doe@example.com", Name = "Jane" }];

    [Fact]
    public async Task Post_PassesTheMentionsOn_AndRecordsTheUnresolvedOnes()
    {
        SetUpChannelAlias();
        _botService
            .Setup(b => b.SendAsync("team-1", "channel-1", "text", "<at>jane</at>", null, It.IsAny<IReadOnlyList<MessageMention>?>()))
            .ReturnsAsync(new SentActivity(ChannelConversation, "activity-new") { UnresolvedMentions = ["jane.doe@example.com"] });
        DeliveryOutcome? outcome = null;
        _records.Setup(r => r.MarkDeliveredAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryOutcome>()))
            .Callback<QueueMessage, DeliveryOutcome>((_, o) => outcome = o);
        var message = NewMessage(message: "<at>jane</at>");
        message.Mentions = Mentions;

        await RunAsync(message);

        _botService.Verify(b => b.SendAsync("team-1", "channel-1", "text", "<at>jane</at>", null,
            It.Is<IReadOnlyList<MessageMention>?>(m => m != null && m.Single().Id == "jane.doe@example.com")), Times.Once);
        Assert.Equal(["jane.doe@example.com"], outcome!.UnresolvedMentions);
    }

    [Fact]
    public async Task Update_PassesTheMentionsOn_AndRecordsTheUnresolvedOnes()
    {
        SetUpRecord(DeliveredParent());
        _botService
            .Setup(b => b.UpdateAsync("team-old", "channel-old", "19:channel-old@thread.tacv2", "activity-root",
                "text", "<at>jane</at>", It.IsAny<IReadOnlyList<MessageMention>?>()))
            .ReturnsAsync(["jane.doe@example.com"]);
        DeliveryOutcome? outcome = null;
        _records.Setup(r => r.MarkDeliveredAsync(It.IsAny<QueueMessage>(), It.IsAny<DeliveryOutcome>()))
            .Callback<QueueMessage, DeliveryOutcome>((_, o) => outcome = o);
        var message = NewMessage(message: "<at>jane</at>", update: "msg-parent");
        message.Mentions = Mentions;

        await RunAsync(message);

        Assert.Equal(PostedAs.Update, outcome!.PostedAs);
        Assert.Equal(["jane.doe@example.com"], outcome.UnresolvedMentions);
    }
}
