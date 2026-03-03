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
    private readonly Mock<QueueClient> _queueClient = new();
    private readonly Mock<IIdempotencyService> _idempotencyService = new();
    private readonly NotifyFunction _function;

    public NotifyFunctionTests()
    {
        _function = new NotifyFunction(
            _aliasService.Object,
            _queueClient.Object,
            _idempotencyService.Object,
            NullLogger<NotifyFunction>.Instance);
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

    [Fact]
    public async Task IdempotencyKey_FirstCall_Returns202AndStores()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());
        _idempotencyService
            .Setup(s => s.GetAsync("notify", "key-123"))
            .ReturnsAsync((IdempotencyResult?)null);

        var req = HttpRequestHelper.CreatePostRequest(
            body: """{"message": "Hello", "format": "text"}""",
            headers: new Dictionary<string, string> { ["Idempotency-Key"] = "key-123" });

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);

        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Once);
        _idempotencyService.Verify(s => s.SetAsync("notify", "key-123", 202, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task IdempotencyKey_DuplicateCall_ReturnsCachedResponse()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });
        _idempotencyService
            .Setup(s => s.GetAsync("notify", "key-123"))
            .ReturnsAsync(new IdempotencyResult
            {
                StatusCode = 202,
                ResponseBody = """{"status":"queued","messageId":"msg-abc","correlationId":"corr-1","timestamp":"2026-01-01T00:00:00.0000000Z"}"""
            });

        var req = HttpRequestHelper.CreatePostRequest(
            body: """{"message": "Hello", "format": "text"}""",
            headers: new Dictionary<string, string> { ["Idempotency-Key"] = "key-123" });

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);

        // Should NOT enqueue a message
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NoIdempotencyKey_NormalBehavior()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());

        var req = HttpRequestHelper.CreatePostRequest(
            body: """{"message": "Hello", "format": "text"}""");

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);

        // Should enqueue but NOT interact with idempotency service
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Once);
        _idempotencyService.Verify(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _idempotencyService.Verify(s => s.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }
}
