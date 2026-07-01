using Azure.Storage.Queues;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

public class UpdownIngestFunctionTests
{
    private readonly Mock<IWebhookService> _webhookService = new();
    private readonly Mock<QueueClient> _queueClient = new();
    private readonly Mock<IIdempotencyService> _idempotency = new();

    private static WebhookTokenEntity ChannelWebhook(string enabledEvents = "") => new()
    {
        PartitionKey = "webhook",
        RowKey = "hash",
        Id = "w1",
        Source = "updown",
        TargetType = "channel",
        TeamId = "team-1",
        ChannelId = "channel-1",
        UpdownAccount = "prod / ops@dsb.no",
        EnabledEvents = enabledEvents
    };

    private UpdownIngestFunction NewFunction(ILogger<UpdownIngestFunction>? logger = null)
    {
        // Default: token resolves to a channel webhook, not seen before, enqueue succeeds.
        _webhookService.Setup(s => s.ResolveByTokenAsync(It.IsAny<string>()))
            .ReturnsAsync(ChannelWebhook());
        _idempotency.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((IdempotencyResult?)null);
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());

        return new UpdownIngestFunction(
            _webhookService.Object, _queueClient.Object, _idempotency.Object,
            logger ?? NullLogger<UpdownIngestFunction>.Instance);
    }

    private void VerifyEnqueued(Times times) =>
        _queueClient.Verify(q => q.SendMessageAsync(It.IsAny<string>()), times);

    [Fact]
    public async Task ValidToken_CheckDown_Enqueues_AdaptiveCard_ToTarget()
    {
        var fn = NewFunction();
        var req = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.CheckDown);

        var result = await fn.Run(req, "good-token");

        Assert.IsType<OkObjectResult>(result);
        _queueClient.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => s.Contains("adaptive-card") && s.Contains("channel-1") && s.Contains("team-1"))),
            Times.Once);
        _webhookService.Verify(s => s.TouchLastReceivedAsync(It.IsAny<WebhookTokenEntity>()), Times.Once);
    }

    [Fact]
    public async Task UnknownToken_Returns404_NothingEnqueued()
    {
        var fn = NewFunction();
        _webhookService.Setup(s => s.ResolveByTokenAsync(It.IsAny<string>()))
            .ReturnsAsync((WebhookTokenEntity?)null);

        var req = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.CheckDown);
        var result = await fn.Run(req, "bogus");

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        VerifyEnqueued(Times.Never());
    }

    [Fact]
    public async Task MalformedBody_Returns200_NothingEnqueued()
    {
        var fn = NewFunction();
        var req = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.Malformed);

        var result = await fn.Run(req, "good-token");

        Assert.IsType<OkObjectResult>(result);
        VerifyEnqueued(Times.Never());
    }

    [Fact]
    public async Task NotAnArray_Returns200_NothingEnqueued()
    {
        var fn = NewFunction();
        var req = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.NotAnArray);

        var result = await fn.Run(req, "good-token");

        Assert.IsType<OkObjectResult>(result);
        VerifyEnqueued(Times.Never());
    }

    [Fact]
    public async Task EmptyArray_Returns200_NothingEnqueued()
    {
        var fn = NewFunction();
        var req = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.EmptyArray);

        var result = await fn.Run(req, "good-token");

        Assert.IsType<OkObjectResult>(result);
        VerifyEnqueued(Times.Never());
    }

    [Fact]
    public async Task UnknownEventType_Skipped_Returns200()
    {
        var fn = NewFunction();
        var req = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.UnknownEvent);

        var result = await fn.Run(req, "good-token");

        Assert.IsType<OkObjectResult>(result);
        VerifyEnqueued(Times.Never());
    }

    [Fact]
    public async Task PerformanceDrop_DisabledByDefault_Skipped()
    {
        var fn = NewFunction(); // default EnabledEvents excludes performance_drop
        var req = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.PerformanceDrop);

        var result = await fn.Run(req, "good-token");

        Assert.IsType<OkObjectResult>(result);
        VerifyEnqueued(Times.Never());
    }

    [Fact]
    public async Task FilteredEvent_Skipped()
    {
        var fn = NewFunction();
        _webhookService.Setup(s => s.ResolveByTokenAsync(It.IsAny<string>()))
            .ReturnsAsync(ChannelWebhook(enabledEvents: "check.ssl_expiration,check.ssl_renewed"));

        var req = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.CheckUp);
        var result = await fn.Run(req, "good-token");

        Assert.IsType<OkObjectResult>(result);
        VerifyEnqueued(Times.Never());
    }

    [Fact]
    public async Task DuplicateEvent_Deduped_NotEnqueuedAgain()
    {
        var fn = NewFunction();
        _idempotency.Setup(s => s.GetAsync("updown-ingest", It.IsAny<string>()))
            .ReturnsAsync(new IdempotencyResult { StatusCode = 200, ResponseBody = "" });

        var req = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.CheckDown);
        var result = await fn.Run(req, "good-token");

        Assert.IsType<OkObjectResult>(result);
        VerifyEnqueued(Times.Never());
    }

    [Fact]
    public async Task MixedArray_OnlyEligibleEnqueued()
    {
        var fn = NewFunction();
        // down (enabled) + unknown (skip) + performance_drop (disabled by default) → 1 enqueue
        var body = "[" +
            Inner(UpdownPayloads.CheckDown) + "," +
            Inner(UpdownPayloads.UnknownEvent) + "," +
            Inner(UpdownPayloads.PerformanceDrop) + "]";

        var req = HttpRequestHelper.CreatePostRequest(body: body);
        var result = await fn.Run(req, "good-token");

        Assert.IsType<OkObjectResult>(result);
        VerifyEnqueued(Times.Once());
    }

    [Fact]
    public async Task OversizeBody_Returns413()
    {
        var fn = NewFunction();
        var big = "[{\"event\":\"check.down\",\"description\":\"" + new string('x', 70 * 1024) + "\"}]";
        var req = HttpRequestHelper.CreatePostRequest(body: big);

        var result = await fn.Run(req, "good-token");

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(413, obj.StatusCode);
        VerifyEnqueued(Times.Never());
    }

    [Fact]
    public async Task EnqueueFailure_Returns500_ForRetry()
    {
        var fn = NewFunction();
        // A storage enqueue failure surfaces as RequestFailedException — the handler converts it to
        // a controlled 500 (so updown retries). Non-storage exceptions intentionally bubble instead.
        _queueClient.Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ThrowsAsync(new Azure.RequestFailedException(503, "storage down"));

        var req = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.CheckDown);
        var result = await fn.Run(req, "good-token");

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, obj.StatusCode);
    }

    [Fact]
    public async Task DebugDump_WhenEnabled_LogsPayloadAtDebug()
    {
        var logger = new Mock<ILogger<UpdownIngestFunction>>();
        var fn = NewFunction(logger.Object);

        var prev = Environment.GetEnvironmentVariable("UpdownWebhook__DebugLogPayload");
        Environment.SetEnvironmentVariable("UpdownWebhook__DebugLogPayload", "true");
        try
        {
            var req = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.CheckDown);
            await fn.Run(req, "good-token");
        }
        finally
        {
            Environment.SetEnvironmentVariable("UpdownWebhook__DebugLogPayload", prev);
        }

        logger.Verify(l => l.Log(
            LogLevel.Debug,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => true),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    /// <summary>Strips the outer [ ] of a single-event fixture so events can be concatenated.</summary>
    private static string Inner(string arrayFixture)
    {
        var t = arrayFixture.Trim();
        return t[1..^1].Trim();
    }
}
