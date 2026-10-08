using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

public class NotifyFunctionTests
{
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<IBotService> _botService = new();
    private readonly Mock<QueueClient> _queueClient = new();
    private readonly Mock<IIdempotencyService> _idempotencyService = new();
    private readonly Mock<IDeliveryRecords> _deliveryRecords = new();
    private readonly NotifyFunction _function;

    public NotifyFunctionTests()
    {
        _function = new NotifyFunction(
            _aliasService.Object,
            _botService.Object,
            new NotificationQueue(_queueClient.Object, Mock.Of<IDeliveryRecords>(), Mock.Of<IDeliveryEvents>()),
            _idempotencyService.Object,
            _deliveryRecords.Object,
            NullLogger<NotifyFunction>.Instance);

        // The alias's conversation exists unless a test says otherwise.
        _botService.Setup(b => b.HasConversationAsync(It.IsAny<AliasEntity>())).ReturnsAsync(true);
    }

    [Fact]
    public async Task T1_UnknownAlias_Returns404ProblemDetails()
    {
        _aliasService.Setup(s => s.GetAliasAsync("unknown")).ReturnsAsync((AliasEntity?)null);
        var req = HttpRequestHelper.CreatePostRequest(
            body: """{"message": "Hello", "format": "text"}""");

        var result = await _function.Run(req, "unknown");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("Not Found", problem.Title);
        Assert.Contains("unknown", problem.Detail);
    }

    [Fact]
    public async Task T2_WrongContentType_Returns415ProblemDetails()
    {
        var req = HttpRequestHelper.CreatePostRequest(
            body: "Hello",
            contentType: "text/plain");

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(415, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("Unsupported Media Type", problem.Title);
    }

    [Fact]
    public async Task T3_ValidTextRequest_Returns202WithCorrelationId()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());

        var req = HttpRequestHelper.CreatePostRequest(
            body: """{"message": "Hello World", "format": "text"}""");

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);

        var json = JsonSerializer.Serialize(objectResult.Value);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("queued", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));
        Assert.True(doc.RootElement.TryGetProperty("messageId", out _));

        _queueClient.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => s.Contains("Hello World"))), Times.Once);
    }

    [Fact]
    public async Task InvalidJson_Returns400ProblemDetails()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });

        var req = HttpRequestHelper.CreatePostRequest(body: "not-json{{{");

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    [Fact]
    public async Task InvalidRequestBody_Returns400ProblemDetails()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });

        var req = HttpRequestHelper.CreatePostRequest(
            body: """{"message": {"key": "value"}, "format": "text"}""");

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    [Fact]
    public async Task AdaptiveCardWithProhibitedAction_Returns400ProblemDetails()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });

        var cardJson = """
        {
            "type": "AdaptiveCard",
            "version": "1.4",
            "body": [{ "type": "TextBlock", "text": "Hi" }],
            "actions": [{ "type": "Action.OpenUrl", "title": "Click", "url": "https://evil.com" }]
        }
        """;
        var req = HttpRequestHelper.CreatePostRequest(
            body: $$$"""{"message": {{{cardJson}}}, "format": "adaptive-card"}""");

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    // --- Idempotency-Key ---

    private void SetUpDeliverableAlias()
    {
        // Case-insensitive, like AliasService.
        _aliasService.Setup(s => s.GetAliasAsync(It.Is<string>(n => string.Equals(n, "test", StringComparison.OrdinalIgnoreCase)))).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());
    }

    private static Microsoft.AspNetCore.Http.HttpRequest RequestWithKey(string key, string principal = "caller-a")
    {
        var req = HttpRequestHelper.CreatePostRequest(
            body: """{"message": "Hello", "format": "text"}""",
            headers: new Dictionary<string, string> { ["Idempotency-Key"] = key });
        req.HttpContext.Items[TeamsNotificationBot.Middleware.AuthMiddleware.PrincipalIdItemKey] = principal;
        return req;
    }

    private static IdempotencyClaim Claim(IdempotencyClaimStatus status, IdempotencyResult? result = null) =>
        new(status, "notify", "scoped", DateTimeOffset.UtcNow, result);

    [Fact]
    public async Task IdempotencyKey_FirstCall_ClaimsScopedKey_Queues_AndCompletes()
    {
        SetUpDeliverableAlias();
        var scoped = TeamsNotificationBot.Helpers.IdempotencyKeys.Scope("caller-a", "key-123", "test");
        var claim = Claim(IdempotencyClaimStatus.Claimed);
        _idempotencyService.Setup(s => s.ClaimAsync("notify", scoped)).ReturnsAsync(claim);

        var result = await _function.Run(RequestWithKey("key-123"), "Test");

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Once);
        _idempotencyService.Verify(s => s.CompleteAsync(claim, 202, It.Is<string>(b => b.Contains("msg-"))), Times.Once);
    }

    [Fact]
    public async Task IdempotencyKey_ScopeDependsOnTheCaller()
    {
        SetUpDeliverableAlias();
        _idempotencyService.Setup(s => s.ClaimAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(Claim(IdempotencyClaimStatus.Claimed));

        await _function.Run(RequestWithKey("key-123", principal: "caller-a"), "test");
        await _function.Run(RequestWithKey("key-123", principal: "caller-b"), "test");

        _idempotencyService.Verify(s => s.ClaimAsync("notify",
            TeamsNotificationBot.Helpers.IdempotencyKeys.Scope("caller-a", "key-123", "test")), Times.Once);
        _idempotencyService.Verify(s => s.ClaimAsync("notify",
            TeamsNotificationBot.Helpers.IdempotencyKeys.Scope("caller-b", "key-123", "test")), Times.Once);
    }

    [Fact]
    public async Task IdempotencyKey_Completed_ReplaysTheOriginalResponse_WithoutQueuing()
    {
        SetUpDeliverableAlias();
        _idempotencyService.Setup(s => s.ClaimAsync("notify", It.IsAny<string>()))
            .ReturnsAsync(Claim(IdempotencyClaimStatus.Completed, new IdempotencyResult
            {
                StatusCode = 202,
                ResponseBody = """{"status":"queued","messageId":"msg-abc","correlationId":"corr-1","timestamp":"2026-01-01T00:00:00.0000000Z"}"""
            }));

        var result = await _function.Run(RequestWithKey("key-123"), "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);
        Assert.Contains("msg-abc", JsonSerializer.Serialize(objectResult.Value));
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IdempotencyKey_InProgress_Returns409_WithoutQueuing()
    {
        SetUpDeliverableAlias();
        _idempotencyService.Setup(s => s.ClaimAsync("notify", It.IsAny<string>()))
            .ReturnsAsync(Claim(IdempotencyClaimStatus.InProgress));

        var result = await _function.Run(RequestWithKey("key-123"), "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, objectResult.StatusCode);
        Assert.Contains("still being processed", Assert.IsType<ProblemDetails>(objectResult.Value).Detail);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IdempotencyKey_QueueFailure_ReleasesTheClaim_AndPropagates()
    {
        SetUpDeliverableAlias();
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ThrowsAsync(new Azure.RequestFailedException(503, "unavailable"));
        var claim = Claim(IdempotencyClaimStatus.Claimed);
        _idempotencyService.Setup(s => s.ClaimAsync("notify", It.IsAny<string>())).ReturnsAsync(claim);

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() => _function.Run(RequestWithKey("key-123"), "test"));

        _idempotencyService.Verify(s => s.ReleaseAsync(claim), Times.Once);
        _idempotencyService.Verify(s => s.CompleteAsync(It.IsAny<IdempotencyClaim>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IdempotencyKey_Completed_StillReplays_AfterTheConversationIsGone()
    {
        // The first request queued fine; the bot was removed from the team before the retry.
        var orphan = new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" };
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(orphan);
        _botService.Setup(b => b.HasConversationAsync(orphan)).ReturnsAsync(false);
        _idempotencyService.Setup(s => s.ClaimAsync("notify", It.IsAny<string>()))
            .ReturnsAsync(Claim(IdempotencyClaimStatus.Completed,
                new IdempotencyResult { StatusCode = 202, ResponseBody = """{"messageId":"msg-abc"}""" }));

        var result = await _function.Run(RequestWithKey("key-123"), "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);
        Assert.Contains("msg-abc", JsonSerializer.Serialize(objectResult.Value));
    }

    [Fact]
    public async Task IdempotencyKey_Completed_StillReplays_AfterTheAliasIsRemoved()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync((AliasEntity?)null);
        _idempotencyService.Setup(s => s.ClaimAsync("notify", It.IsAny<string>()))
            .ReturnsAsync(Claim(IdempotencyClaimStatus.Completed,
                new IdempotencyResult { StatusCode = 202, ResponseBody = """{"messageId":"msg-abc"}""" }));

        var result = await _function.Run(RequestWithKey("key-123"), "test");

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task IdempotencyKey_FreshClaim_IsReleased_WhenTheConversationIsGone()
    {
        var orphan = new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" };
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(orphan);
        _botService.Setup(b => b.HasConversationAsync(orphan)).ReturnsAsync(false);
        var claim = Claim(IdempotencyClaimStatus.Claimed);
        _idempotencyService.Setup(s => s.ClaimAsync("notify", It.IsAny<string>())).ReturnsAsync(claim);

        var result = await _function.Run(RequestWithKey("key-123"), "test");

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        _idempotencyService.Verify(s => s.ReleaseAsync(claim), Times.Once);
        _idempotencyService.Verify(s => s.CompleteAsync(It.IsAny<IdempotencyClaim>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IdempotencyKey_FreshClaim_IsReleased_WhenTheAliasIsUnknown()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync((AliasEntity?)null);
        var claim = Claim(IdempotencyClaimStatus.Claimed);
        _idempotencyService.Setup(s => s.ClaimAsync("notify", It.IsAny<string>())).ReturnsAsync(claim);

        var result = await _function.Run(RequestWithKey("key-123"), "test");

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        _idempotencyService.Verify(s => s.ReleaseAsync(claim), Times.Once);
    }

    [Fact]
    public async Task IdempotencyKey_FreshClaim_IsReleased_WhenACheckThrows()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test"))
            .ThrowsAsync(new Azure.RequestFailedException(503, "unavailable"));
        var claim = Claim(IdempotencyClaimStatus.Claimed);
        _idempotencyService.Setup(s => s.ClaimAsync("notify", It.IsAny<string>())).ReturnsAsync(claim);

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() => _function.Run(RequestWithKey("key-123"), "test"));

        _idempotencyService.Verify(s => s.ReleaseAsync(claim), Times.Once);
    }

    [Fact]
    public async Task IdempotencyKey_Invalid_Returns400_WithoutClaiming()
    {
        SetUpDeliverableAlias();

        var result = await _function.Run(RequestWithKey(new string('k', 257)), "test");

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        _idempotencyService.VerifyNoOtherCalls();
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NoIdempotencyKey_NormalBehavior()
    {
        SetUpDeliverableAlias();

        var result = await _function.Run(HttpRequestHelper.CreatePostRequest(
            body: """{"message": "Hello", "format": "text"}"""), "test");

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Once);
        _idempotencyService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AliasWhoseConversationIsGone_Returns404_AndQueuesNothing()
    {
        var orphan = new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" };
        _aliasService.Setup(s => s.GetAliasAsync("ops")).ReturnsAsync(orphan);
        _botService.Setup(b => b.HasConversationAsync(orphan)).ReturnsAsync(false);

        var result = await _function.Run(HttpRequestHelper.CreatePostRequest(
            body: """{"message": "Hello", "format": "text"}"""), "ops");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("no longer has the conversation", problem.Detail);
        Assert.DoesNotContain("Unknown alias", problem.Detail);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    // --- replyTo / update ---

    private const string ParentId = "msg-0123456789abcdef0123456789abcdef";

    private static Microsoft.AspNetCore.Http.HttpRequest RequestWith(string extraJson) =>
        HttpRequestHelper.CreatePostRequest(body: $$"""{"message": "Hello", "format": "text", {{extraJson}}}""");

    [Fact]
    public async Task ReplyTo_KnownMessageOfThisAlias_IsQueuedWithTheReference()
    {
        SetUpDeliverableAlias();
        _deliveryRecords.Setup(r => r.GetAsync(ParentId)).ReturnsAsync(
            new DeliveryRecordEntity { PartitionKey = ParentId, Alias = "test", Status = DeliveryStatus.Delivered });

        var result = await _function.Run(RequestWith($"\"replyTo\": \"{ParentId}\""), "Test");

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
        _queueClient.Verify(q => q.SendMessageAsync(It.Is<string>(s => s.Contains($"\"replyTo\":\"{ParentId}\""))), Times.Once);
    }

    [Fact]
    public async Task ReplyTo_UnknownMessage_IsAccepted_BecauseItBecomesANewPost()
    {
        SetUpDeliverableAlias();

        var result = await _function.Run(RequestWith($"\"replyTo\": \"{ParentId}\""), "test");

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Update_UnknownMessage_Returns404()
    {
        SetUpDeliverableAlias();

        var result = await _function.Run(RequestWith($"\"update\": \"{ParentId}\""), "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
        Assert.Contains("nothing to update", Assert.IsType<ProblemDetails>(objectResult.Value).Detail);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("replyTo")]
    [InlineData("update")]
    public async Task Reference_ToAnotherAlias_Returns409(string field)
    {
        SetUpDeliverableAlias();
        _deliveryRecords.Setup(r => r.GetAsync(ParentId)).ReturnsAsync(
            new DeliveryRecordEntity { PartitionKey = ParentId, Alias = "other-alias", Status = DeliveryStatus.Delivered });

        var result = await _function.Run(RequestWith($"\"{field}\": \"{ParentId}\""), "test");

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReplyToAndUpdate_Together_Returns400()
    {
        SetUpDeliverableAlias();

        var result = await _function.Run(
            RequestWith($"\"replyTo\": \"{ParentId}\", \"update\": \"{ParentId}\""), "test");

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task MalformedReference_Returns400_WithoutALookup()
    {
        SetUpDeliverableAlias();

        var result = await _function.Run(RequestWith("\"replyTo\": \"msg-../../x\""), "test");

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        _deliveryRecords.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("replyTo")]
    [InlineData("update")]
    public async Task Reference_ToADeliveredMessage_ChecksItsConversation_NotTheAliasesCurrentOne(string field)
    {
        // The alias was repointed to a conversation the bot has since lost; the parent's is fine.
        var repointed = new AliasEntity { TargetType = "channel", TeamId = "team-new", ChannelId = "channel-new" };
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(repointed);
        _botService.Setup(b => b.HasConversationAsync(repointed)).ReturnsAsync(false);
        _botService.Setup(b => b.HasConversationAsync("team-old", "channel-old")).ReturnsAsync(true);
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());
        _deliveryRecords.Setup(r => r.GetAsync(ParentId)).ReturnsAsync(new DeliveryRecordEntity
        {
            PartitionKey = ParentId, Alias = "test", Status = DeliveryStatus.Delivered,
            TargetType = "channel", TeamId = "team-old", ChannelId = "channel-old"
        });

        var result = await _function.Run(RequestWith($"\"{field}\": \"{ParentId}\""), "test");

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Reference_ToADeliveredMessage_WhoseConversationIsGone_Returns404()
    {
        SetUpDeliverableAlias();
        _botService.Setup(b => b.HasConversationAsync("team-old", "channel-old")).ReturnsAsync(false);
        _deliveryRecords.Setup(r => r.GetAsync(ParentId)).ReturnsAsync(new DeliveryRecordEntity
        {
            PartitionKey = ParentId, Alias = "test", Status = DeliveryStatus.Delivered,
            TargetType = "channel", TeamId = "team-old", ChannelId = "channel-old"
        });

        var result = await _function.Run(RequestWith($"\"update\": \"{ParentId}\""), "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
        Assert.Contains(ParentId, Assert.IsType<ProblemDetails>(objectResult.Value).Detail);
    }

    // --- mentions ---

    private const string PersonMention = """{"key": "jane", "id": "jane.doe@example.com", "name": "Jane"}""";
    private const string TagMention = """{"key": "oncall", "tag": "dGFnLWlk", "name": "On call"}""";

    private static Microsoft.AspNetCore.Http.HttpRequest MentionRequest(string message, params string[] mentions) =>
        HttpRequestHelper.CreatePostRequest(body:
            $$"""{"message": "{{message}}", "format": "text", "mentions": [{{string.Join(",", mentions)}}]}""");

    private void SetUpAlias(AliasEntity alias)
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(alias);
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());
    }

    [Fact]
    public async Task Mentions_InAChannel_AreQueuedWithTheMessage()
    {
        SetUpDeliverableAlias();

        var result = await _function.Run(MentionRequest("<at>jane</at>, <at>oncall</at>", PersonMention, TagMention), "test");

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
        _queueClient.Verify(q => q.SendMessageAsync(It.Is<string>(s =>
            s.Contains("\"mentions\":[{\"key\":\"jane\",\"id\":\"jane.doe@example.com\",\"name\":\"Jane\"}"))), Times.Once);
    }

    [Fact]
    public async Task NoMentions_QueueNoMentionsField()
    {
        SetUpDeliverableAlias();

        await _function.Run(HttpRequestHelper.CreatePostRequest(body: """{"message": "Hi", "mentions": []}"""), "test");

        _queueClient.Verify(q => q.SendMessageAsync(It.Is<string>(s => !s.Contains("mentions"))), Times.Once);
    }

    [Fact]
    public async Task Mentions_Misplaced_Return400_WithoutQueuing()
    {
        SetUpDeliverableAlias();

        var result = await _function.Run(MentionRequest("<at>someone</at>", PersonMention), "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.Contains("doesn't name a key", Assert.IsType<ProblemDetails>(objectResult.Value).Detail);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Mentions_OnAPersonalAlias_Return400()
    {
        SetUpAlias(new AliasEntity { TargetType = "personal", UserId = "user-1" });

        var result = await _function.Run(MentionRequest("<at>jane</at>", PersonMention), "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.Contains("personal chat", Assert.IsType<ProblemDetails>(objectResult.Value).Detail);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Tags_OnAGroupChatAlias_Return400_ButPeopleAreFine()
    {
        SetUpAlias(new AliasEntity { TargetType = "groupChat", ChatId = "19:chat@thread.v2" });

        var tags = await _function.Run(MentionRequest("<at>oncall</at>", TagMention), "test");
        var people = await _function.Run(MentionRequest("<at>jane</at>", PersonMention), "test");

        Assert.Equal(400, Assert.IsType<ObjectResult>(tags).StatusCode);
        Assert.Equal(202, Assert.IsType<ObjectResult>(people).StatusCode);
    }

    [Fact]
    public async Task Mentions_InAReplyToADeliveredChannelPost_AreCheckedAgainstWhereTheParentWent()
    {
        // The alias now points to a group chat, but the reply goes into the parent's channel thread.
        SetUpAlias(new AliasEntity { TargetType = "groupChat", ChatId = "19:chat@thread.v2" });
        _botService.Setup(b => b.HasConversationAsync("team-old", "channel-old")).ReturnsAsync(true);
        _deliveryRecords.Setup(r => r.GetAsync(ParentId)).ReturnsAsync(new DeliveryRecordEntity
        {
            PartitionKey = ParentId, Alias = "test", Status = DeliveryStatus.Delivered,
            TargetType = "channel", TeamId = "team-old", ChannelId = "channel-old"
        });

        var result = await _function.Run(HttpRequestHelper.CreatePostRequest(body:
            $$"""{"message": "<at>oncall</at>", "replyTo": "{{ParentId}}", "mentions": [{{TagMention}}]}"""), "test");

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Mentions_RejectedForTheTarget_ReleaseTheIdempotencyClaim()
    {
        SetUpAlias(new AliasEntity { TargetType = "personal", UserId = "user-1" });
        var claim = new IdempotencyClaim(IdempotencyClaimStatus.Claimed, "notify", "row", DateTimeOffset.UtcNow);
        _idempotencyService.Setup(s => s.ClaimAsync("notify", It.IsAny<string>())).ReturnsAsync(claim);
        var req = MentionRequest("<at>jane</at>", PersonMention);
        req.Headers["Idempotency-Key"] = "key-1";

        var result = await _function.Run(req, "test");

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        _idempotencyService.Verify(s => s.ReleaseAsync(claim), Times.Once);
    }
}
