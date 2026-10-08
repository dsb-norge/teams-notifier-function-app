using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;

namespace TeamsNotificationBot.Functions;

public class QueueProcessorFunction
{
    /// <summary>How long a reply or update waits for a parent that is still queued.</summary>
    public static readonly TimeSpan MaxParentWait = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a held message stays invisible before it looks again: a reply or update waiting for
    /// its parent, or a copy waiting while another copy of the same message is being sent.
    /// </summary>
    public static readonly TimeSpan HoldDelay = TimeSpan.FromSeconds(20);

    private readonly IBotService _botService;
    private readonly IAliasService _aliasService;
    private readonly IDeliveryRecords _records;
    private readonly INotificationQueue _queue;
    private readonly IDeliveryEvents _events;
    private readonly ILogger<QueueProcessorFunction> _logger;

    public QueueProcessorFunction(
        IBotService botService,
        IAliasService aliasService,
        IDeliveryRecords records,
        INotificationQueue queue,
        IDeliveryEvents events,
        ILogger<QueueProcessorFunction> logger)
    {
        _botService = botService;
        _aliasService = aliasService;
        _records = records;
        _queue = queue;
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

        // Every failure of this attempt is reported exactly once, here: reading records, resolving
        // the target, holding, claiming and sending alike. The queue then retries the message, and
        // after the last attempt moves it to the poison queue.
        try
        {
            await ProcessAsync(queueMessage, dequeueCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deliver message. MessageId={MessageId}, Format={Format}",
                queueMessage.MessageId, queueMessage.Format);
            _events.DeliveryFailed(queueMessage, dequeueCount, ex.GetType().Name, ex.Message);
            throw;
        }
    }

    private async Task ProcessAsync(QueueMessage queueMessage, long dequeueCount)
    {
        // The queue delivers at least once: a retry after a successful send must not post twice.
        // (Two copies racing are handled by the send claim below.)
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
            // Offline mode: resolve and log where a new post would go, and stop. Replies and updates
            // aren't simulated; nothing is ever delivered, so their parents would never be.
            if (await ResolveDestinationAsync(queueMessage, dequeueCount) is { } offline)
            {
                _logger.LogInformation(
                    "Teams integration disabled. Message would be sent to {PK}/{RK}. MessageId={MessageId}, Format={Format}",
                    offline.PartitionKey, offline.RowKey, queueMessage.MessageId, queueMessage.Format);
            }
            return;
        }

        // A reply or an update depends on the message it refers to (its parent).
        DeliveryRecordEntity? parent = null;
        var parentId = queueMessage.Update ?? queueMessage.ReplyTo;
        if (parentId != null)
        {
            parent = await _records.GetAsync(parentId);
            if (parent?.Status is DeliveryStatus.Queued or DeliveryStatus.Sending)
            {
                if (DateTimeOffset.UtcNow - queueMessage.EnqueuedAt < MaxParentWait)
                {
                    // Put back with a delay rather than throw, so waiting doesn't use up delivery
                    // attempts or end in the poison queue.
                    await _queue.RequeueAsync(queueMessage, HoldDelay);
                    _logger.LogInformation(
                        "Parent {ParentId} not delivered yet; holding. MessageId={MessageId}",
                        parentId, queueMessage.MessageId);
                    return;
                }

                _logger.LogWarning(
                    "Parent {ParentId} still not delivered after {MaxWait}; going ahead without it. MessageId={MessageId}",
                    parentId, MaxParentWait, queueMessage.MessageId);
            }
        }

        DeliveryOutcome outcome;
        if (queueMessage.Update != null)
        {
            if (parent is not { Status: DeliveryStatus.Delivered, ActivityId: { } activityId, ConversationId: { } conversationId }
                || parent.ConversationKey() is not { } parentKey)
            {
                await FailPermanentlyAsync(queueMessage, dequeueCount, "UpdateTargetNotDelivered",
                    $"Message '{queueMessage.Update}' was never delivered, so there is nothing to update.");
                return;
            }

            if (await ClaimSendAsync(queueMessage, record) is not { } claim)
                return;
            IReadOnlyList<string> unresolved = [];
            await SendUnderClaimAsync(queueMessage, claim, async () => unresolved = await _botService.UpdateAsync(
                parentKey.PartitionKey, parentKey.RowKey, conversationId, activityId, queueMessage.Format, queueMessage.Message,
                queueMessage.Mentions));
            outcome = new DeliveryOutcome(PostedAs.Update, parentKey, conversationId, activityId, parent.ThreadActivityId)
            {
                UnresolvedMentions = unresolved
            };
        }
        else
        {
            (string PartitionKey, string RowKey) key;
            string? threadActivityId = null;
            if (queueMessage.ReplyTo != null && parent is { Status: DeliveryStatus.Delivered } &&
                parent.ConversationKey() is { } parentKey)
            {
                // The thread stays where its parent went, even if the alias has been repointed since.
                // Only channels have threads; in a chat the reply is an ordinary message.
                key = parentKey;
                if (parent.TargetType == "channel")
                    threadActivityId = parent.ThreadActivityId ?? parent.ActivityId;
            }
            else if (await ResolveDestinationAsync(queueMessage, dequeueCount) is { } destination)
            {
                key = destination;
            }
            else
            {
                return; // failed permanently
            }

            if (await ClaimSendAsync(queueMessage, record) is not { } claim)
                return;
            SentActivity sent = null!;
            await SendUnderClaimAsync(queueMessage, claim, async () => sent = await _botService.SendAsync(
                key.PartitionKey, key.RowKey, queueMessage.Format, queueMessage.Message, threadActivityId,
                queueMessage.Mentions));
            outcome = new DeliveryOutcome(
                threadActivityId != null ? PostedAs.Reply : PostedAs.Post,
                key, sent.ConversationId, sent.ActivityId, threadActivityId ?? sent.ActivityId)
            {
                UnresolvedMentions = sent.UnresolvedMentions
            };
        }

        _logger.LogInformation(
            "Message delivered successfully. MessageId={MessageId}, PK={PK}, RK={RK}, Format={Format}, PostedAs={PostedAs}, UnresolvedMentions={Unresolved}",
            queueMessage.MessageId, outcome.ConversationKey.PartitionKey, outcome.ConversationKey.RowKey,
            queueMessage.Format, outcome.PostedAs, outcome.UnresolvedMentions.Count);
        await _records.MarkDeliveredAsync(queueMessage, outcome);
        _events.Delivered(queueMessage, outcome.ConversationKey.PartitionKey, outcome.ConversationKey.RowKey, dequeueCount);
    }

    /// <summary>
    /// Claims the send right before the Teams call, so two copies of one queue message can't both
    /// post it. Null when this invocation must not send: another copy delivered it already
    /// (skipped), or is sending it right now (held, to look again later).
    /// </summary>
    private async Task<SendClaim?> ClaimSendAsync(QueueMessage queueMessage, DeliveryRecordEntity? record)
    {
        var claim = await _records.ClaimSendAsync(queueMessage, record);
        switch (claim.Status)
        {
            case SendClaimStatus.Claimed:
                return claim;
            case SendClaimStatus.AlreadyDelivered:
                _logger.LogInformation("Delivered by another copy; skipping. MessageId={MessageId}", queueMessage.MessageId);
                return null;
            default:
                await _queue.RequeueAsync(queueMessage, HoldDelay);
                _logger.LogInformation("Another copy is sending it; holding. MessageId={MessageId}", queueMessage.MessageId);
                return null;
        }
    }

    /// <summary>
    /// The Teams call, under the send claim. A failure gives the claim back, so the queue's retry
    /// can claim it at once instead of waiting out the lease.
    /// </summary>
    private async Task SendUnderClaimAsync(QueueMessage queueMessage, SendClaim claim, Func<Task> send)
    {
        try
        {
            await send();
        }
        catch
        {
            await _records.ReleaseSendAsync(queueMessage, claim);
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
