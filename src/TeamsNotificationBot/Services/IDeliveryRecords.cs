using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

/// <summary>
/// Delivery records in the <c>deliveryrecords</c> table: one per queued message, from
/// <c>queued</c> to <c>delivered</c> or <c>failed</c>. Records expire after the configured
/// retention: an expired record reads as absent, and <see cref="PurgeExpiredAsync"/> deletes it.
/// </summary>
public interface IDeliveryRecords
{
    /// <summary>
    /// Records <paramref name="message"/> as queued and returns the record's ETag. Throws on a
    /// storage failure, before anything is queued.
    /// </summary>
    Task<Azure.ETag> CreateAsync(QueueMessage message);

    /// <summary>The record, or null when the message is unknown or its record expired.</summary>
    Task<DeliveryRecordEntity?> GetAsync(string messageId);

    /// <summary>
    /// Atomically claims the send of <paramref name="message"/>, right before the Teams call, so
    /// two copies of one queue message (the queue delivers at least once, and a held message is put
    /// back as a new copy) can't both post it. <paramref name="current"/> is the record as last read.
    /// A claim older than <c>DeliveryRecords.SendLease</c> was abandoned and can be taken over.
    /// </summary>
    Task<SendClaim> ClaimSendAsync(QueueMessage message, DeliveryRecordEntity? current);

    /// <summary>
    /// Gives a claim back after the send failed, so the queue's retry can claim it at once. Only
    /// while the claim is still this one's. Never throws.
    /// </summary>
    Task ReleaseSendAsync(QueueMessage message, SendClaim claim);

    /// <summary>
    /// Records a successful delivery. Never throws: the message is already in Teams, and a
    /// failure here must not make the queue retry it into a second post.
    /// </summary>
    Task MarkDeliveredAsync(QueueMessage message, DeliveryOutcome outcome);

    /// <summary>
    /// Records a failure that won't be retried, unless the message was delivered meanwhile or another
    /// copy holds a live send claim: neither is turned into a failure. Never throws.
    /// </summary>
    Task MarkFailedAsync(QueueMessage message, string error);

    /// <summary>
    /// Removes the record of a message whose enqueue failed, but only while it is still the record
    /// <see cref="CreateAsync"/> wrote (<paramref name="createdETag"/>): an enqueue can fail after
    /// the message was in fact queued (a lost response), and a claim or delivery recorded since must
    /// stay. Never throws.
    /// </summary>
    Task DeleteAsync(string messageId, Azure.ETag createdETag);

    /// <summary>Deletes every expired record. Returns how many were deleted.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

public enum SendClaimStatus
{
    /// <summary>This invocation may send.</summary>
    Claimed,

    /// <summary>Another copy delivered it already: don't send.</summary>
    AlreadyDelivered,

    /// <summary>Another copy is sending it right now: don't send; look again later.</summary>
    InProgress
}

/// <param name="ETag">The record's ETag after the claim, for a conditional release.</param>
public sealed record SendClaim(SendClaimStatus Status, Azure.ETag ETag = default);

/// <summary>Where and how a message was delivered.</summary>
/// <param name="ConversationKey">The <c>conversationreferences</c> key of the conversation it went to.</param>
/// <param name="ConversationId">The Teams conversation the activity lives in (threaded for a reply).</param>
/// <param name="ThreadActivityId">The activity that roots the thread the message is in.</param>
public sealed record DeliveryOutcome(
    string PostedAs,
    (string PartitionKey, string RowKey) ConversationKey,
    string ConversationId,
    string? ActivityId,
    string? ThreadActivityId);
