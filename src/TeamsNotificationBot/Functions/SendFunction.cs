using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Functions;

public class SendFunction
{
    private readonly QueueClient _queueClient;
    private readonly ILogger<SendFunction> _logger;

    public SendFunction(QueueClient queueClient, ILogger<SendFunction> logger)
    {
        _queueClient = queueClient;
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

        // Validate adaptive card if applicable
        if (request.Format == "adaptive-card")
        {
            try
            {
                var (isValid, cardError) = AdaptiveCardValidator.Validate(
                    JsonDocument.Parse(request.Message).RootElement);
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

        var queueMessage = new QueueMessage
        {
            MessageId = messageId,
            Target = request.Target,
            Message = request.Message,
            Format = request.Format,
            Metadata = request.Metadata,
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        var messageJson = JsonSerializer.Serialize(queueMessage);
        await _queueClient.SendMessageAsync(messageJson);

        _logger.LogInformation(
            "Send message queued. MessageId={MessageId}, TargetType={Type}, Format={Format}, CorrelationId={CorrelationId}",
            messageId, request.Target.Type, request.Format, correlationId);

        return new ObjectResult(new
        {
            status = "queued",
            messageId,
            correlationId,
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        })
        { StatusCode = StatusCodes.Status202Accepted };
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
            "groupChat" when string.IsNullOrEmpty(target.ChatId) => "target.chatId is required for groupChat type.",
            "channel" or "personal" or "groupChat" => null,
            _ => $"Unknown target type: '{target.Type}'. Expected 'channel', 'personal', or 'groupChat'."
        };
    }
}
