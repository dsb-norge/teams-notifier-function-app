using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;

namespace TeamsNotificationBot.Functions;

public class BotOperationsFunction
{
    private readonly IBotService _botService;
    private readonly ILogger<BotOperationsFunction> _logger;

    public BotOperationsFunction(
        IBotService botService,
        ILogger<BotOperationsFunction> logger)
    {
        _botService = botService;
        _logger = logger;
    }

    [Function("BotOperations")]
    public async Task Run(
        [QueueTrigger("botoperations")] string messageJson,
        FunctionContext context)
    {
        BotOperationMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<BotOperationMessage>(messageJson);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize bot operation message");
            return;
        }

        if (message == null)
        {
            _logger.LogError("Bot operation message deserialized to null");
            return;
        }

        _logger.LogInformation("Processing bot operation: {Operation} for team {TeamGuid}",
            message.Operation, message.TeamGuid);

        switch (message.Operation)
        {
            case "enumerate_channels":
                if (string.IsNullOrEmpty(message.SerializedReference))
                {
                    _logger.LogError("enumerate_channels operation missing SerializedReference");
                    return;
                }
                await _botService.EnumerateAndStoreTeamChannelsAsync(
                    message.SerializedReference, message.TeamGuid, message.TeamName, message.TeamThreadId);
                break;

            case "remove_team_refs":
                await _botService.BatchRemoveTeamReferencesAsync(message.TeamGuid);
                break;

            case "rename_team":
                await _botService.BatchUpdateTeamNameAsync(message.TeamGuid, message.TeamName);
                break;

            default:
                _logger.LogError("Unknown bot operation: {Operation}", message.Operation);
                break;
        }
    }
}
