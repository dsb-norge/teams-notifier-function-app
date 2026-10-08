using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public class DeliveryRecords : IDeliveryRecords
{
    /// <summary>
    /// A send claim older than this was abandoned (the function timeout is 5 minutes) and may be
    /// taken over.
    /// </summary>
    public static readonly TimeSpan SendLease = TimeSpan.FromMinutes(5);

    // App Insights and the status endpoint show this; an exception message has no bound of its own.
    private const int MaxErrorLength = 1024;

    private readonly TableClient _tableClient;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _time;
    private readonly ILogger<DeliveryRecords> _logger;

    public DeliveryRecords(TableClient tableClient)
        : this(tableClient, DeliveryRecordsConfig.Retention, TimeProvider.System, NullLogger<DeliveryRecords>.Instance)
    {
    }

    public DeliveryRecords(TableClient tableClient, TimeSpan retention, TimeProvider time, ILogger<DeliveryRecords> logger)
    {
        _tableClient = tableClient;
        _retention = retention;
        _time = time;
        _logger = logger;
    }

    public async Task<ETag> CreateAsync(QueueMessage message)
    {
        var response = await _tableClient.UpsertEntityAsync(NewRecord(message, DeliveryStatus.Queued), TableUpdateMode.Replace);
        return response.Headers.ETag ?? default;
    }

    public async Task<DeliveryRecordEntity?> GetAsync(string messageId)
    {
        if (string.IsNullOrEmpty(messageId))
            return null;

        try
        {
            var record = (await _tableClient.GetEntityAsync<DeliveryRecordEntity>(messageId, string.Empty)).Value;
            return record.EnqueuedAt + _retention <= _time.GetUtcNow() ? null : record;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<SendClaim> ClaimSendAsync(QueueMessage message, DeliveryRecordEntity? current)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var now = _time.GetUtcNow();
            if (current?.Status == DeliveryStatus.Delivered)
                return new SendClaim(SendClaimStatus.AlreadyDelivered);
            if (current is { Status: DeliveryStatus.Sending, SendingAt: { } since } && since + SendLease > now)
                return new SendClaim(SendClaimStatus.InProgress);

            var sending = NewRecord(message, DeliveryStatus.Sending);
            sending.SendingAt = now;
            try
            {
                // Insert-only or ETag-guarded: of two copies claiming at once, exactly one gets here.
                var response = current == null
                    ? await _tableClient.AddEntityAsync(sending)
                    : await _tableClient.UpdateEntityAsync(sending, current.ETag, TableUpdateMode.Replace);
                return new SendClaim(SendClaimStatus.Claimed, response.Headers.ETag ?? default);
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 409 or 412)
            {
                // Changed since it was read: decide again from what is stored now, whatever its
                // age (an expired row still occupies the key).
                current = await ReadAsync(message.MessageId);
            }
        }

        // Still contended after several attempts: another copy is busy with it.
        return new SendClaim(SendClaimStatus.InProgress);
    }

    public async Task ReleaseSendAsync(QueueMessage message, SendClaim claim)
    {
        try
        {
            await _tableClient.UpdateEntityAsync(NewRecord(message, DeliveryStatus.Queued),
                claim.ETag == default ? ETag.All : claim.ETag, TableUpdateMode.Replace);
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            // Gone or no longer ours: nothing to give back.
        }
        catch (Exception ex)
        {
            // Side concern (docs/contributing.md §5): this runs while the send failure propagates.
            // An unreleased claim only delays the retry until SendLease runs out.
            _logger.LogWarning(ex, "Could not release a send claim. MessageId={MessageId}", message.MessageId);
        }
    }

    public async Task MarkDeliveredAsync(QueueMessage message, DeliveryOutcome outcome)
    {
        var record = NewRecord(message, DeliveryStatus.Delivered);
        record.PostedAs = outcome.PostedAs;
        SetTarget(record, outcome.ConversationKey);
        record.ConversationId = outcome.ConversationId;
        record.ActivityId = outcome.ActivityId;
        record.ThreadActivityId = outcome.ThreadActivityId;
        record.UnresolvedMentions = outcome.UnresolvedMentions.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(outcome.UnresolvedMentions)
            : null;
        record.DeliveredAt = _time.GetUtcNow();

        // Replace, built from the queue message: also right for a message queued before records
        // existed, and clears the error of an attempt that failed before a manual retry.
        await WriteQuietlyAsync(record, "delivered");
    }

    public async Task MarkFailedAsync(QueueMessage message, string error)
    {
        var failed = NewRecord(message, DeliveryStatus.Failed);
        failed.Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;

        try
        {
            // Never over a delivered record: a late poison notification (a retried message whose
            // poison copy wasn't deleted) must not turn a delivery into a failure. ETag-guarded, so
            // a delivery that lands between the read and the write wins.
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var current = await ReadAsync(message.MessageId);
                if (current?.Status == DeliveryStatus.Delivered)
                {
                    _logger.LogInformation("Already delivered; not recording it as failed. MessageId={MessageId}",
                        message.MessageId);
                    return;
                }

                // Nor over a live send claim: another copy is sending it right now, and erasing the
                // claim would let a held copy post it concurrently. If that send fails for good,
                // it records the failure itself.
                if (current is { Status: DeliveryStatus.Sending, SendingAt: { } since } && since + SendLease > _time.GetUtcNow())
                {
                    _logger.LogInformation("Being sent by another copy; not recording it as failed. MessageId={MessageId}",
                        message.MessageId);
                    return;
                }

                try
                {
                    if (current == null)
                        await _tableClient.AddEntityAsync(failed);
                    else
                        await _tableClient.UpdateEntityAsync(failed, current.ETag, TableUpdateMode.Replace);
                    return;
                }
                catch (RequestFailedException ex) when (ex.Status is 404 or 409 or 412)
                {
                    // Changed since the read; look again.
                }
            }
            _logger.LogWarning("Could not record the message as failed after {Attempts} conflicts. MessageId={MessageId}",
                maxAttempts, message.MessageId);
        }
        catch (Exception ex)
        {
            // Side concern (docs/contributing.md §5): the failure itself is already reported.
            _logger.LogWarning(ex, "Could not record the message as failed. MessageId={MessageId}", message.MessageId);
        }
    }

    public async Task DeleteAsync(string messageId, ETag createdETag)
    {
        try
        {
            await _tableClient.DeleteEntityAsync(messageId, string.Empty,
                createdETag == default ? ETag.All : createdETag);
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            // Already gone, or claimed or delivered meanwhile (the message was queued after all): keep it.
        }
        catch (Exception ex)
        {
            // Side concern (docs/contributing.md §5): this runs while the enqueue failure propagates,
            // and must not replace it. A leftover record says "queued" for a message that never was;
            // it expires in time.
            _logger.LogWarning(ex, "Could not remove the delivery record of an unqueued message. MessageId={MessageId}", messageId);
        }
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var cutoff = now - _retention;
        var deleted = 0;
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            // "le", matching GetAsync: a record is expired from the instant it is exactly _retention old.
            TableClient.CreateQueryFilter($"EnqueuedAt le {cutoff}"),
            select: ["PartitionKey", "RowKey", "Status", "SendingAt"],
            cancellationToken: cancellationToken))
        {
            // A message delayed past a short retention can still be sending: deleting its claim
            // would let another copy claim and post it concurrently.
            if (entity.GetString("Status") == DeliveryStatus.Sending &&
                entity.GetDateTimeOffset("SendingAt") is { } since && since + SendLease > now)
                continue;

            try
            {
                // Guarded by the ETag the query read, so a claim taken since survives too.
                await _tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag, cancellationToken);
                deleted++;
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 412)
            {
                // Deleted (404) or changed (412) since the query read it.
            }
        }
        return deleted;
    }

    private async Task WriteQuietlyAsync(DeliveryRecordEntity record, string state)
    {
        try
        {
            await _tableClient.UpsertEntityAsync(record, TableUpdateMode.Replace);
        }
        catch (Exception ex)
        {
            // Side concern (docs/contributing.md §5): this runs after Teams accepted the message.
            // Throwing would make the queue retry it into a second post.
            _logger.LogWarning(ex, "Could not record the message as {State}. MessageId={MessageId}",
                state, record.PartitionKey);
        }
    }

    /// <summary>The stored row, whatever its age, or null.</summary>
    private async Task<DeliveryRecordEntity?> ReadAsync(string messageId)
    {
        try
        {
            return (await _tableClient.GetEntityAsync<DeliveryRecordEntity>(messageId, string.Empty)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static DeliveryRecordEntity NewRecord(QueueMessage message, string status) => new()
    {
        PartitionKey = message.MessageId,
        RowKey = string.Empty,
        Status = status,
        Source = message.Source,
        PrincipalId = message.PrincipalId,
        Alias = message.Alias?.ToLowerInvariant(),
        RequestedTarget = message.Target?.Key(),
        ReplyTo = message.ReplyTo,
        Update = message.Update,
        EnqueuedAt = message.EnqueuedAt
    };

    private static void SetTarget(DeliveryRecordEntity record, (string PartitionKey, string RowKey) key)
    {
        record.TargetType = ConversationReferenceEntity.TargetTypeOf(key.PartitionKey);
        switch (record.TargetType)
        {
            case "personal":
                record.UserId = key.RowKey;
                break;
            case "groupChat":
                record.ChatId = key.RowKey;
                break;
            default:
                record.TeamId = key.PartitionKey;
                record.ChannelId = key.RowKey;
                break;
        }
    }
}
