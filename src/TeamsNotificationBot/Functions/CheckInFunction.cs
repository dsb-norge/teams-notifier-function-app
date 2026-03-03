using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;

namespace TeamsNotificationBot.Functions;

public class CheckInFunction
{
    private readonly IAliasService _aliasService;
    private readonly QueueClient _queueClient;
    private readonly ILogger<CheckInFunction> _logger;

    public CheckInFunction(
        IAliasService aliasService,
        QueueClient queueClient,
        ILogger<CheckInFunction> logger)
    {
        _aliasService = aliasService;
        _queueClient = queueClient;
        _logger = logger;
    }

    [Function("CheckIn")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/checkin/{alias}")] HttpRequest req,
        string alias)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var correlationId = req.HttpContext.Items["CorrelationId"] as string;
        var sourceIp = req.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                       ?? req.HttpContext.Connection.RemoteIpAddress?.ToString()
                       ?? "unknown";
        var messageId = $"checkin-{Guid.NewGuid():N}";
        var instance = req.Path.Value ?? $"/api/v1/checkin/{alias}";

        _logger.LogInformation(
            "CheckIn request received. Alias={Alias}, SourceIp={SourceIp}, MessageId={MessageId}, CorrelationId={CorrelationId}",
            alias, sourceIp, messageId, correlationId);

        // Parse optional body for source info
        string source = "unknown";
        try
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            if (!string.IsNullOrEmpty(body))
            {
                var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("source", out var sourceElement))
                    source = sourceElement.GetString() ?? "unknown";
            }
        }
        catch
        {
            // Body is optional, ignore parse errors
        }

        // Validate alias exists
        var channelAlias = await _aliasService.GetAliasAsync(alias);
        if (channelAlias == null)
        {
            _logger.LogWarning(
                "Unknown alias for checkin: {Alias}. MessageId={MessageId}, CorrelationId={CorrelationId}",
                alias, messageId, correlationId);
            return ApiResponse.Problem(404, "Not Found",
                $"Unknown alias '{alias}'.", instance, correlationId);
        }

        // Build check-in text message
        var checkinText = $"\ud83d\udd14 Check-in | Version: {AppInfo.Version} | Time: {timestamp:u} | Source: {source}";

        // Enqueue as text message
        var queueMessage = new QueueMessage
        {
            MessageId = messageId,
            Alias = alias,
            Message = checkinText,
            Format = "text",
            EnqueuedAt = timestamp
        };

        var messageJson = JsonSerializer.Serialize(queueMessage);
        await _queueClient.SendMessageAsync(messageJson);

        _logger.LogInformation(
            "CheckIn message queued. Alias={Alias}, MessageId={MessageId}, Version={Version}, Source={Source}, CorrelationId={CorrelationId}",
            alias, messageId, AppInfo.Version, source, correlationId);

        return new ObjectResult(new
        {
            status = "queued",
            messageId,
            correlationId,
            timestamp = timestamp.ToString("o"),
            version = AppInfo.Version
        })
        { StatusCode = StatusCodes.Status202Accepted };
    }
}
