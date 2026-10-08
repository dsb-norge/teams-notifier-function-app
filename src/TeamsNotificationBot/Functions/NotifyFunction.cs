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

public class NotifyFunction
{
    private const string IdempotencyScope = "notify";

    private readonly IAliasService _aliasService;
    private readonly IBotService _botService;
    private readonly INotificationQueue _notificationQueue;
    private readonly IIdempotencyService _idempotencyService;
    private readonly IDeliveryRecords _deliveryRecords;
    private readonly ILogger<NotifyFunction> _logger;

    public NotifyFunction(
        IAliasService aliasService,
        IBotService botService,
        INotificationQueue notificationQueue,
        IIdempotencyService idempotencyService,
        IDeliveryRecords deliveryRecords,
        ILogger<NotifyFunction> logger)
    {
        _aliasService = aliasService;
        _botService = botService;
        _notificationQueue = notificationQueue;
        _idempotencyService = idempotencyService;
        _deliveryRecords = deliveryRecords;
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
            Sanitize(alias), Sanitize(sourceIp), messageId, correlationId);

        // Validate Content-Type
        var contentType = req.ContentType ?? "";
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Invalid Content-Type: {ContentType}. Alias={Alias}, MessageId={MessageId}, CorrelationId={CorrelationId}",
                Sanitize(contentType), Sanitize(alias), messageId, correlationId);
            return ApiResponse.Problem(415, "Unsupported Media Type",
                "Content-Type must be application/json.", instance, correlationId);
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
                Sanitize(alias), messageId, correlationId);
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
                Sanitize(validationError), Sanitize(alias), messageId, correlationId);
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
                    Sanitize(cardError), Sanitize(alias), messageId, correlationId);
                return ApiResponse.Problem(400, "Bad Request",
                    cardError ?? "Invalid adaptive card.", instance, correlationId);
            }
        }

        // Idempotency: the key is scoped to the caller and the alias, and claimed before queuing,
        // so a concurrent duplicate can't queue a second copy. Claimed before the checks that depend
        // on current state (alias, conversation, the message replied to or updated), so a retry of a
        // request that completed replays its response even if any of those has changed since.
        if (!IdempotencyKeys.TryRead(req, out var idempotencyKey, out var keyError))
        {
            return ApiResponse.Problem(400, "Bad Request", keyError!, instance, correlationId);
        }

        var principalId = AuthMiddleware.GetPrincipalId(req.HttpContext);
        IdempotencyClaim? claim = null;
        if (idempotencyKey != null)
        {
            claim = await _idempotencyService.ClaimAsync(IdempotencyScope,
                IdempotencyKeys.Scope(principalId, idempotencyKey, alias.ToLowerInvariant()));
            if (claim.Status == IdempotencyClaimStatus.Completed)
            {
                // The caller's key isn't logged: the correlation ID ties the replay to this request.
                _logger.LogInformation(
                    "Idempotent replay. Alias={Alias}, CorrelationId={CorrelationId}",
                    Sanitize(alias), correlationId);
                return IdempotencyKeys.Replay(claim.Result!);
            }
            if (claim.Status == IdempotencyClaimStatus.InProgress)
            {
                _logger.LogWarning(
                    "Idempotency key in use by a concurrent request. Alias={Alias}, CorrelationId={CorrelationId}",
                    Sanitize(alias), correlationId);
                return IdempotencyKeys.InProgress(instance, correlationId);
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
            EnqueuedAt = DateTimeOffset.UtcNow,
            Source = "notify",
            PrincipalId = principalId,
            ReplyTo = request.ReplyTo,
            Update = request.Update,
            Mentions = request.Mentions is { Count: > 0 } ? request.Mentions : null
        };

        IActionResult? rejection;
        try
        {
            rejection = await CheckDeliverableAsync(alias, request, instance, correlationId, messageId, sourceIp);
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

        var duration = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        _logger.LogInformation(
            "Message queued. Alias={Alias}, MessageId={MessageId}, Format={Format}, Duration={Duration}ms, SourceIp={SourceIp}, CorrelationId={CorrelationId}",
            Sanitize(alias), messageId, Sanitize(request.Format), duration, Sanitize(sourceIp), correlationId);

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
    /// The checks that depend on current state: the alias exists, a reply or update refers to a
    /// message sent to this alias, its mentions suit the conversation it will go to, and the bot
    /// still has that conversation. Null when the message can be queued, else the 400, 404 or 409
    /// to return.
    /// </summary>
    private async Task<IActionResult?> CheckDeliverableAsync(
        string alias, NotificationRequest request, string instance, string? correlationId, string messageId, string sourceIp)
    {
        var channelAlias = await _aliasService.GetAliasAsync(alias);
        if (channelAlias == null)
        {
            _logger.LogWarning(
                "Unknown alias: {Alias}. MessageId={MessageId}, SourceIp={SourceIp}, CorrelationId={CorrelationId}",
                Sanitize(alias), messageId, Sanitize(sourceIp), correlationId);
            return ApiResponse.Problem(404, "Not Found",
                $"Unknown alias '{alias}'.", instance, correlationId);
        }

        // An unknown or expired parent is fine for a reply (it becomes a new post) but not for an
        // update, which has nothing to replace.
        DeliveryRecordEntity? referenced = null;
        var referencedId = request.Update ?? request.ReplyTo;
        if (referencedId != null)
        {
            referenced = await _deliveryRecords.GetAsync(referencedId);
            if (referenced == null && request.Update != null)
            {
                return ApiResponse.Problem(404, "Not Found",
                    $"Unknown or expired message '{request.Update}'; there is nothing to update.",
                    instance, correlationId);
            }
            if (referenced != null && !string.Equals(referenced.Alias, alias.ToLowerInvariant(), StringComparison.Ordinal))
            {
                return ApiResponse.Problem(409, "Conflict",
                    $"Message '{referencedId}' was sent to a different alias or target.", instance, correlationId);
            }
        }

        // Check the conversation the message will actually go to: a reply or an update to a
        // delivered message goes where that message went, even if the alias has been repointed since.
        var goesToParent = referenced is { Status: DeliveryStatus.Delivered } && referenced.ConversationKey() != null;

        var mentionError = MentionRules.CheckTarget(request.Mentions,
            goesToParent ? referenced!.TargetType : channelAlias.TargetType);
        if (mentionError != null)
        {
            _logger.LogWarning(
                "Mentions don't suit the target: {Error}. Alias={Alias}, CorrelationId={CorrelationId}",
                mentionError, Sanitize(alias), correlationId);
            return ApiResponse.Problem(400, "Bad Request", mentionError, instance, correlationId);
        }

        if (goesToParent && referenced!.ConversationKey() is { } parentKey)
        {
            if (!await _botService.HasConversationAsync(parentKey.PartitionKey, parentKey.RowKey))
            {
                _logger.LogWarning(
                    "Message {ReferencedId} went to a conversation the bot no longer has. CorrelationId={CorrelationId}",
                    Sanitize(referencedId), correlationId);
                return ApiResponse.Problem(404, "Not Found",
                    $"The bot no longer has the conversation message '{referencedId}' went to " +
                    "(the bot may have been removed from that team or chat).",
                    instance, correlationId);
            }
        }
        else if (!await _botService.HasConversationAsync(channelAlias))
        {
            _logger.LogWarning(
                "Alias {Alias} points to a conversation the bot no longer has. CorrelationId={CorrelationId}",
                Sanitize(alias), correlationId);
            return ApiResponse.ConversationGone(alias, instance, correlationId);
        }

        return null;
    }
}
