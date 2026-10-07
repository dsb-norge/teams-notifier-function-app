using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;

namespace TeamsNotificationBot.Functions;

public class QueueProcessorFunction
{
    private readonly IBotService _botService;
    private readonly IAliasService _aliasService;
    private readonly IDeliveryRecords _records;
    private readonly IDeliveryEvents _events;
    private readonly ILogger<QueueProcessorFunction> _logger;

    public QueueProcessorFunction(
        IBotService botService,
        IAliasService aliasService,
        IDeliveryRecords records,
        IDeliveryEvents events,
        ILogger<QueueProcessorFunction> logger)
    {
        _botService = botService;
        _aliasService = aliasService;
        _records = records;
        _events = events;
        _logger = logger;
    }

    [Function("QueueProcessor")]
    public async Task Run(
        [QueueTrigger("notifications")] string messageJson,
        FunctionContext context)
    {
        QueueMessage? queueMessage;
        try
        {
            queueMessage = JsonSerializer.Deserialize<QueueMessage>(messageJson);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize queue message");
            return;
        }

        if (queueMessage == null)
        {
            _logger.LogError("Queue message deserialized to null");
            return;
        }

        var dequeueCount = GetDequeueCount(context);
        _logger.LogInformation(
            "Processing queue message. MessageId={MessageId}, Alias={Alias}, Format={Format}, DequeueCount={DequeueCount}",
            queueMessage.MessageId, queueMessage.Alias, queueMessage.Format, dequeueCount);

        // The queue delivers at least once: a retry after a successful send must not post twice.
        var record = await _records.GetAsync(queueMessage.MessageId);
        if (record?.Status == DeliveryStatus.Delivered)
        {
            _logger.LogInformation("Already delivered; skipping. MessageId={MessageId}", queueMessage.MessageId);
            return;
        }

        var teamsDisabled = string.Equals(
            Environment.GetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (teamsDisabled)
        {
            // Offline mode: resolve and log where the message would go, and stop.
            if (await ResolveReportingFailuresAsync(queueMessage, dequeueCount) is { } offline)
            {
                _logger.LogInformation(
                    "Teams integration disabled. Message would be sent to {PK}/{RK}. MessageId={MessageId}, Format={Format}",
                    offline.PartitionKey, offline.RowKey, queueMessage.MessageId, queueMessage.Format);
            }
            return;
        }

        if (await ResolveReportingFailuresAsync(queueMessage, dequeueCount) is not { } key)
            return; // failed for good, already reported

        SentActivity sent = null!;
        await DeliverAsync(queueMessage, dequeueCount, key, async () => sent = await _botService.SendAsync(
            key.PartitionKey, key.RowKey, queueMessage.Format, queueMessage.Message));
        var outcome = new DeliveryOutcome(PostedAs.Post, key, sent.ConversationId, sent.ActivityId, sent.ActivityId);

        _logger.LogInformation(
            "Message delivered successfully. MessageId={MessageId}, PK={PK}, RK={RK}, Format={Format}, PostedAs={PostedAs}",
            queueMessage.MessageId, outcome.ConversationKey.PartitionKey, outcome.ConversationKey.RowKey,
            queueMessage.Format, outcome.PostedAs);
        await _records.MarkDeliveredAsync(queueMessage, outcome);
        _events.Delivered(queueMessage, outcome.ConversationKey.PartitionKey, outcome.ConversationKey.RowKey, dequeueCount);
    }

    /// <summary>
    /// Runs the Teams call. A failure is reported and rethrown, so the queue retries it and, after
    /// the last attempt, moves it to the poison queue.
    /// </summary>
    private async Task DeliverAsync(
        QueueMessage queueMessage, long dequeueCount, (string PartitionKey, string RowKey) key, Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to deliver message. MessageId={MessageId}, PK={PK}, RK={RK}, Format={Format}",
                queueMessage.MessageId, key.PartitionKey, key.RowKey, queueMessage.Format);
            _events.DeliveryFailed(queueMessage, dequeueCount, ex.GetType().Name, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// <see cref="ResolveDestinationAsync"/>, inside the failure report: a storage error or a
    /// malformed alias is retried like a failed send, and the delivery trail must show it.
    /// </summary>
    private async Task<(string PartitionKey, string RowKey)?> ResolveReportingFailuresAsync(
        QueueMessage queueMessage, long dequeueCount)
    {
        try
        {
            return await ResolveDestinationAsync(queueMessage, dequeueCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve the target. MessageId={MessageId}", queueMessage.MessageId);
            _events.DeliveryFailed(queueMessage, dequeueCount, ex.GetType().Name, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// The conversation a new post goes to: the direct target, or the alias's conversation. Null
    /// when the message has no target or its alias no longer exists, which fails it for good.
    /// </summary>
    private async Task<(string PartitionKey, string RowKey)?> ResolveDestinationAsync(QueueMessage queueMessage, long dequeueCount)
    {
        if (queueMessage.Target != null)
        {
            var key = ResolveTarget(queueMessage.Target);
            _logger.LogInformation(
                "Direct target resolved. Type={Type}, PK={PK}, RK={RK}, MessageId={MessageId}",
                queueMessage.Target.Type, key.partitionKey, key.rowKey, queueMessage.MessageId);
            return key;
        }

        if (string.IsNullOrEmpty(queueMessage.Alias))
        {
            await FailPermanentlyAsync(queueMessage, dequeueCount, "NoTarget",
                "The message has neither a target nor an alias.");
            return null;
        }

        var alias = await _aliasService.GetAliasAsync(queueMessage.Alias);
        if (alias == null)
        {
            await FailPermanentlyAsync(queueMessage, dequeueCount, "UnknownAlias",
                $"Alias '{queueMessage.Alias}' no longer exists; the message was dropped.");
            return null;
        }

        var aliasKey = alias.ConversationKey() ?? throw new InvalidOperationException(
            $"Alias '{alias.RowKey}' has no valid target (type '{alias.TargetType}').");
        _logger.LogInformation(
            "Alias resolved. Alias={Alias}, Type={Type}, PK={PK}, RK={RK}, MessageId={MessageId}",
            queueMessage.Alias, alias.TargetType, aliasKey.PartitionKey, aliasKey.RowKey, queueMessage.MessageId);
        return aliasKey;
    }

    /// <summary>A failure no retry can fix: recorded as failed, with no retry and no poison-queue alert.</summary>
    private async Task FailPermanentlyAsync(QueueMessage queueMessage, long dequeueCount, string errorType, string error)
    {
        _logger.LogError("{Error} MessageId={MessageId}", LogSanitizer.Sanitize(error), queueMessage.MessageId);
        await _records.MarkFailedAsync(queueMessage, error);
        _events.DeliveryFailed(queueMessage, dequeueCount, errorType, error);
    }

    /// <summary>
    /// How many times the queue has handed out this message, including this time (1 on the first
    /// attempt). 0 when the binding data doesn't carry it (or, in tests, there is no context).
    /// </summary>
    private static long GetDequeueCount(FunctionContext? context) =>
        context?.BindingContext?.BindingData is { } data &&
        data.TryGetValue("DequeueCount", out var value) &&
        long.TryParse(value?.ToString(), out var count)
            ? count
            : 0;

    private static (string partitionKey, string rowKey) ResolveTarget(MessageTarget target)
    {
        return target.Type switch
        {
            "channel" => (target.TeamId ?? throw new InvalidOperationException("TeamId required for channel target"),
                          target.ChannelId ?? throw new InvalidOperationException("ChannelId required for channel target")),
            "personal" => ("user", target.UserId ?? throw new InvalidOperationException("UserId required for personal target")),
            "groupChat" => ("chat", target.ChatId ?? throw new InvalidOperationException("ChatId required for groupChat target")),
            _ => throw new InvalidOperationException($"Unknown target type: {target.Type}")
        };
    }
}
