using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public class DeliveryRecords : IDeliveryRecords
{
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

    public async Task CreateAsync(QueueMessage message)
    {
        await _tableClient.UpsertEntityAsync(NewRecord(message, DeliveryStatus.Queued), TableUpdateMode.Replace);
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

    public async Task MarkDeliveredAsync(QueueMessage message, DeliveryOutcome outcome)
    {
        var record = NewRecord(message, DeliveryStatus.Delivered);
        record.PostedAs = outcome.PostedAs;
        SetTarget(record, outcome.ConversationKey);
        record.ConversationId = outcome.ConversationId;
        record.ActivityId = outcome.ActivityId;
        record.ThreadActivityId = outcome.ThreadActivityId;
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

    public async Task DeleteAsync(string messageId)
    {
        try
        {
            await _tableClient.DeleteEntityAsync(messageId, string.Empty, ETag.All);
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
        var cutoff = _time.GetUtcNow() - _retention;
        var deleted = 0;
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            // "le", matching GetAsync: a record is expired from the instant it is exactly _retention old.
            TableClient.CreateQueryFilter($"EnqueuedAt le {cutoff}"),
            select: ["PartitionKey", "RowKey"],
            cancellationToken: cancellationToken))
        {
            try
            {
                await _tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, ETag.All, cancellationToken);
                deleted++;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Deleted since the query read it.
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
        EnqueuedAt = message.EnqueuedAt
    };

    private static void SetTarget(DeliveryRecordEntity record, (string PartitionKey, string RowKey) key)
    {
        switch (key.PartitionKey)
        {
            case "user":
                record.TargetType = "personal";
                record.UserId = key.RowKey;
                break;
            case "chat":
                record.TargetType = "groupChat";
                record.ChatId = key.RowKey;
                break;
            default:
                record.TargetType = "channel";
                record.TeamId = key.PartitionKey;
                record.ChannelId = key.RowKey;
                break;
        }
    }
}
