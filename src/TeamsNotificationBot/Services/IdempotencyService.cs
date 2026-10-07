using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsNotificationBot.Helpers;

namespace TeamsNotificationBot.Services;

public class IdempotencyService : IIdempotencyService
{
    /// <summary>
    /// A pending claim older than this was abandoned by a request that died between claiming and
    /// completing, and may be claimed again. The function timeout (host.json) is 5 minutes, so a
    /// live request never holds a claim this long.
    /// </summary>
    public static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(5);

    private const string StatePending = "pending";
    private const string StateCompleted = "completed";

    // Each lost race means another request changed the record in between; after a few, report
    // the key as busy rather than spin.
    private const int MaxClaimAttempts = 3;

    private readonly TableClient _tableClient;
    private readonly TimeSpan _expiry;
    private readonly TimeProvider _time;
    private readonly ILogger<IdempotencyService> _logger;

    public IdempotencyService(TableClient tableClient)
        : this(tableClient, IdempotencyConfig.Expiry, TimeProvider.System, NullLogger<IdempotencyService>.Instance)
    {
    }

    public IdempotencyService(
        TableClient tableClient, TimeSpan expiry, TimeProvider time, ILogger<IdempotencyService> logger)
    {
        _tableClient = tableClient;
        _expiry = expiry;
        _time = time;
        _logger = logger;
    }

    public async Task<IdempotencyResult?> GetAsync(string scope, string key)
    {
        var entity = await TryGetAsync(scope, key);
        return entity == null || IsExpired(entity, _time.GetUtcNow()) ? null : ToResult(entity);
    }

    public async Task SetAsync(string scope, string key, int statusCode, string responseBody)
    {
        await _tableClient.UpsertEntityAsync(
            Completed(scope, key, statusCode, responseBody, _time.GetUtcNow()), TableUpdateMode.Replace);
    }

    public async Task<IdempotencyClaim> ClaimAsync(string scope, string key)
    {
        for (var attempt = 1; attempt <= MaxClaimAttempts; attempt++)
        {
            var now = _time.GetUtcNow();
            try
            {
                // Insert-only: of two concurrent first requests, exactly one gets here.
                var added = await _tableClient.AddEntityAsync(Pending(scope, key, now));
                return new IdempotencyClaim(IdempotencyClaimStatus.Claimed, scope, key, now, ETag: added.Headers.ETag ?? default);
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // A record exists; decide from what it says.
            }

            var existing = await TryGetAsync(scope, key);
            if (existing == null)
                continue; // released or purged since our insert failed

            if (IsExpired(existing, now) || IsAbandoned(existing, now))
            {
                try
                {
                    // ETag-guarded, so only one of several requests can take over a stale record.
                    var replaced = await _tableClient.UpdateEntityAsync(Pending(scope, key, now), existing.ETag, TableUpdateMode.Replace);
                    return new IdempotencyClaim(IdempotencyClaimStatus.Claimed, scope, key, now, ETag: replaced.Headers.ETag ?? default);
                }
                catch (RequestFailedException ex) when (ex.Status is 404 or 412)
                {
                    continue;
                }
            }

            return existing.GetString("State") == StatePending
                ? new IdempotencyClaim(IdempotencyClaimStatus.InProgress, scope, key, now)
                : new IdempotencyClaim(IdempotencyClaimStatus.Completed, scope, key, now, ToResult(existing));
        }

        return new IdempotencyClaim(IdempotencyClaimStatus.InProgress, scope, key, _time.GetUtcNow());
    }

    public async Task CompleteAsync(IdempotencyClaim claim, int statusCode, string responseBody)
    {
        try
        {
            // CreatedAt stays the claim time, so expiry counts from when the key was first used.
            // Conditional on the claim's ETag: a claim taken over since belongs to another request.
            await _tableClient.UpdateEntityAsync(
                Completed(claim.Scope, claim.Key, statusCode, responseBody, claim.ClaimedAt),
                OwnedBy(claim), TableUpdateMode.Replace);
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            _logger.LogWarning(
                "Queued, but the idempotency claim was taken over or removed meanwhile; left as it is. Scope={Scope}, Key={Key}",
                claim.Scope, claim.Key);
        }
        catch (Exception ex)
        {
            // Side concern (docs/contributing.md §5): the message is already queued, and failing the
            // request now would make the caller retry it into a second copy. A duplicate sees a
            // pending claim (409) until PendingTimeout, then queues again.
            _logger.LogWarning(ex,
                "Queued, but could not complete the idempotency record. Scope={Scope}, Key={Key}",
                claim.Scope, claim.Key);
        }
    }

    public async Task ReleaseAsync(IdempotencyClaim claim)
    {
        try
        {
            // Conditional on the claim's ETag: a claim taken over since belongs to another request.
            await _tableClient.DeleteEntityAsync(claim.Scope, claim.Key, OwnedBy(claim));
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            // Already gone, or no longer ours: nothing to release.
        }
        catch (Exception ex)
        {
            // Side concern (docs/contributing.md §5): this runs while the request's own failure
            // propagates, and must not replace it. The caller's retry sees a pending claim (409)
            // until PendingTimeout, then succeeds.
            _logger.LogWarning(ex,
                "Could not release an idempotency claim after a failed request. Scope={Scope}, Key={Key}",
                claim.Scope, claim.Key);
        }
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _time.GetUtcNow() - _expiry;
        var deleted = 0;
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            // "le", matching IsExpired: a record is expired from the instant it is exactly _expiry old.
            TableClient.CreateQueryFilter($"CreatedAt le {cutoff}"),
            select: ["PartitionKey", "RowKey"],
            cancellationToken: cancellationToken))
        {
            try
            {
                // Guarded by the ETag the query read: a claim may have taken the expired record
                // over since, and that one is live and must stay.
                await _tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag, cancellationToken);
                deleted++;
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 412)
            {
                // Deleted (404) or taken over (412) since the query read it.
            }
        }
        return deleted;
    }

    // A claim made by ClaimAsync always carries its ETag; one built elsewhere (tests) without one
    // falls back to unconditional.
    private static ETag OwnedBy(IdempotencyClaim claim) => claim.ETag == default ? ETag.All : claim.ETag;

    private async Task<TableEntity?> TryGetAsync(string scope, string key)
    {
        try
        {
            return (await _tableClient.GetEntityAsync<TableEntity>(scope, key)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    // A record without CreatedAt predates expiry tracking; treat it as long expired.
    private bool IsExpired(TableEntity entity, DateTimeOffset now) =>
        entity.GetDateTimeOffset("CreatedAt") is not { } createdAt || createdAt + _expiry <= now;

    private static bool IsAbandoned(TableEntity entity, DateTimeOffset now) =>
        entity.GetString("State") == StatePending &&
        entity.GetDateTimeOffset("CreatedAt") is { } createdAt &&
        createdAt + PendingTimeout <= now;

    private static IdempotencyResult ToResult(TableEntity entity) => new()
    {
        StatusCode = entity.GetInt32("StatusCode") ?? 0,
        ResponseBody = entity.GetString("ResponseBody") ?? ""
    };

    private static TableEntity Pending(string scope, string key, DateTimeOffset now) => new(scope, key)
    {
        ["State"] = StatePending,
        ["CreatedAt"] = now
    };

    // Records written before the State column existed are completed ones; a missing State reads
    // as completed too.
    private static TableEntity Completed(
        string scope, string key, int statusCode, string responseBody, DateTimeOffset createdAt) => new(scope, key)
    {
        ["State"] = StateCompleted,
        ["StatusCode"] = statusCode,
        ["ResponseBody"] = responseBody,
        ["CreatedAt"] = createdAt
    };
}
