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
    private readonly ILogger<QueueProcessorFunction> _logger;

    public QueueProcessorFunction(
        IBotService botService,
        IAliasService aliasService,
        ILogger<QueueProcessorFunction> logger)
    {
        _botService = botService;
        _aliasService = aliasService;
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

        _logger.LogInformation(
            "Processing queue message. MessageId={MessageId}, Alias={Alias}, Format={Format}",
            queueMessage.MessageId, queueMessage.Alias, queueMessage.Format);

        // Resolve target: either from direct Target or via Alias lookup
        string partitionKey;
        string rowKey;

        if (queueMessage.Target != null)
        {
            // Direct targeting via /v1/send
            (partitionKey, rowKey) = ResolveTarget(queueMessage.Target);
            _logger.LogInformation(
                "Direct target resolved. Type={Type}, PK={PK}, RK={RK}, MessageId={MessageId}",
                queueMessage.Target.Type, partitionKey, rowKey, queueMessage.MessageId);
        }
        else if (!string.IsNullOrEmpty(queueMessage.Alias))
        {
            // Alias-based targeting
            var alias = await _aliasService.GetAliasAsync(queueMessage.Alias);
            if (alias == null)
            {
                _logger.LogError(
                    "Unknown alias in queue message: {Alias}. MessageId={MessageId}",
                    queueMessage.Alias, queueMessage.MessageId);
                return;
            }
            (partitionKey, rowKey) = ResolveAliasTarget(alias);
            _logger.LogInformation(
                "Alias resolved. Alias={Alias}, Type={Type}, PK={PK}, RK={RK}, MessageId={MessageId}",
                queueMessage.Alias, alias.TargetType, partitionKey, rowKey, queueMessage.MessageId);
        }
        else
        {
            _logger.LogError("Queue message has neither Target nor Alias. MessageId={MessageId}",
                queueMessage.MessageId);
            return;
        }

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
                var card = JsonDocument.Parse(queueMessage.Message).RootElement;
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
            throw;
        }
    }

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
            "channel" => (alias.TeamId!, alias.ChannelId!),
            "personal" => ("user", alias.UserId!),
            "groupChat" => ("chat", alias.ChatId!),
            _ => throw new InvalidOperationException($"Unknown alias target type: {alias.TargetType}")
        };
    }
}
