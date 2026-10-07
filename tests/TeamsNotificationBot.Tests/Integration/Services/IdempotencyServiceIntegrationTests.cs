using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Integration.Fixtures;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.Services;

[Collection("Azurite")]
public class IdempotencyServiceIntegrationTests
{
    private static readonly TimeSpan Expiry = TimeSpan.FromHours(168);

    private readonly TableClient _tableClient;
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly IdempotencyService _service;

    public IdempotencyServiceIntegrationTests(AzuriteFixture azurite)
    {
        _tableClient = azurite.CreateTableClient("idempotency");
        _service = new IdempotencyService(_tableClient, Expiry, _time, NullLogger<IdempotencyService>.Instance);
    }

    // Keys are unique per test so tests sharing the table never see each other's records.
    private static string NewKey() => Guid.NewGuid().ToString("N");

    // --- Get/Set (updown dedupe) ---

    [Fact]
    public async Task SetAndGet_RoundTrips_StatusCodeAndBody()
    {
        var key = NewKey();
        var body = """{"id":"msg-1","status":"accepted"}""";

        await _service.SetAsync("notify", key, 202, body);
        var result = await _service.GetAsync("notify", key);

        Assert.NotNull(result);
        Assert.Equal(202, result.StatusCode);
        Assert.Equal(body, result.ResponseBody);
    }

    [Fact]
    public async Task Get_NotFound_ReturnsNull()
    {
        Assert.Null(await _service.GetAsync("nonexistent-scope", NewKey()));
    }

    [Fact]
    public async Task Set_Upsert_Overwrites()
    {
        var key = NewKey();
        await _service.SetAsync("notify", key, 202, "first");

        await _service.SetAsync("notify", key, 500, "error occurred");
        var result = await _service.GetAsync("notify", key);

        Assert.NotNull(result);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal("error occurred", result.ResponseBody);
    }

    [Fact]
    public async Task Get_AfterExpiry_ReturnsNull()
    {
        var key = NewKey();
        await _service.SetAsync("updown-ingest", key, 200, "");

        _time.Advance(Expiry);

        Assert.Null(await _service.GetAsync("updown-ingest", key));
    }

    // --- Claim / Complete / Release (API Idempotency-Key) ---

    [Fact]
    public async Task Claim_FreshKey_IsClaimed()
    {
        var claim = await _service.ClaimAsync("notify", NewKey());

        Assert.Equal(IdempotencyClaimStatus.Claimed, claim.Status);
    }

    [Fact]
    public async Task Claim_WhileTheFirstRequestIsInFlight_IsInProgress()
    {
        var key = NewKey();
        await _service.ClaimAsync("notify", key);

        var second = await _service.ClaimAsync("notify", key);

        Assert.Equal(IdempotencyClaimStatus.InProgress, second.Status);
    }

    [Fact]
    public async Task Claim_AfterComplete_ReplaysTheResponse()
    {
        var key = NewKey();
        var first = await _service.ClaimAsync("notify", key);
        await _service.CompleteAsync(first, 202, """{"messageId":"msg-1"}""");

        var second = await _service.ClaimAsync("notify", key);

        Assert.Equal(IdempotencyClaimStatus.Completed, second.Status);
        Assert.Equal(202, second.Result!.StatusCode);
        Assert.Equal("""{"messageId":"msg-1"}""", second.Result.ResponseBody);
    }

    [Fact]
    public async Task Claim_AfterRelease_IsClaimedAgain()
    {
        var key = NewKey();
        var first = await _service.ClaimAsync("notify", key);
        await _service.ReleaseAsync(first);

        var retry = await _service.ClaimAsync("notify", key);

        Assert.Equal(IdempotencyClaimStatus.Claimed, retry.Status);
    }

    [Fact]
    public async Task Claim_AfterExpiry_IsClaimedAgain_SoAResolvedIncidentCanRecur()
    {
        var key = NewKey();
        var first = await _service.ClaimAsync("notify", key);
        await _service.CompleteAsync(first, 202, "{}");

        _time.Advance(Expiry);
        var later = await _service.ClaimAsync("notify", key);

        Assert.Equal(IdempotencyClaimStatus.Claimed, later.Status);
    }

    [Fact]
    public async Task Claim_JustBeforeExpiry_StillReplays()
    {
        var key = NewKey();
        var first = await _service.ClaimAsync("notify", key);
        await _service.CompleteAsync(first, 202, "{}");

        _time.Advance(Expiry - TimeSpan.FromMinutes(1));

        Assert.Equal(IdempotencyClaimStatus.Completed, (await _service.ClaimAsync("notify", key)).Status);
    }

    [Fact]
    public async Task Claim_AbandonedPendingClaim_CanBeTakenOver()
    {
        var key = NewKey();
        await _service.ClaimAsync("notify", key); // never completed nor released: the request died

        _time.Advance(IdempotencyService.PendingTimeout);
        var retry = await _service.ClaimAsync("notify", key);

        Assert.Equal(IdempotencyClaimStatus.Claimed, retry.Status);
    }

    [Fact]
    public async Task LateCompletion_AfterATakeover_LeavesTheNewClaimAlone()
    {
        var key = NewKey();
        var stale = await _service.ClaimAsync("notify", key);
        _time.Advance(IdempotencyService.PendingTimeout);
        var fresh = await _service.ClaimAsync("notify", key);
        Assert.Equal(IdempotencyClaimStatus.Claimed, fresh.Status);

        await _service.CompleteAsync(stale, 202, """{"messageId":"msg-stale"}""");

        // Still the fresh request's pending claim, not the stale response.
        Assert.Equal(IdempotencyClaimStatus.InProgress, (await _service.ClaimAsync("notify", key)).Status);
        await _service.CompleteAsync(fresh, 202, """{"messageId":"msg-fresh"}""");
        Assert.Contains("msg-fresh", (await _service.ClaimAsync("notify", key)).Result!.ResponseBody);
    }

    [Fact]
    public async Task LateRelease_AfterATakeover_LeavesTheNewClaimAlone()
    {
        var key = NewKey();
        var stale = await _service.ClaimAsync("notify", key);
        _time.Advance(IdempotencyService.PendingTimeout);
        await _service.ClaimAsync("notify", key);

        await _service.ReleaseAsync(stale);

        Assert.Equal(IdempotencyClaimStatus.InProgress, (await _service.ClaimAsync("notify", key)).Status);
    }

    [Fact]
    public async Task Claim_ExpiryCountsFromTheClaim_NotFromCompletion()
    {
        var key = NewKey();
        var first = await _service.ClaimAsync("notify", key);
        _time.Advance(TimeSpan.FromMinutes(2));
        await _service.CompleteAsync(first, 202, "{}");

        _time.Advance(Expiry - TimeSpan.FromMinutes(1));

        Assert.Equal(IdempotencyClaimStatus.Claimed, (await _service.ClaimAsync("notify", key)).Status);
    }

    [Fact]
    public async Task Claim_ConcurrentDuplicates_ExactlyOneIsClaimed()
    {
        var key = NewKey();

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _service.ClaimAsync("notify", key)));

        Assert.Single(claims, c => c.Status == IdempotencyClaimStatus.Claimed);
        Assert.All(claims.Where(c => c.Status != IdempotencyClaimStatus.Claimed),
            c => Assert.Equal(IdempotencyClaimStatus.InProgress, c.Status));
    }

    [Fact]
    public async Task Claim_SameKeyInAnotherScope_DoesNotCollide()
    {
        var key = NewKey();
        await _service.ClaimAsync("notify", key);

        Assert.Equal(IdempotencyClaimStatus.Claimed, (await _service.ClaimAsync("send", key)).Status);
    }

    // --- Purge ---

    [Fact]
    public async Task Purge_DeletesExpiredRecordsOfEveryScope_AndKeepsTheRest()
    {
        var expiredNotify = NewKey();
        var expiredUpdown = NewKey();
        var fresh = NewKey();
        await _service.CompleteAsync(await _service.ClaimAsync("notify", expiredNotify), 202, "{}");
        await _service.SetAsync("updown-ingest", expiredUpdown, 200, "");

        _time.Advance(Expiry);
        await _service.SetAsync("updown-ingest", fresh, 200, "");

        await _service.PurgeExpiredAsync(TestContext.Current.CancellationToken);

        Assert.False(await ExistsAsync("notify", expiredNotify));
        Assert.False(await ExistsAsync("updown-ingest", expiredUpdown));
        Assert.True(await ExistsAsync("updown-ingest", fresh));
    }

    [Fact]
    public async Task Purge_KeepsARecordThatAClaimTookOverWhileThePurgeRan()
    {
        var key = NewKey();
        await _service.CompleteAsync(await _service.ClaimAsync("notify", key), 202, "{}");
        _time.Advance(Expiry);

        // A TableClient that lets a fresh claim take the expired record over between the purge's
        // query and its delete, the race a purge must survive.
        var racing = new Moq.Mock<TableClient>();
        racing.Setup(t => t.QueryAsync<TableEntity>(
                Moq.It.IsAny<string>(), Moq.It.IsAny<int?>(), Moq.It.IsAny<IEnumerable<string>>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((string filter, int? max, IEnumerable<string> select, CancellationToken ct) =>
                _tableClient.QueryAsync<TableEntity>(filter, max, select, ct));
        racing.Setup(t => t.DeleteEntityAsync(
                Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), Moq.It.IsAny<Azure.ETag>(), Moq.It.IsAny<CancellationToken>()))
            .Returns(async (string pk, string rk, Azure.ETag ifMatch, CancellationToken ct) =>
            {
                if (pk == "notify" && rk == key)
                    Assert.Equal(IdempotencyClaimStatus.Claimed, (await _service.ClaimAsync("notify", key)).Status);
                return await _tableClient.DeleteEntityAsync(pk, rk, ifMatch, ct);
            });
        var purging = new IdempotencyService(racing.Object, Expiry, _time, NullLogger<IdempotencyService>.Instance);

        await purging.PurgeExpiredAsync(TestContext.Current.CancellationToken);

        // The fresh claim survived, so a duplicate still sees it in progress.
        Assert.Equal(IdempotencyClaimStatus.InProgress, (await _service.ClaimAsync("notify", key)).Status);
    }

    private async Task<bool> ExistsAsync(string scope, string key) =>
        (await _tableClient.GetEntityIfExistsAsync<TableEntity>(scope, key,
            cancellationToken: TestContext.Current.CancellationToken)).HasValue;

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
