using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;

namespace TeamsNotificationBot.Functions;

public class QueueProcessorFunction
{
    private readonly IBotService _botService;
    private readonly IAliasService _aliasService;
    private readonly IDeliveryEvents _events;
    private readonly ILogger<QueueProcessorFunction> _logger;

    public QueueProcessorFunction(
        IBotService botService,
        IAliasService aliasService,
        IDeliveryEvents events,
        ILogger<QueueProcessorFunction> logger)
    {
        _botService = botService;
        _aliasService = aliasService;
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

        // Resolve the target inside the failure report too: a storage error or a malformed alias
        // is retried like a failed send, and the delivery trail must show it.
        (string PartitionKey, string RowKey)? destination;
        try
        {
            destination = await ResolveDestinationAsync(queueMessage, dequeueCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve the target. MessageId={MessageId}", queueMessage.MessageId);
            _events.DeliveryFailed(queueMessage, dequeueCount, ex.GetType().Name, ex.Message);
            throw;
        }
        if (destination is not { } key)
            return; // failed for good, already reported
        var (partitionKey, rowKey) = key;

        var teamsDisabled = string.Equals(
            Environment.GetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (teamsDisabled)
        {
            _logger.LogInformation(
                "Teams integration disabled. Message would be sent to {PK}/{RK}. MessageId={MessageId}, Format={Format}",
                partitionKey, rowKey, queueMessage.MessageId, queueMessage.Format);
            return;
        }

        try
        {
            if (queueMessage.Format == "adaptive-card")
            {
                using var cardDoc = JsonDocument.Parse(queueMessage.Message);
                var card = cardDoc.RootElement.Clone();
                await _botService.SendAdaptiveCardAsync(partitionKey, rowKey, card);
            }
            else
            {
                await _botService.SendMessageAsync(partitionKey, rowKey, queueMessage.Message);
            }

            _logger.LogInformation(
                "Message delivered successfully. MessageId={MessageId}, PK={PK}, RK={RK}, Format={Format}",
                queueMessage.MessageId, partitionKey, rowKey, queueMessage.Format);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to deliver message. MessageId={MessageId}, PK={PK}, RK={RK}, Format={Format}",
                queueMessage.MessageId, partitionKey, rowKey, queueMessage.Format);
            _events.DeliveryFailed(queueMessage, dequeueCount, ex.GetType().Name, ex.Message);
            throw;
        }

        _events.Delivered(queueMessage, partitionKey, rowKey, dequeueCount);
    }

    /// <summary>
    /// The conversation the message goes to: the direct target, or the alias's conversation. Null,
    /// after reporting the failure, when the message has no target or its alias no longer exists.
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
            _logger.LogError("Queue message has neither Target nor Alias. MessageId={MessageId}",
                queueMessage.MessageId);
            _events.DeliveryFailed(queueMessage, dequeueCount, "NoTarget",
                "The message has neither a target nor an alias.");
            return null;
        }

        var alias = await _aliasService.GetAliasAsync(queueMessage.Alias);
        if (alias == null)
        {
            _logger.LogError(
                "Unknown alias in queue message: {Alias}. MessageId={MessageId}",
                queueMessage.Alias, queueMessage.MessageId);
            _events.DeliveryFailed(queueMessage, dequeueCount, "UnknownAlias",
                $"Alias '{queueMessage.Alias}' no longer exists; the message was dropped.");
            return null;
        }

        var aliasKey = ResolveAliasTarget(alias);
        _logger.LogInformation(
            "Alias resolved. Alias={Alias}, Type={Type}, PK={PK}, RK={RK}, MessageId={MessageId}",
            queueMessage.Alias, alias.TargetType, aliasKey.partitionKey, aliasKey.rowKey, queueMessage.MessageId);
        return aliasKey;
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

    private static (string partitionKey, string rowKey) ResolveAliasTarget(AliasEntity alias)
    {
        return alias.TargetType switch
        {
            "channel" => (alias.TeamId ?? throw new InvalidOperationException("TeamId required for channel alias"),
                          alias.ChannelId ?? throw new InvalidOperationException("ChannelId required for channel alias")),
            "personal" => ("user", alias.UserId ?? throw new InvalidOperationException("UserId required for personal alias")),
            "groupChat" => ("chat", alias.ChatId ?? throw new InvalidOperationException("ChatId required for groupChat alias")),
            _ => throw new InvalidOperationException($"Unknown alias target type: {alias.TargetType}")
        };
    }
}
