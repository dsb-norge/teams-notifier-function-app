using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Services;
using static TeamsNotificationBot.Helpers.LogSanitizer;

namespace TeamsNotificationBot.Functions;

public class GetAliasesFunction
{
    private readonly IAliasService _aliasService;
    private readonly ILogger<GetAliasesFunction> _logger;

    public GetAliasesFunction(IAliasService aliasService, ILogger<GetAliasesFunction> logger)
    {
        _aliasService = aliasService;
        _logger = logger;
    }

    [Function("GetAliases")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/aliases")] HttpRequest req)
    {
        var correlationId = req.HttpContext.Items["CorrelationId"] as string;
        var sourceIp = req.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                       ?? req.HttpContext.Connection.RemoteIpAddress?.ToString()
                       ?? "unknown";
        var instance = req.Path.Value ?? "/api/v1/aliases";

        var debugMode = string.Equals(
            Environment.GetEnvironmentVariable("DEBUG_MODE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (!debugMode)
        {
            _logger.LogWarning(
                "Aliases endpoint accessed with debug mode disabled. SourceIp={SourceIp}, ResponseCode=403, CorrelationId={CorrelationId}",
                Sanitize(sourceIp), correlationId);

            return ApiResponse.Problem(403, "Forbidden",
                "Debug mode is disabled.", instance, correlationId);
        }

        var aliases = await _aliasService.GetAllAliasesAsync();
        var aliasList = aliases.Select(a => new
        {
            alias = a.RowKey,
            targetType = a.TargetType,
            teamId = a.TeamId,
            channelId = a.ChannelId,
            userId = a.UserId,
            chatId = a.ChatId,
            description = a.Description,
            createdBy = a.CreatedByName,
            createdAt = a.CreatedAt.ToString("o")
        });

        _logger.LogInformation(
            "Aliases endpoint accessed. SourceIp={SourceIp}, AliasCount={AliasCount}, ResponseCode=200, CorrelationId={CorrelationId}",
            Sanitize(sourceIp), aliases.Count, correlationId);

        return new OkObjectResult(new { aliases = aliasList });
    }
}
