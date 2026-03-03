using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Services;

namespace TeamsNotificationBot.Functions;

public class PoisonQueueMonitorFunction
{
    private readonly IBotService _botService;
    private readonly IAliasService _aliasService;
    private readonly ILogger<PoisonQueueMonitorFunction> _logger;

    public PoisonQueueMonitorFunction(
        IBotService botService,
        IAliasService aliasService,
        ILogger<PoisonQueueMonitorFunction> logger)
    {
        _botService = botService;
        _aliasService = aliasService;
        _logger = logger;
    }

    [Function("NotificationsPoisonMonitor")]
    public async Task RunNotifications(
        [QueueTrigger("notifications-poison")] string messageJson,
        FunctionContext context)
    {
        await ProcessPoisonMessageAsync("notifications-poison", messageJson);
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
                var doc = JsonDocument.Parse(messageJson);
                if (doc.RootElement.TryGetProperty("enqueuedAt", out var ts))
                    enqueuedTime = ts.GetDateTimeOffset();
            }
            catch { /* best-effort parse */ }

            var cardJson = PoisonAlertCardBuilder.Build(queueName, messageJson, enqueuedTime);
            var card = JsonDocument.Parse(cardJson).RootElement;

            var (pk, rk) = ResolveAliasTarget(alias);
            await _botService.SendAdaptiveCardAsync(pk, rk, card);

            _logger.LogInformation("Poison alert sent to alias '{Alias}' for queue {Queue}", aliasName, queueName);
        }
        catch (Exception ex)
        {
            // Log but DO NOT rethrow — prevent *-poison-poison queues
            _logger.LogError(ex, "Failed to process poison message alert for {Queue}. Message swallowed to prevent cascading failure.", queueName);
        }
    }

    private static (string pk, string rk) ResolveAliasTarget(Models.AliasEntity alias)
    {
        return alias.TargetType switch
        {
            "channel" => (alias.TeamId!, alias.ChannelId!),
            "personal" => ("user", alias.UserId!),
            "groupChat" => ("chat", alias.ChatId!),
            _ => throw new InvalidOperationException($"Unknown alias target type: {alias.TargetType}")
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
