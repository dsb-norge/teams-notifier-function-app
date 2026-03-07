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

        // Skip auth for bot messages endpoint (uses Bot Framework JWT auth), health probe, and OpenAPI spec
        if (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/openapi.yaml", StringComparison.OrdinalIgnoreCase))
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
    private static bool HasRequiredRole(HttpContext httpContext, out string? roles)
    {
        roles = null;
        var principalHeader = httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL"].FirstOrDefault();
        if (string.IsNullOrEmpty(principalHeader))
            return false;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(principalHeader));
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("claims", out var claims))
                return false;

            var roleValues = claims.EnumerateArray()
                .Where(c => c.TryGetProperty("typ", out var typ) &&
                            typ.GetString() is "roles" or "http://schemas.microsoft.com/ws/2008/06/identity/claims/role")
                .Select(c => c.GetProperty("val").GetString())
                .Where(v => v != null)
                .ToList();

            roles = roleValues.Count > 0 ? string.Join(", ", roleValues) : null;
            return roleValues.Contains(RequiredRole, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
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
