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

public class NotifyFunction
{
    private readonly IAliasService _aliasService;
    private readonly QueueClient _queueClient;
    private readonly IIdempotencyService _idempotencyService;
    private readonly ILogger<NotifyFunction> _logger;

    public NotifyFunction(
        IAliasService aliasService,
        QueueClient queueClient,
        IIdempotencyService idempotencyService,
        ILogger<NotifyFunction> logger)
    {
        _aliasService = aliasService;
        _queueClient = queueClient;
        _idempotencyService = idempotencyService;
        _logger = logger;
    }

    [Function("Notify")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/notify/{alias}")] HttpRequest req,
        string alias)
    {
        var startTime = DateTimeOffset.UtcNow;
        var correlationId = req.HttpContext.Items["CorrelationId"] as string;
        var sourceIp = req.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                       ?? req.HttpContext.Connection.RemoteIpAddress?.ToString()
                       ?? "unknown";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var instance = req.Path.Value ?? $"/api/v1/notify/{alias}";

        _logger.LogInformation(
            "Notify request received. Alias={Alias}, SourceIp={SourceIp}, MessageId={MessageId}, CorrelationId={CorrelationId}",
            alias, sourceIp, messageId, correlationId);

        // Validate Content-Type
        var contentType = req.ContentType ?? "";
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Invalid Content-Type: {ContentType}. Alias={Alias}, MessageId={MessageId}, CorrelationId={CorrelationId}",
                contentType, alias, messageId, correlationId);
            return ApiResponse.Problem(415, "Unsupported Media Type",
                "Content-Type must be application/json.", instance, correlationId);
        }

        // Validate alias exists
        var channelAlias = await _aliasService.GetAliasAsync(alias);
        if (channelAlias == null)
        {
            _logger.LogWarning(
                "Unknown alias: {Alias}. MessageId={MessageId}, SourceIp={SourceIp}, CorrelationId={CorrelationId}",
                alias, messageId, sourceIp, correlationId);
            return ApiResponse.Problem(404, "Not Found",
                $"Unknown alias '{alias}'.", instance, correlationId);
        }

        // Parse request body
        NotificationRequest? request;
        try
        {
            request = await req.ReadFromJsonAsync<NotificationRequest>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Invalid JSON payload. Alias={Alias}, MessageId={MessageId}, CorrelationId={CorrelationId}",
                alias, messageId, correlationId);
            return ApiResponse.Problem(400, "Bad Request",
                "Invalid JSON payload.", instance, correlationId);
        }

        if (request == null)
        {
            return ApiResponse.Problem(400, "Bad Request",
                "Request body is required.", instance, correlationId);
        }

        // Validate request
        if (!request.IsValid(out var validationError))
        {
            _logger.LogWarning(
                "Invalid request: {Error}. Alias={Alias}, MessageId={MessageId}, CorrelationId={CorrelationId}",
                validationError, alias, messageId, correlationId);
            return ApiResponse.Problem(400, "Bad Request",
                validationError ?? "Invalid request.", instance, correlationId);
        }

        // Validate adaptive card if applicable
        if (request.Format == "adaptive-card")
        {
            var (isValid, cardError) = AdaptiveCardValidator.Validate(request.Message);
            if (!isValid)
            {
                _logger.LogWarning(
                    "Adaptive card validation failed: {Error}. Alias={Alias}, MessageId={MessageId}, CorrelationId={CorrelationId}",
                    cardError, alias, messageId, correlationId);
                return ApiResponse.Problem(400, "Bad Request",
                    cardError ?? "Invalid adaptive card.", instance, correlationId);
            }
        }

        // Check idempotency
        var idempotencyKey = req.Headers["Idempotency-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var cached = await _idempotencyService.GetAsync("notify", idempotencyKey);
            if (cached != null)
            {
                _logger.LogInformation(
                    "Idempotent request. Key={Key}, Alias={Alias}, CorrelationId={CorrelationId}",
                    idempotencyKey, alias, correlationId);
                return new ObjectResult(JsonSerializer.Deserialize<object>(cached.ResponseBody))
                { StatusCode = cached.StatusCode };
            }
        }

        // Build queue message
        var queueMessage = new QueueMessage
        {
            MessageId = messageId,
            Alias = alias,
            Message = request.Format == "text"
                ? request.Message.GetString() ?? ""
                : request.Message.GetRawText(),
            Format = request.Format,
            Metadata = request.Metadata,
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        // Enqueue message
        var messageJson = JsonSerializer.Serialize(queueMessage);
        await _queueClient.SendMessageAsync(messageJson);

        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        _logger.LogInformation(
            "Message queued. Alias={Alias}, MessageId={MessageId}, Format={Format}, Duration={Duration}ms, SourceIp={SourceIp}, CorrelationId={CorrelationId}",
            alias, messageId, request.Format, duration, sourceIp, correlationId);

        var responseBody = new
        {
            status = "queued",
            messageId,
            correlationId,
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        };

        // Store idempotency record
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _idempotencyService.SetAsync("notify", idempotencyKey,
                StatusCodes.Status202Accepted, JsonSerializer.Serialize(responseBody));
        }

        return new ObjectResult(responseBody)
        { StatusCode = StatusCodes.Status202Accepted };
    }
}
