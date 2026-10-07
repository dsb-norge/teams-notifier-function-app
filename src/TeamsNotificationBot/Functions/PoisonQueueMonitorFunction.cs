using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Services;

namespace TeamsNotificationBot.Functions;

public class PoisonQueueMonitorFunction
{
    private readonly IBotService _botService;
    private readonly IAliasService _aliasService;
    private readonly IDeliveryRecords _records;
    private readonly ILogger<PoisonQueueMonitorFunction> _logger;

    public PoisonQueueMonitorFunction(
        IBotService botService,
        IAliasService aliasService,
        IDeliveryRecords records,
        ILogger<PoisonQueueMonitorFunction> logger)
    {
        _botService = botService;
        _aliasService = aliasService;
        _records = records;
        _logger = logger;
    }

    [Function("NotificationsPoisonMonitor")]
    public async Task RunNotifications(
        [QueueTrigger("notifications-poison")] string messageJson,
        FunctionContext context)
    {
        await MarkFailedAsync(messageJson);
        await ProcessPoisonMessageAsync("notifications-poison", messageJson);
    }

    /// <summary>
    /// Records a notification that used up its delivery attempts as failed, so its status says so.
    /// Best-effort, and never throws: an exception here would create a -poison-poison queue.
    /// </summary>
    private async Task MarkFailedAsync(string messageJson)
    {
        try
        {
            var message = JsonSerializer.Deserialize<Models.QueueMessage>(messageJson);
            if (message is { MessageId.Length: > 0 })
            {
                await _records.MarkFailedAsync(message,
                    "Delivery failed on every attempt; the message was moved to the poison queue.");
            }
        }
        catch (Exception ex)
        {
            // Side concern (docs/contributing.md §5): the poison alert below must still go out.
            _logger.LogWarning(ex, "Could not record a poison message as failed");
        }
    }

    [Function("BotOperationsPoisonMonitor")]
    public async Task RunBotOperations(
        [QueueTrigger("botoperations-poison")] string messageJson,
        FunctionContext context)
    {
        await ProcessPoisonMessageAsync("botoperations-poison", messageJson);
    }

    private async Task ProcessPoisonMessageAsync(string queueName, string messageJson)
    {
        // CRITICAL: Wrap ALL logic in try/catch. An unhandled exception here creates
        // *-poison-poison queues, which is a cascading failure.
        try
        {
            _logger.LogWarning("Poison message detected in {Queue}: {Excerpt}",
                queueName, Truncate(messageJson, 200));

            var aliasName = Environment.GetEnvironmentVariable("PoisonAlertAlias");
            if (string.IsNullOrEmpty(aliasName))
            {
                _logger.LogWarning("PoisonAlertAlias not configured — skipping alert notification");
                return;
            }

            var alias = await _aliasService.GetAliasAsync(aliasName);
            if (alias == null)
            {
                _logger.LogWarning("Poison alert alias '{Alias}' not found — skipping alert notification", aliasName);
                return;
            }

            // Best-effort: extract enqueued time from message
            DateTimeOffset? enqueuedTime = null;
            try
            {
                using var doc = JsonDocument.Parse(messageJson);
                if (doc.RootElement.TryGetProperty("enqueuedAt", out var ts))
                    enqueuedTime = ts.GetDateTimeOffset();
            }
            catch { /* best-effort parse */ }

            var cardJson = PoisonAlertCardBuilder.Build(queueName, messageJson, enqueuedTime);
            using var cardDoc = JsonDocument.Parse(cardJson);
            var card = cardDoc.RootElement.Clone();

            var (pk, rk) = alias.ConversationKey() ?? throw new InvalidOperationException(
                $"Alias '{alias.RowKey}' has no valid target (type '{alias.TargetType}').");
            await _botService.SendAdaptiveCardAsync(pk, rk, card);

            _logger.LogInformation("Poison alert sent to alias '{Alias}' for queue {Queue}", aliasName, queueName);
        }
        catch (Exception ex)
        {
            // Log but DO NOT rethrow — prevent *-poison-poison queues
            _logger.LogError(ex, "Failed to process poison message alert for {Queue}. Message swallowed to prevent cascading failure.", queueName);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
