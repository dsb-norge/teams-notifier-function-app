using System.Text;
using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using static TeamsNotificationBot.Helpers.LogSanitizer;

namespace TeamsNotificationBot.Functions;

/// <summary>
/// Anonymous ingress for updown.io webhooks: <c>POST /api/v1/ingest/updown/{token}</c>.
///
/// This is a distinct, opt-in trust zone (see docs/feat-updown-io-webhook/design.md). It does its
/// own auth via the high-entropy path token (SHA-256 looked up in the webhooktokens table), never
/// touches the AAD-gated routes' code, and returns 200 quickly. Malformed/unknown payloads are
/// logged and answered 200 to avoid updown's 25× retry storm; only a transient enqueue failure
/// returns 5xx so a genuinely retryable delivery is retried.
/// </summary>
public class UpdownIngestFunction
{
    private const string DedupeScope = "updown-ingest";

    // 28 KB matches the app-wide body limit (AuthMiddleware). It also keeps the derived, Base64-
    // encoded queue message safely under Azure Storage Queue's 64 KB (post-encoding) message limit,
    // so an accepted body can't produce a QueueMessage that always fails SendMessageAsync → 500 →
    // endless updown retries. Overridable via UpdownWebhook__MaxBodyBytes for intentional tuning.
    private const int DefaultMaxBodyBytes = 28 * 1024;

    private readonly IWebhookService _webhookService;
    private readonly QueueClient _queueClient;
    private readonly IIdempotencyService _idempotency;
    private readonly IUpdownIpAllowlistService _ipAllowlist;
    private readonly ILogger<UpdownIngestFunction> _logger;

    public UpdownIngestFunction(
        IWebhookService webhookService,
        QueueClient queueClient,
        IIdempotencyService idempotency,
        IUpdownIpAllowlistService ipAllowlist,
        ILogger<UpdownIngestFunction> logger)
    {
        _webhookService = webhookService;
        _queueClient = queueClient;
        _idempotency = idempotency;
        _ipAllowlist = ipAllowlist;
        _logger = logger;
    }

    [Function("UpdownIngest")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/ingest/updown/{token}")] HttpRequest req,
        string token)
    {
        var correlationId = req.HttpContext.Items["CorrelationId"] as string;
        // NB: never put the token in `instance`/logs — it is a bearer secret.
        const string instance = "/api/v1/ingest/updown";
        // Prefer the first X-Forwarded-For hop, but only if it parses as an IP (Azure sends "ip:port",
        // so strip the port); otherwise fall back to the connection's RemoteIpAddress. A blank/garbage
        // XFF must not become the "source IP" — that would cause an enforce-mode false rejection.
        var xffFirstHop = req.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault();
        var sourceIp = IpMatcher.ParseClientIp(xffFirstHop)
                       ?? IpMatcher.ParseClientIp(req.HttpContext.Connection.RemoteIpAddress?.ToString())
                       ?? "unknown";

        // Source-IP allowlist (design §17) — updown-endpoint-only. Modes: off | log-only | enforce.
        // Placed before body read so an enforced rejection is shed cheaply.
        var ipMode = GetIpFilterMode();
        if (ipMode != "off")
        {
            var allowlist = await _ipAllowlist.GetOrRefreshAsync(TimeSpan.FromHours(GetAllowlistMaxAgeHours()), "lazy");
            var entries = allowlist?.GetCidrs() ?? [];

            if (!IpMatcher.IsAllowed(sourceIp, entries))
            {
                if (entries.Count == 0)
                {
                    // Fail-safe: never block when we have no list at all (e.g. DNS never resolved) —
                    // that would drop every alert on a transient DNS issue. Log the degraded state.
                    _logger.LogWarning(
                        "updown IP allowlist empty/unavailable — allowing without IP check (fail-safe). SourceIp={SourceIp}, CorrelationId={CorrelationId}",
                        Sanitize(sourceIp), correlationId);
                }
                else if (ipMode == "enforce")
                {
                    _logger.LogWarning(
                        "Rejected updown webhook: source IP not in allowlist (enforce). SourceIp={SourceIp}, CorrelationId={CorrelationId}",
                        Sanitize(sourceIp), correlationId);
                    return ApiResponse.Problem(403, "Forbidden", "Source IP not allowed.", instance, correlationId);
                }
                else // log-only
                {
                    _logger.LogWarning(
                        "updown webhook source IP not in allowlist (log-only — not blocked). SourceIp={SourceIp}, CorrelationId={CorrelationId}",
                        Sanitize(sourceIp), correlationId);
                }
            }
        }

        var maxBytes = GetMaxBodyBytes();
        var (tooLarge, body) = await ReadBodyAsync(req, maxBytes);
        if (tooLarge)
        {
            _logger.LogWarning(
                "updown webhook body too large (> {Max} bytes). SourceIp={SourceIp}, CorrelationId={CorrelationId}",
                maxBytes, Sanitize(sourceIp), correlationId);
            return ApiResponse.Problem(413, "Payload Too Large",
                $"Request body exceeds the maximum allowed size of {maxBytes} bytes.", instance, correlationId);
        }

        // Debug payload dump (off by default). Body carries no secret — the token is in the URL, not the body.
        if (DebugDumpEnabled())
        {
            _logger.LogDebug(
                "updown webhook payload dump. SourceIp={SourceIp}, CorrelationId={CorrelationId}, Body={Body}",
                Sanitize(sourceIp), correlationId, Sanitize(body));
        }

        var webhook = await _webhookService.ResolveByTokenAsync(token);
        if (webhook == null)
        {
            // Log a short hash prefix (not the token) so repeated probing is correlatable without leaking the secret.
            _logger.LogWarning(
                "Rejected updown webhook: unknown token. TokenHashPrefix={Prefix}, SourceIp={SourceIp}, CorrelationId={CorrelationId}",
                WebhookService.Sha256Hex(token)[..8], Sanitize(sourceIp), correlationId);
            return ApiResponse.Problem(404, "Not Found", "Unknown webhook token.", instance, correlationId);
        }

        _logger.LogInformation(
            "updown webhook received. WebhookId={WebhookId}, Source={Source}, SourceIp={SourceIp}, CorrelationId={CorrelationId}",
            webhook.Id, Sanitize(webhook.Source), Sanitize(sourceIp), correlationId);

        List<UpdownEvent>? events;
        try
        {
            events = JsonSerializer.Deserialize<List<UpdownEvent>>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Failed to parse updown payload. WebhookId={WebhookId}, CorrelationId={CorrelationId}, Body={Body}",
                webhook.Id, correlationId, Sanitize(body));
            return Accepted(correlationId, 0, 0); // 200: a malformed body will not parse on retry
        }

        if (events == null)
        {
            _logger.LogError(
                "updown payload deserialized to null. WebhookId={WebhookId}, CorrelationId={CorrelationId}",
                webhook.Id, correlationId);
            return Accepted(correlationId, 0, 0);
        }

        var accountLabel = string.IsNullOrWhiteSpace(webhook.UpdownAccount) ? null : webhook.UpdownAccount;
        int enqueued = 0, skipped = 0;

        foreach (var e in events)
        {
            if (!UpdownEventTypes.IsKnown(e.Event))
            {
                _logger.LogInformation(
                    "Skipping unknown/future updown event type '{EventType}'. WebhookId={WebhookId}, CorrelationId={CorrelationId}",
                    Sanitize(e.Event ?? "(null)"), webhook.Id, correlationId);
                skipped++;
                continue;
            }

            if (!webhook.IsEventEnabled(e.Event))
            {
                skipped++;
                continue;
            }

            // Dedupe updown's retries on (webhook, event, check token, time). Only when the event
            // carries a stable identity (token + time both present) — otherwise a leniently-parsed
            // event with null token/time could collapse onto unrelated events and be dropped. The
            // key is hashed to a fixed-size, RowKey-safe hex string (the raw fields may contain
            // characters Table Storage disallows in a RowKey).
            string? dedupeKey = e.Time is { Length: > 0 } t && e.Check?.Token is { Length: > 0 } ct
                ? WebhookService.Sha256Hex($"{webhook.Id}|{e.Event}|{ct}|{t}")
                : null;
            if (dedupeKey != null && await _idempotency.GetAsync(DedupeScope, dedupeKey) != null)
            {
                skipped++;
                continue;
            }

            var card = UpdownCardBuilder.Build(e, accountLabel);
            var queueMessage = new QueueMessage
            {
                MessageId = $"updown-{Guid.NewGuid():N}",
                Target = ToTarget(webhook),
                Message = card,
                Format = "adaptive-card",
                EnqueuedAt = DateTimeOffset.UtcNow
            };

            try
            {
                await _queueClient.SendMessageAsync(JsonSerializer.Serialize(queueMessage));
            }
            catch (Azure.RequestFailedException ex)
            {
                // Storage enqueue failure — return 5xx so updown retries the delivery. Narrowed to
                // the Azure Storage exception; any other unexpected failure bubbles to a host 500
                // (which updown also retries).
                _logger.LogError(ex,
                    "Failed to enqueue updown card. WebhookId={WebhookId}, CorrelationId={CorrelationId}",
                    webhook.Id, correlationId);
                return ApiResponse.Problem(500, "Internal Server Error",
                    "Failed to enqueue notification.", instance, correlationId);
            }

            if (dedupeKey != null)
                await _idempotency.SetAsync(DedupeScope, dedupeKey, 200, string.Empty);
            enqueued++;
        }

        await _webhookService.TouchLastReceivedAsync(webhook);

        _logger.LogInformation(
            "updown webhook processed. WebhookId={WebhookId}, Enqueued={Enqueued}, Skipped={Skipped}, CorrelationId={CorrelationId}",
            webhook.Id, enqueued, skipped, correlationId);

        return Accepted(correlationId, enqueued, skipped);
    }

    private static MessageTarget ToTarget(WebhookTokenEntity w) => new()
    {
        Type = w.TargetType,
        TeamId = w.TeamId,
        ChannelId = w.ChannelId,
        UserId = w.UserId,
        ChatId = w.ChatId
    };

    private static IActionResult Accepted(string? correlationId, int enqueued, int skipped) =>
        new OkObjectResult(new
        {
            status = "ok",
            enqueued,
            skipped,
            correlationId,
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        });

    private static int GetMaxBodyBytes() =>
        int.TryParse(Environment.GetEnvironmentVariable("UpdownWebhook__MaxBodyBytes"), out var v) && v > 0
            ? v : DefaultMaxBodyBytes;

    private static bool DebugDumpEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("UpdownWebhook__DebugLogPayload"),
            "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Source-IP filter mode: "off" | "log-only" | "enforce". Defaults to "log-only".</summary>
    private static string GetIpFilterMode()
    {
        var mode = Environment.GetEnvironmentVariable("UpdownWebhook__IpFilterMode")?.Trim().ToLowerInvariant();
        return mode is "off" or "log-only" or "enforce" ? mode : "log-only";
    }

    private static int GetAllowlistMaxAgeHours() =>
        int.TryParse(Environment.GetEnvironmentVariable("UpdownWebhook__IpAllowlistMaxAgeHours"), out var v) && v > 0
            ? Math.Min(v, 8760)   // clamp to 1 year — guards TimeSpan.FromHours against a misconfigured overflow
            : 48;

    /// <summary>Reads the body with a hard cap. Returns (tooLarge, body); body is "" when tooLarge.</summary>
    private static async Task<(bool tooLarge, string body)> ReadBodyAsync(HttpRequest req, int maxBytes)
    {
        if (req.ContentLength is long len && len > maxBytes)
            return (true, string.Empty);

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await req.Body.ReadAsync(buffer)) > 0)
        {
            if (ms.Length + read > maxBytes)
                return (true, string.Empty);
            ms.Write(buffer, 0, read);
        }
        return (false, Encoding.UTF8.GetString(ms.ToArray()));
    }
}
