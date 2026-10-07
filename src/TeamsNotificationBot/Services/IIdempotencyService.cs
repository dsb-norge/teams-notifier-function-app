namespace TeamsNotificationBot.Services;

/// <summary>
/// Idempotency records in the <c>idempotencykeys</c> table, keyed by scope (partition) and key
/// (row). Records expire: one older than the configured expiry reads as absent, and
/// <see cref="PurgeExpiredAsync"/> deletes it.
/// </summary>
public interface IIdempotencyService
{
    /// <summary>A completed, unexpired record, or null. Used by the updown ingress to deduplicate retries.</summary>
    Task<IdempotencyResult?> GetAsync(string scope, string key);

    /// <summary>Writes a completed record, replacing any record under the same key.</summary>
    Task SetAsync(string scope, string key, int statusCode, string responseBody);

    /// <summary>
    /// Atomically claims <paramref name="key"/> for one API request before it queues anything.
    /// Returns <see cref="IdempotencyClaimStatus.Claimed"/> when this request owns the key (no
    /// record, or only an expired or abandoned one), <see cref="IdempotencyClaimStatus.Completed"/>
    /// with the response to replay, or <see cref="IdempotencyClaimStatus.InProgress"/> when another
    /// request holds the key right now.
    /// </summary>
    Task<IdempotencyClaim> ClaimAsync(string scope, string key);

    /// <summary>
    /// Records the response of a claimed request so later duplicates replay it, if the claim is still
    /// this request's. Never throws: the message is already queued, so any failure here is logged and
    /// the request still succeeds.
    /// </summary>
    Task CompleteAsync(IdempotencyClaim claim, int statusCode, string responseBody);

    /// <summary>
    /// Gives up a claim when the request failed before queuing, so the caller can retry with the
    /// same key, if the claim is still this request's. Never throws: it runs while the original
    /// failure propagates.
    /// </summary>
    Task ReleaseAsync(IdempotencyClaim claim);

    /// <summary>Deletes every expired record, whatever its scope. Returns how many were deleted.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

public class IdempotencyResult
{
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
}

public enum IdempotencyClaimStatus
{
    Claimed,
    Completed,
    InProgress
}

/// <param name="Result">The response to replay; set only when <paramref name="Status"/> is Completed.</param>
/// <param name="ETag">
/// The record's ETag when this request claimed it. Completing and releasing are conditional on it,
/// so a request whose claim was taken over (after <c>PendingTimeout</c>) can't overwrite or delete
/// the newer claim with a late write.
/// </param>
public sealed record IdempotencyClaim(
    IdempotencyClaimStatus Status, string Scope, string Key, DateTimeOffset ClaimedAt,
    IdempotencyResult? Result = null, Azure.ETag ETag = default);
