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

public class SendFunctionTests
{
    private readonly Mock<QueueClient> _queueClient = new();
    private readonly Mock<IIdempotencyService> _idempotencyService = new();
    private readonly Mock<IDeliveryRecords> _deliveryRecords = new();
    private readonly Mock<IBotService> _botService = new();
    private readonly SendFunction _function;

    public SendFunctionTests()
    {
        _function = new SendFunction(
            new NotificationQueue(_queueClient.Object, Mock.Of<IDeliveryRecords>(), Mock.Of<IDeliveryEvents>()),
            _idempotencyService.Object,
            _deliveryRecords.Object,
            _botService.Object,
            NullLogger<SendFunction>.Instance);
    }

    [Fact]
    public async Task ValidChannelTarget_Returns202()
    {
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());

        var req = HttpRequestHelper.CreatePostRequest(body: """
            {
                "target": { "type": "channel", "teamId": "team-1", "channelId": "channel-1" },
                "message": "Hello",
                "format": "text"
            }
            """);

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);

        var json = JsonSerializer.Serialize(objectResult.Value);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("queued", doc.RootElement.GetProperty("status").GetString());

        _queueClient.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => s.Contains("Hello") && s.Contains("channel"))), Times.Once);
    }

    [Fact]
    public async Task ValidPersonalTarget_Returns202()
    {
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());

        var req = HttpRequestHelper.CreatePostRequest(body: """
            {
                "target": { "type": "personal", "userId": "0b5f8a8e-1c1e-4f43-9a37-2f6b1b0c9d11" },
                "message": "Hello user",
                "format": "text"
            }
            """);

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);
    }

    [Fact]
    public async Task MissingTargetType_Returns400()
    {
        var req = HttpRequestHelper.CreatePostRequest(body: """
            {
                "target": { "teamId": "team-1" },
                "message": "Hello",
                "format": "text"
            }
            """);

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("target.type", problem.Detail);
    }

    [Fact]
    public async Task ChannelMissingTeamId_Returns400()
    {
        var req = HttpRequestHelper.CreatePostRequest(body: """
            {
                "target": { "type": "channel", "channelId": "channel-1" },
                "message": "Hello",
                "format": "text"
            }
            """);

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("teamId", problem.Detail);
    }

    [Fact]
    public async Task InvalidContentType_Returns415()
    {
        var req = HttpRequestHelper.CreatePostRequest(body: "text", contentType: "text/plain");

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(415, objectResult.StatusCode);
        Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    [Fact]
    public async Task EmptyMessage_Returns400()
    {
        var req = HttpRequestHelper.CreatePostRequest(body: """
            {
                "target": { "type": "channel", "teamId": "team-1", "channelId": "channel-1" },
                "message": "",
                "format": "text"
            }
            """);

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("Message", problem.Detail);
    }

    [Fact]
    public async Task InvalidJson_Returns400()
    {
        var req = HttpRequestHelper.CreatePostRequest(body: "{{invalid json}}");

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    [Fact]
    public async Task OversizedMetadata_Returns400_AndQueuesNothing()
    {
        var req = HttpRequestHelper.CreatePostRequest(body: """
            {
                "target": { "type": "channel", "teamId": "team-1", "channelId": "channel-1" },
                "message": "hi",
                "metadata": { "bad key": "v" }
            }
            """);

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("metadata key", problem.Detail);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    // --- Idempotency-Key ---

    private const string ChannelSendBody = """
        {
            "target": { "type": "channel", "teamId": "team-1", "channelId": "channel-1" },
            "message": "hi"
        }
        """;

    [Fact]
    public async Task IdempotencyKey_ScopedToCallerAndTarget_QueuesOnce_AndCompletes()
    {
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());
        var scoped = TeamsNotificationBot.Helpers.IdempotencyKeys.Scope("caller-a", "run-7", "channel", "team-1", "channel-1", null, null);
        var claim = new IdempotencyClaim(IdempotencyClaimStatus.Claimed, "send", scoped, DateTimeOffset.UtcNow);
        _idempotencyService.Setup(s => s.ClaimAsync("send", scoped)).ReturnsAsync(claim);
        var req = HttpRequestHelper.CreatePostRequest(body: ChannelSendBody,
            headers: new Dictionary<string, string> { ["Idempotency-Key"] = "run-7" });
        req.HttpContext.Items[TeamsNotificationBot.Middleware.AuthMiddleware.PrincipalIdItemKey] = "caller-a";

        var result = await _function.Run(req);

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Once);
        _idempotencyService.Verify(s => s.CompleteAsync(claim, 202, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task IdempotencyKey_Duplicate_ReplaysWithoutQueuing()
    {
        _idempotencyService.Setup(s => s.ClaimAsync("send", It.IsAny<string>()))
            .ReturnsAsync(new IdempotencyClaim(IdempotencyClaimStatus.Completed, "send", "k", DateTimeOffset.UtcNow,
                new IdempotencyResult { StatusCode = 202, ResponseBody = """{"messageId":"send-abc"}""" }));
        var req = HttpRequestHelper.CreatePostRequest(body: ChannelSendBody,
            headers: new Dictionary<string, string> { ["Idempotency-Key"] = "run-7" });

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);
        Assert.Contains("send-abc", JsonSerializer.Serialize(objectResult.Value));
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IdempotencyKey_TargetsThatOnlyDifferInWhereASeparatorFalls_DoNotShareAKey()
    {
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());
        var keys = new List<string>();
        _idempotencyService.Setup(s => s.ClaimAsync("send", It.IsAny<string>()))
            .Callback<string, string>((_, k) => keys.Add(k))
            .ReturnsAsync(new IdempotencyClaim(IdempotencyClaimStatus.Claimed, "send", "k", DateTimeOffset.UtcNow));

        foreach (var (team, channel) in new[] { ("a|b", "c"), ("a", "b|c") })
        {
            var req = HttpRequestHelper.CreatePostRequest(
                body: $$"""{"target": {"type": "channel", "teamId": "{{team}}", "channelId": "{{channel}}"}, "message": "hi"}""",
                headers: new Dictionary<string, string> { ["Idempotency-Key"] = "run-7" });
            await _function.Run(req);
        }

        Assert.Equal(2, keys.Distinct().Count());
    }

    private static Microsoft.AspNetCore.Http.HttpRequest SendWithKey(string key) =>
        HttpRequestHelper.CreatePostRequest(body: ChannelSendBody,
            headers: new Dictionary<string, string> { ["Idempotency-Key"] = key });

    [Fact]
    public async Task IdempotencyKey_Invalid_Returns400_WithoutClaiming()
    {
        var result = await _function.Run(SendWithKey(new string('k', 257)));

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        _idempotencyService.VerifyNoOtherCalls();
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IdempotencyKey_InProgress_Returns409_WithoutQueuing()
    {
        _idempotencyService.Setup(s => s.ClaimAsync("send", It.IsAny<string>()))
            .ReturnsAsync(new IdempotencyClaim(IdempotencyClaimStatus.InProgress, "send", "k", DateTimeOffset.UtcNow));

        var result = await _function.Run(SendWithKey("run-7"));

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IdempotencyKey_QueueFailure_ReleasesTheClaim_AndPropagates()
    {
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ThrowsAsync(new Azure.RequestFailedException(503, "unavailable"));
        var claim = new IdempotencyClaim(IdempotencyClaimStatus.Claimed, "send", "k", DateTimeOffset.UtcNow);
        _idempotencyService.Setup(s => s.ClaimAsync("send", It.IsAny<string>())).ReturnsAsync(claim);

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() => _function.Run(SendWithKey("run-7")));

        _idempotencyService.Verify(s => s.ReleaseAsync(claim), Times.Once);
        _idempotencyService.Verify(s => s.CompleteAsync(It.IsAny<IdempotencyClaim>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    // --- Direct messages ---

    private void QueueAccepts() => _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
        .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());

    private static Microsoft.AspNetCore.Http.HttpRequest Personal(string userId, string extra = "") =>
        HttpRequestHelper.CreatePostRequest(body: $$"""
            { "target": { "type": "personal", "userId": "{{userId}}" }, "message": "Hello"{{extra}} }
            """);

    [Theory]
    [InlineData("jane.doe@example.com")]
    [InlineData("0b5f8a8e-1c1e-4f43-9a37-2f6b1b0c9d11")]
    public async Task PersonalTarget_ByUpnOrObjectId_IsQueued(string userId)
    {
        QueueAccepts();

        var result = await _function.Run(Personal(userId));

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
        _queueClient.Verify(q => q.SendMessageAsync(It.Is<string>(s => s.Contains(userId))), Times.Once);
    }

    [Theory]
    [InlineData("user-abc")]
    [InlineData("29:1abc")]
    [InlineData("jane doe@example.com")]
    public async Task PersonalTarget_NeitherObjectIdNorUpn_Returns400(string userId)
    {
        var result = await _function.Run(Personal(userId));

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.Contains("object ID", Assert.IsType<ProblemDetails>(objectResult.Value).Detail);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    // --- update ---

    private const string ParentId = "send-0123456789abcdef0123456789abcdef";

    private static string PersonalKey(string userId) => new MessageTarget { Type = "personal", UserId = userId }.Key();

    private void Parent(string requestedTarget, string status = DeliveryStatus.Delivered) =>
        _deliveryRecords.Setup(r => r.GetAsync(ParentId)).ReturnsAsync(new DeliveryRecordEntity
        {
            PartitionKey = ParentId,
            Status = status,
            RequestedTarget = requestedTarget,
            TargetType = status == DeliveryStatus.Delivered ? "personal" : null,
            UserId = status == DeliveryStatus.Delivered ? "oid-jane" : null
        });

    [Fact]
    public async Task Update_OfAMessageToTheSameTarget_IsQueuedWithTheReference()
    {
        QueueAccepts();
        Parent(PersonalKey("jane.doe@example.com"));
        _botService.Setup(b => b.HasConversationAsync("user", "oid-jane")).ReturnsAsync(true);

        var result = await _function.Run(Personal("Jane.Doe@example.com", $", \"update\": \"{ParentId}\""));

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
        _queueClient.Verify(q => q.SendMessageAsync(It.Is<string>(s => s.Contains($"\"update\":\"{ParentId}\""))), Times.Once);
    }

    [Fact]
    public async Task Update_OfAMessageStillQueued_IsQueued_ToBeHeld()
    {
        QueueAccepts();
        Parent(PersonalKey("jane.doe@example.com"), DeliveryStatus.Queued);

        var result = await _function.Run(Personal(JaneUpnForUpdate, $", \"update\": \"{ParentId}\""));

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    private const string JaneUpnForUpdate = "jane.doe@example.com";

    [Fact]
    public async Task Update_OfAnUnknownMessage_Returns404()
    {
        var result = await _function.Run(Personal(JaneUpnForUpdate, $", \"update\": \"{ParentId}\""));

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
        Assert.Contains("nothing to update", Assert.IsType<ProblemDetails>(objectResult.Value).Detail);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("""["personal","someone.else@example.com"]""")]
    [InlineData("""["personal","0b5f8a8e-1c1e-4f43-9a37-2f6b1b0c9d11"]""")] // the same person by object ID still differs
    [InlineData("""["channel","team-1","channel-1"]""")]
    [InlineData(null)] // a /v1/notify message
    public async Task Update_OfAMessageToAnotherTarget_Returns409(string? requestedTarget)
    {
        Parent(requestedTarget!);

        var result = await _function.Run(Personal(JaneUpnForUpdate, $", \"update\": \"{ParentId}\""));

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Update_OfAMessageWhoseConversationIsGone_Returns404()
    {
        Parent(PersonalKey("jane.doe@example.com"));
        _botService.Setup(b => b.HasConversationAsync("user", "oid-jane")).ReturnsAsync(false);

        var result = await _function.Run(Personal(JaneUpnForUpdate, $", \"update\": \"{ParentId}\""));

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
        Assert.Contains("no longer has the conversation", Assert.IsType<ProblemDetails>(objectResult.Value).Detail);
    }

    [Fact]
    public async Task Update_Malformed_Returns400_WithoutALookup()
    {
        var result = await _function.Run(Personal(JaneUpnForUpdate, ", \"update\": \"send-../x\""));

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        _deliveryRecords.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Update_Rejected_ReleasesTheIdempotencyClaim()
    {
        var claim = new IdempotencyClaim(IdempotencyClaimStatus.Claimed, "send", "row", DateTimeOffset.UtcNow);
        _idempotencyService.Setup(s => s.ClaimAsync("send", It.IsAny<string>())).ReturnsAsync(claim);
        var req = HttpRequestHelper.CreatePostRequest(
            body: $$"""{ "target": { "type": "personal", "userId": "jane.doe@example.com" }, "message": "Hi", "update": "{{ParentId}}" }""",
            headers: new Dictionary<string, string> { ["Idempotency-Key"] = "run-7" });

        var result = await _function.Run(req);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        _idempotencyService.Verify(s => s.ReleaseAsync(claim), Times.Once);
    }

    [Fact]
    public void TargetKey_NamesTheConversation_NotTheTeamThatNarrowsAPersonsSearch()
    {
        Assert.Equal(
            new MessageTarget { Type = "personal", UserId = "Jane@Example.com", TeamId = "team-1" }.Key(),
            new MessageTarget { Type = "personal", UserId = "jane@example.com" }.Key());
        Assert.NotEqual(
            new MessageTarget { Type = "channel", TeamId = "t", ChannelId = "a|b" }.Key(),
            new MessageTarget { Type = "channel", TeamId = "t|a", ChannelId = "b" }.Key());
        Assert.Equal("""["groupChat","19:chat@thread.v2"]""",
            new MessageTarget { Type = "groupChat", ChatId = "19:chat@thread.v2", TeamId = "ignored" }.Key());
        Assert.Equal("""["other"]""", new MessageTarget { Type = "other" }.Key());
    }
}
