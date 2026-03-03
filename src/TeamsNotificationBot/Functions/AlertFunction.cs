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

public class AlertFunction
{
    private readonly IAliasService _aliasService;
    private readonly QueueClient _queueClient;
    private readonly ILogger<AlertFunction> _logger;

    public AlertFunction(
        IAliasService aliasService,
        QueueClient queueClient,
        ILogger<AlertFunction> logger)
    {
        _aliasService = aliasService;
        _queueClient = queueClient;
        _logger = logger;
    }

    [Function("Alert")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/alert/{alias}")] HttpRequest req,
        string alias)
    {
        var correlationId = req.HttpContext.Items["CorrelationId"] as string;
        var messageId = $"alert-{Guid.NewGuid():N}";
        var instance = req.Path.Value ?? $"/api/v1/alert/{alias}";

        _logger.LogInformation(
            "Alert request received. Alias={Alias}, MessageId={MessageId}, CorrelationId={CorrelationId}",
            alias, messageId, correlationId);

        // Validate Content-Type
        var contentType = req.ContentType ?? "";
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Invalid Content-Type for alert: {ContentType}. Alias={Alias}, CorrelationId={CorrelationId}",
                contentType, alias, correlationId);
            return ApiResponse.Problem(415, "Unsupported Media Type",
                "Content-Type must be application/json.", instance, correlationId);
        }

        // Validate alias exists
        var channelAlias = await _aliasService.GetAliasAsync(alias);
        if (channelAlias == null)
        {
            _logger.LogWarning(
                "Unknown alias for alert: {Alias}. CorrelationId={CorrelationId}",
                alias, correlationId);
            return ApiResponse.Problem(404, "Not Found",
                $"Unknown alias '{alias}'.", instance, correlationId);
        }

        // Parse Common Alert Schema payload
        CommonAlertPayload? alertPayload;
        try
        {
            alertPayload = await req.ReadFromJsonAsync<CommonAlertPayload>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Invalid JSON in alert payload. Alias={Alias}, CorrelationId={CorrelationId}",
                alias, correlationId);
            return ApiResponse.Problem(400, "Bad Request",
                "Invalid JSON payload.", instance, correlationId);
        }

        if (alertPayload == null)
        {
            return ApiResponse.Problem(400, "Bad Request",
                "Request body is required.", instance, correlationId);
        }

        // Validate Common Alert Schema
        if (!string.Equals(alertPayload.SchemaId, "azureMonitorCommonAlertSchema", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Invalid schema ID: {SchemaId}. Expected azureMonitorCommonAlertSchema. Alias={Alias}, CorrelationId={CorrelationId}",
                alertPayload.SchemaId, alias, correlationId);
            return ApiResponse.Problem(400, "Bad Request",
                $"Unsupported schema '{alertPayload.SchemaId}'. Expected 'azureMonitorCommonAlertSchema'.",
                instance, correlationId);
        }

        // Build Adaptive Card from alert
        var cardJson = AlertCardBuilder.Build(alertPayload);

        // Enqueue as notification
        var queueMessage = new QueueMessage
        {
            MessageId = messageId,
            Alias = alias,
            Message = cardJson,
            Format = "adaptive-card",
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        var messageJson = JsonSerializer.Serialize(queueMessage);
        await _queueClient.SendMessageAsync(messageJson);

        _logger.LogInformation(
            "Alert queued. Alias={Alias}, MessageId={MessageId}, AlertRule={AlertRule}, Severity={Severity}, CorrelationId={CorrelationId}",
            alias, messageId,
            alertPayload.Data?.Essentials?.AlertRule ?? "unknown",
            alertPayload.Data?.Essentials?.Severity ?? "unknown",
            correlationId);

        return new ObjectResult(new
        {
            status = "queued",
            messageId,
            correlationId,
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        })
        { StatusCode = StatusCodes.Status202Accepted };
    }
}
