using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Extensions.Logging;
using static TeamsNotificationBot.Helpers.LogSanitizer;

namespace TeamsNotificationBot.Functions;

public class BotMessagesFunction
{
    private readonly IAgentHttpAdapter _adapter;
    private readonly IAgent _agent;
    private readonly ILogger<BotMessagesFunction> _logger;

    public BotMessagesFunction(
        IAgentHttpAdapter adapter,
        IAgent agent,
        ILogger<BotMessagesFunction> logger)
    {
        _adapter = adapter;
        _agent = agent;
        _logger = logger;
    }

    [Function("BotMessages")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "messages")] HttpRequest req)
    {
        _logger.LogInformation("BotMessages: incoming request from {ClientIp}",
            Sanitize(req.Headers["X-Forwarded-For"].FirstOrDefault()
                ?? req.HttpContext.Connection.RemoteIpAddress?.ToString()
                ?? "unknown"));

        try
        {
            await _adapter.ProcessAsync(req, req.HttpContext.Response, _agent);
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BotMessages processing failed: {Error}", ex.Message);
            return new ObjectResult(new { error = ex.GetType().Name, message = ex.Message })
            {
                StatusCode = 500
            };
        }
    }
}
