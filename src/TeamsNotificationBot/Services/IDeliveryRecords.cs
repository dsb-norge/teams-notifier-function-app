using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

/// <summary>
/// Delivery records in the <c>deliveryrecords</c> table: one per queued message, from
/// <c>queued</c> to <c>delivered</c> or <c>failed</c>. Records expire after the configured
/// retention: an expired record reads as absent, and <see cref="PurgeExpiredAsync"/> deletes it.
/// </summary>
public interface IDeliveryRecords
{
    /// <summary>Records <paramref name="message"/> as queued. Throws on a storage failure, before anything is queued.</summary>
    Task CreateAsync(QueueMessage message);

    /// <summary>The record, or null when the message is unknown or its record expired.</summary>
    Task<DeliveryRecordEntity?> GetAsync(string messageId);

    /// <summary>
    /// Records a successful delivery. Never throws: the message is already in Teams, and a
    /// failure here must not make the queue retry it into a second post.
    /// </summary>
    Task MarkDeliveredAsync(QueueMessage message, DeliveryOutcome outcome);

    /// <summary>
    /// Records a failure that won't be retried, unless the message was delivered meanwhile: a
    /// delivered record is never turned into a failure. Never throws.
    /// </summary>
    Task MarkFailedAsync(QueueMessage message, string error);

    /// <summary>Removes the record of a message that never got queued. Never throws.</summary>
    Task DeleteAsync(string messageId);

    /// <summary>Deletes every expired record. Returns how many were deleted.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

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
