using Azure;
using Azure.Data.Tables;
using Moq;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class IdempotencyServiceTests
{
    private readonly Mock<TableClient> _tableClient = new();
    private readonly IdempotencyService _service;

    public IdempotencyServiceTests()
    {
        _service = new IdempotencyService(_tableClient.Object);
    }

    [Fact]
    public async Task GetAsync_ExistingEntry_ReturnsResult()
    {
        var entity = new TableEntity("notify", "key-1")
        {
            ["StatusCode"] = 202,
            ["ResponseBody"] = """{"status":"queued"}""",
            ["CreatedAt"] = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        _tableClient
            .Setup(t => t.GetEntityAsync<TableEntity>("notify", "key-1", null, default))
            .ReturnsAsync(Response.FromValue(entity, Mock.Of<Response>()));

        var result = await _service.GetAsync("notify", "key-1");

        Assert.NotNull(result);
        Assert.Equal(202, result.StatusCode);
        Assert.Equal("""{"status":"queued"}""", result.ResponseBody);
    }

    [Fact]
    public async Task GetAsync_MissingEntry_ReturnsNull()
    {
        _tableClient
            .Setup(t => t.GetEntityAsync<TableEntity>("notify", "missing", null, default))
            .ThrowsAsync(new RequestFailedException(404, "Not Found"));

        var result = await _service.GetAsync("notify", "missing");

        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_UpsertsCalled()
    {
        await _service.SetAsync("notify", "key-1", 202, """{"status":"queued"}""");

        _tableClient.Verify(t => t.UpsertEntityAsync(
            It.Is<TableEntity>(e =>
                e.PartitionKey == "notify" &&
                e.RowKey == "key-1" &&
                (int)e["StatusCode"] == 202 &&
                (string)e["ResponseBody"] == """{"status":"queued"}"""),
            It.IsAny<TableUpdateMode>(),
            default), Times.Once);
    }

    [Fact]
    public async Task GetAsync_RecordWithoutCreatedAt_IsTreatedAsExpired()
    {
        // Written before expiry was tracked; must not count forever.
        var entity = new TableEntity("updown-ingest", "key-1") { ["StatusCode"] = 200, ["ResponseBody"] = "" };
        _tableClient
            .Setup(t => t.GetEntityAsync<TableEntity>("updown-ingest", "key-1", null, default))
            .ReturnsAsync(Response.FromValue(entity, Mock.Of<Response>()));

        Assert.Null(await _service.GetAsync("updown-ingest", "key-1"));
    }

    public static TheoryData<Exception> BookkeepingFailures => new()
    {
        new RequestFailedException(503, "unavailable"),
        new Azure.Identity.AuthenticationFailedException("token acquisition failed"),
        new InvalidOperationException("anything else")
    };

    [Theory]
    [MemberData(nameof(BookkeepingFailures))]
    public async Task CompleteAsync_AnyFailure_DoesNotThrow_BecauseTheMessageIsAlreadyQueued(Exception failure)
    {
        _tableClient
            .Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), default))
            .ThrowsAsync(failure);
        var claim = new IdempotencyClaim(IdempotencyClaimStatus.Claimed, "notify", "k", DateTimeOffset.UtcNow, ETag: new ETag("W/\"1\""));

        await _service.CompleteAsync(claim, 202, "{}");
    }

    [Theory]
    [MemberData(nameof(BookkeepingFailures))]
    public async Task ReleaseAsync_AnyFailure_DoesNotThrow_SoTheOriginalFailurePropagates(Exception failure)
    {
        _tableClient
            .Setup(t => t.DeleteEntityAsync("notify", "k", It.IsAny<ETag>(), default))
            .ThrowsAsync(failure);
        var claim = new IdempotencyClaim(IdempotencyClaimStatus.Claimed, "notify", "k", DateTimeOffset.UtcNow, ETag: new ETag("W/\"1\""));

        await _service.ReleaseAsync(claim);
    }

    [Fact]
    public async Task CompleteAndRelease_AreConditionalOnTheClaimsETag()
    {
        var etag = new ETag("W/\"claim-etag\"");
        var claim = new IdempotencyClaim(IdempotencyClaimStatus.Claimed, "notify", "k", DateTimeOffset.UtcNow, ETag: etag);

        await _service.CompleteAsync(claim, 202, "{}");
        await _service.ReleaseAsync(claim);

        _tableClient.Verify(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), etag, TableUpdateMode.Replace, default), Times.Once);
        _tableClient.Verify(t => t.DeleteEntityAsync("notify", "k", etag, default), Times.Once);
    }
}
