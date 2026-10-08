using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Middleware;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using static TeamsNotificationBot.Helpers.LogSanitizer;

namespace TeamsNotificationBot.Functions;

public class SendFunction
{
    private const string IdempotencyScope = "send";

    private readonly INotificationQueue _notificationQueue;
    private readonly IIdempotencyService _idempotencyService;
    private readonly IDeliveryRecords _deliveryRecords;
    private readonly IBotService _botService;
    private readonly ILogger<SendFunction> _logger;

    public SendFunction(
        INotificationQueue notificationQueue,
        IIdempotencyService idempotencyService,
        IDeliveryRecords deliveryRecords,
        IBotService botService,
        ILogger<SendFunction> logger)
    {
        _notificationQueue = notificationQueue;
        _idempotencyService = idempotencyService;
        _deliveryRecords = deliveryRecords;
        _botService = botService;
        _logger = logger;
    }

    [Function("Send")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/send")] HttpRequest req)
    {
        var correlationId = req.HttpContext.Items["CorrelationId"] as string;
        var messageId = $"send-{Guid.NewGuid():N}";
        var instance = req.Path.Value ?? "/api/v1/send";

        _logger.LogInformation(
            "Send request received. MessageId={MessageId}, CorrelationId={CorrelationId}",
            messageId, correlationId);

        var contentType = req.ContentType ?? "";
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return ApiResponse.Problem(415, "Unsupported Media Type",
                "Content-Type must be application/json.", instance, correlationId);
        }

        SendRequest? request;
        try
        {
            request = await req.ReadFromJsonAsync<SendRequest>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON payload. MessageId={MessageId}, CorrelationId={CorrelationId}",
                messageId, correlationId);
            return ApiResponse.Problem(400, "Bad Request",
                "Invalid JSON payload.", instance, correlationId);
        }

        if (request == null)
        {
            return ApiResponse.Problem(400, "Bad Request",
                "Request body is required.", instance, correlationId);
        }

        // Validate target
        var validationError = ValidateTarget(request.Target);
        if (validationError != null)
        {
            _logger.LogWarning("Invalid target: {Error}. MessageId={MessageId}, CorrelationId={CorrelationId}",
                validationError, messageId, correlationId);
            return ApiResponse.Problem(400, "Bad Request", validationError, instance, correlationId);
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return ApiResponse.Problem(400, "Bad Request",
                "Message is required.", instance, correlationId);
        }

        // Validate format
        if (!string.IsNullOrWhiteSpace(request.Format) &&
            request.Format != "text" && request.Format != "adaptive-card")
        {
            return ApiResponse.Problem(400, "Bad Request",
                "Invalid format. Expected 'text' or 'adaptive-card'.", instance, correlationId);
        }

        var metadataError = MetadataRules.Validate(request.Metadata);
        if (metadataError != null)
        {
            return ApiResponse.Problem(400, "Bad Request", metadataError, instance, correlationId);
        }

        if (request.Update != null && !MessageIds.IsValid(request.Update))
        {
            return ApiResponse.Problem(400, "Bad Request",
                "'update' must be a messageId returned by this API.", instance, correlationId);
        }

        // Validate adaptive card if applicable
        if (request.Format == "adaptive-card")
        {
            try
            {
                using var cardDoc = JsonDocument.Parse(request.Message);
                var (isValid, cardError) = AdaptiveCardValidator.Validate(cardDoc.RootElement);
                if (!isValid)
                {
                    return ApiResponse.Problem(400, "Bad Request",
                        cardError ?? "Invalid adaptive card.", instance, correlationId);
                }
            }
            catch (JsonException)
            {
                return ApiResponse.Problem(400, "Bad Request",
                    "Adaptive card payload must be valid JSON.", instance, correlationId);
            }
        }

        // Idempotency: the key is scoped to the caller and the target, and claimed before queuing,
        // so a concurrent duplicate can't queue a second copy.
        if (!IdempotencyKeys.TryRead(req, out var idempotencyKey, out var keyError))
        {
            return ApiResponse.Problem(400, "Bad Request", keyError!, instance, correlationId);
        }

        var principalId = AuthMiddleware.GetPrincipalId(req.HttpContext);
        IdempotencyClaim? claim = null;
        if (idempotencyKey != null)
        {
            claim = await _idempotencyService.ClaimAsync(IdempotencyScope,
                IdempotencyKeys.Scope(principalId, idempotencyKey, request.Target.Type, request.Target.TeamId,
                    request.Target.ChannelId, request.Target.UserId, request.Target.ChatId));
            if (claim.Status == IdempotencyClaimStatus.Completed)
            {
                // The caller's key isn't logged: the correlation ID ties the replay to this request.
                _logger.LogInformation("Idempotent replay. CorrelationId={CorrelationId}", correlationId);
                return IdempotencyKeys.Replay(claim.Result!);
            }
            if (claim.Status == IdempotencyClaimStatus.InProgress)
            {
                _logger.LogWarning(
                    "Idempotency key in use by a concurrent request. CorrelationId={CorrelationId}", correlationId);
                return IdempotencyKeys.InProgress(instance, correlationId);
            }
        }

        var queueMessage = new QueueMessage
        {
            MessageId = messageId,
            Target = request.Target,
            Message = request.Message,
            Format = request.Format,
            Metadata = request.Metadata,
            EnqueuedAt = DateTimeOffset.UtcNow,
            Source = "send",
            PrincipalId = principalId,
            Update = request.Update
        };

        IActionResult? rejection = null;
        try
        {
            if (request.Update != null)
                rejection = await CheckUpdateAsync(request, instance, correlationId);
            if (rejection == null)
                await _notificationQueue.EnqueueAsync(queueMessage);
        }
        catch when (claim != null)
        {
            // Nothing was queued: free the key so the caller can retry with it.
            await _idempotencyService.ReleaseAsync(claim);
            throw;
        }

        if (rejection != null)
        {
            if (claim != null)
                await _idempotencyService.ReleaseAsync(claim);
            return rejection;
        }

        _logger.LogInformation(
            "Send message queued. MessageId={MessageId}, TargetType={Type}, Format={Format}, CorrelationId={CorrelationId}",
            messageId, Sanitize(request.Target.Type), Sanitize(request.Format), correlationId);

        var responseBody = new
        {
            status = "queued",
            messageId,
            correlationId,
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        };

        if (claim != null)
        {
            await _idempotencyService.CompleteAsync(claim,
                StatusCodes.Status202Accepted, JsonSerializer.Serialize(responseBody));
        }

        return new ObjectResult(responseBody)
        { StatusCode = StatusCodes.Status202Accepted };
    }

    /// <summary>
    /// An update must replace a message this route sent to the same target, and the bot must still
    /// have the conversation it went to. Null when it can be queued, else the 404 or 409 to return.
    /// </summary>
    private async Task<IActionResult?> CheckUpdateAsync(SendRequest request, string instance, string? correlationId)
    {
        var referenced = await _deliveryRecords.GetAsync(request.Update!);
        if (referenced == null)
        {
            return ApiResponse.Problem(404, "Not Found",
                $"Unknown or expired message '{request.Update}'; there is nothing to update.", instance, correlationId);
        }
        if (!string.Equals(referenced.RequestedTarget, request.Target.Key(), StringComparison.Ordinal))
        {
            return ApiResponse.Problem(409, "Conflict",
                $"Message '{request.Update}' was sent to a different alias or target.", instance, correlationId);
        }
        if (referenced is { Status: DeliveryStatus.Delivered } && referenced.ConversationKey() is { } key &&
            !await _botService.HasConversationAsync(key.PartitionKey, key.RowKey))
        {
            _logger.LogWarning(
                "Message {ReferencedId} went to a conversation the bot no longer has. CorrelationId={CorrelationId}",
                Sanitize(request.Update), correlationId);
            return ApiResponse.Problem(404, "Not Found",
                $"The bot no longer has the conversation message '{request.Update}' went to " +
                "(the bot may have been removed from that team or chat).", instance, correlationId);
        }
        return null;
    }

    private static string? ValidateTarget(MessageTarget target)
    {
        if (string.IsNullOrEmpty(target.Type))
            return "target.type is required.";

        return target.Type switch
        {
            "channel" when string.IsNullOrEmpty(target.TeamId) => "target.teamId is required for channel type.",
            "channel" when string.IsNullOrEmpty(target.ChannelId) => "target.channelId is required for channel type.",
            "personal" when string.IsNullOrEmpty(target.UserId) => "target.userId is required for personal type.",
            "personal" when !PersonIds.IsValid(target.UserId) =>
                "target.userId must be an Entra object ID (a GUID) or a UPN.",
            "groupChat" when string.IsNullOrEmpty(target.ChatId) => "target.chatId is required for groupChat type.",
            "channel" or "personal" or "groupChat" => null,
            _ => $"Unknown target type: '{target.Type}'. Expected 'channel', 'personal', or 'groupChat'."
        };
    }
}
