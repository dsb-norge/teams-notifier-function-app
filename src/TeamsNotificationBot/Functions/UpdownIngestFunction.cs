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
        // Resolve the client IP from forwarding headers (X-Forwarded-For, then the App Service
        // X-Azure-* headers), falling back to RemoteIpAddress. On Flex + isolated worker the
        // connection is loopback (::1/127.0.0.1), so a forwarding header is the only real source —
        // see IpMatcher.ClientIpHeaders and refinements.md (F8).
        var sourceIp = IpMatcher.ExtractClientIp(
                           name => req.Headers.TryGetValue(name, out var v) ? v.ToString() : null,
                           req.HttpContext.Connection.RemoteIpAddress?.ToString())
                       ?? "unknown";

        LogHeaderDiagnostics(req, correlationId);

        // Source-IP allowlist (design §17) — updown-endpoint-only. Modes: off | log-only | enforce.
        // Placed before body read so an enforced rejection is shed cheaply.
        var ipMode = UpdownWebhookConfig.IpFilterMode;
        if (ipMode != "off")
        {
            var allowlist = await _ipAllowlist.GetOrRefreshAsync(UpdownWebhookConfig.AllowlistMaxAge, "lazy");
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

    private static bool DebugLogHeadersEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("UpdownWebhook__DebugLogHeaders"),
            "true", StringComparison.OrdinalIgnoreCase);

    // Explicit allowlist of IP-carrying headers whose VALUES are safe to log for the F8 diagnostic.
    // Deliberately not a substring match (e.g. "client"/"for") — that would also dump sensitive headers
    // like X-MS-CLIENT-PRINCIPAL. On Flex the real client IP is in CLIENT-IP (see IpMatcher).
    private static readonly HashSet<string> DiagnosticIpHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLIENT-IP", "X-Forwarded-For", "X-Azure-ClientIP", "X-Azure-SocketIP",
        "X-Client-IP", "X-Real-IP", "X-Original-For", "Forwarded",
    };

    /// <summary>
    /// Diagnostic that discovered the F8 fix: on Flex + isolated worker the real client IP is in the
    /// <c>CLIENT-IP</c> header (not <c>X-Forwarded-For</c>/<c>X-Azure-*</c>, which are absent). Kept
    /// (off by default; enable via <c>UpdownWebhook__DebugLogHeaders=true</c>) for future header
    /// changes. Logs the VALUES of an explicit IP-header allowlist only (<see cref="DiagnosticIpHeaders"/>)
    /// plus all header NAMES — never the values of sensitive headers (Authorization, Cookie,
    /// X-MS-CLIENT-PRINCIPAL, …).
    /// </summary>
    private void LogHeaderDiagnostics(HttpRequest req, string? correlationId)
    {
        if (!DebugLogHeadersEnabled())
            return;

        var ipHeaders = string.Join("; ", req.Headers
            .Where(h => DiagnosticIpHeaders.Contains(h.Key))
            .Select(h => $"{h.Key}={h.Value}"));
        var names = string.Join(",", req.Headers.Keys);

        _logger.LogWarning(
            "updown ingest header dump (debug F8). CorrelationId={CorrelationId}, RemoteIp={RemoteIp}, IpHeaders=[{IpHeaders}], AllHeaderNames=[{Names}]",
            correlationId,
            Sanitize(req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "null"),
            Sanitize(ipHeaders),
            Sanitize(names));
    }

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
