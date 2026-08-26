using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Helpers;
using static TeamsNotificationBot.Helpers.LogSanitizer;

namespace TeamsNotificationBot.Middleware;

public class AuthMiddleware : IFunctionsWorkerMiddleware
{
    private const int MaxRequestBodyBytes = 28 * 1024; // 28 KB — Teams message size limit
    private const string RequiredRole = "Notifications.Send";
    private readonly ILogger<AuthMiddleware> _logger;

    public AuthMiddleware(ILogger<AuthMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            // Not an HTTP trigger (e.g. queue trigger), skip auth
            await next(context);
            return;
        }

        var path = httpContext.Request.Path.Value ?? "";

        // Generate correlation ID for all HTTP requests
        var correlationId = Guid.NewGuid().ToString("N");
        httpContext.Items["CorrelationId"] = correlationId;
        httpContext.Response.Headers["X-Correlation-Id"] = correlationId;

        // Skip auth for bot messages endpoint (uses Bot Framework JWT auth), health probe, and OpenAPI spec.
        // Also skip the anonymous updown.io webhook ingress: it is a distinct trust zone that performs its
        // own token validation in-handler (no EasyAuth principal). See docs/feat-updown-io-webhook/design.md.
        if (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/openapi.yaml", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/v1/ingest/", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var sourceIp = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                       ?? httpContext.Connection.RemoteIpAddress?.ToString()
                       ?? "unknown";

        // Check EasyAuth: if platform validated a Bearer token, X-MS-CLIENT-PRINCIPAL-ID is set
        var easyAuthPrincipal = httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault();
        if (!string.IsNullOrEmpty(easyAuthPrincipal))
        {
            // Authorization: check for required app role in EasyAuth claims
            if (!HasRequiredRole(httpContext, out var roles))
            {
                _logger.LogWarning(
                    "Authorization failed: missing required role '{RequiredRole}'. Endpoint={Endpoint}, Principal={Principal}, Roles={Roles}, SourceIp={SourceIp}, CorrelationId={CorrelationId}",
                    RequiredRole, Sanitize(path), Sanitize(easyAuthPrincipal), string.IsNullOrEmpty(roles) ? "none" : Sanitize(roles), Sanitize(sourceIp), correlationId);

                await ApiResponse.WriteProblemAsync(
                    httpContext.Response, 403, "Forbidden",
                    $"The caller does not have the required '{RequiredRole}' app role.",
                    path, correlationId);
                return;
            }

            _logger.LogInformation(
                "Authentication succeeded via EasyAuth. Endpoint={Endpoint}, Principal={Principal}, SourceIp={SourceIp}, CorrelationId={CorrelationId}",
                Sanitize(path), Sanitize(easyAuthPrincipal), Sanitize(sourceIp), correlationId);
            await ValidateRequestSizeAndProceed(httpContext, path, correlationId, context, next);
            return;
        }

        // No EasyAuth credentials — reject
        _logger.LogWarning(
            "Authentication failed: no credentials provided. Endpoint={Endpoint}, SourceIp={SourceIp}, CorrelationId={CorrelationId}",
            Sanitize(path), Sanitize(sourceIp), correlationId);

        await ApiResponse.WriteProblemAsync(
            httpContext.Response, 401, "Unauthorized",
            "No authentication credentials provided. Supply a valid Bearer token (Entra ID).",
            path, correlationId);
    }

    /// <summary>
    /// Decodes the EasyAuth X-MS-CLIENT-PRINCIPAL header (Base64 JSON) and checks for the required app role.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than <c>private</c> so AuthMiddlewareTests can exercise this method
    /// itself (via <c>InternalsVisibleTo</c>). The tests used to re-implement the parsing inline,
    /// which meant a regression here was invisible to them.
    /// </remarks>
    internal static bool HasRequiredRole(HttpContext httpContext, out string? roles)
    {
        roles = null;
        var principalHeader = httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL"].FirstOrDefault();
        if (string.IsNullOrEmpty(principalHeader))
            return false;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(principalHeader));
            using var doc = JsonDocument.Parse(json);

            // Shape checks are explicit rather than exception-driven: a caller-supplied header
            // whose "claims" is absent or not an array, or whose entries lack a string "val", is
            // ordinary malformed input. Guarding on ValueKind also means EnumerateArray and
            // GetString below cannot throw, which keeps the catch filter honest.
            if (!doc.RootElement.TryGetProperty("claims", out var claims) ||
                claims.ValueKind != JsonValueKind.Array)
                return false;

            var roleValues = claims.EnumerateArray()
                .Where(c => c.TryGetProperty("typ", out var typ) &&
                            typ.ValueKind == JsonValueKind.String &&
                            typ.GetString() is "roles" or "http://schemas.microsoft.com/ws/2008/06/identity/claims/role")
                .Select(c => c.TryGetProperty("val", out var val) && val.ValueKind == JsonValueKind.String
                    ? val.GetString()
                    : null)
                .Where(v => v != null)
                .ToList();

            roles = roleValues.Count > 0 ? string.Join(", ", roleValues) : null;
            return roleValues.Contains(RequiredRole, StringComparer.OrdinalIgnoreCase);
        }
        // Exactly the two exceptions a caller can provoke: a header that isn't Base64, and a
        // decoded body that isn't JSON. Both mean "unauthenticated" and fail closed. Anything
        // else is a defect in this method and must surface as a 500 instead of being masked as
        // a 403 — the previous catch-all made a bug here indistinguishable from a denied caller.
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    private async Task ValidateRequestSizeAndProceed(
        HttpContext httpContext,
        string path,
        string correlationId,
        FunctionContext context,
        FunctionExecutionDelegate next)
    {
        // Validate request body size (Content-Length header check)
        if (httpContext.Request.ContentLength > MaxRequestBodyBytes)
        {
            _logger.LogWarning(
                "Request too large: {ContentLength} bytes. Endpoint={Endpoint}, CorrelationId={CorrelationId}",
                httpContext.Request.ContentLength, Sanitize(path), correlationId);

            await ApiResponse.WriteProblemAsync(
                httpContext.Response, 413, "Payload Too Large",
                $"Request body exceeds the maximum allowed size of {MaxRequestBodyBytes} bytes.",
                path, correlationId);
            return;
        }

        await next(context);
    }
}
