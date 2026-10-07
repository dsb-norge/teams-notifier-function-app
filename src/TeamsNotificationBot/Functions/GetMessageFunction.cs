using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using static TeamsNotificationBot.Helpers.LogSanitizer;

namespace TeamsNotificationBot.Functions;

/// <summary><c>GET /v1/messages/{messageId}</c>: what happened to a queued message, and where it went.</summary>
public class GetMessageFunction
{
    private readonly IDeliveryRecords _deliveryRecords;
    private readonly ILogger<GetMessageFunction> _logger;

    public GetMessageFunction(IDeliveryRecords deliveryRecords, ILogger<GetMessageFunction> logger)
    {
        _deliveryRecords = deliveryRecords;
        _logger = logger;
    }

    [Function("GetMessage")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/messages/{messageId}")] HttpRequest req,
        string messageId)
    {
        var correlationId = req.HttpContext.Items["CorrelationId"] as string;
        var instance = req.Path.Value ?? $"/api/v1/messages/{messageId}";

        // Checked before the ID is used as a Table Storage key.
        var record = MessageIds.IsValid(messageId) ? await _deliveryRecords.GetAsync(messageId) : null;
        if (record == null)
        {
            _logger.LogInformation("Unknown or expired message {MessageId}. CorrelationId={CorrelationId}",
                Sanitize(messageId), correlationId);
            return ApiResponse.Problem(404, "Not Found",
                $"Unknown or expired message '{messageId}'.", instance, correlationId);
        }

        return new OkObjectResult(MessageStatusResponse.From(record));
    }
}
