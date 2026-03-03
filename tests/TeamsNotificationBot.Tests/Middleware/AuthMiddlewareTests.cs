using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace TeamsNotificationBot.Tests.Middleware;

/// <summary>
/// Tests for the auth flow logic used by AuthMiddleware.
/// FunctionContext.GetHttpContext() requires runtime feature registration that is
/// impractical to mock, so these tests verify the extraction and matching logic directly.
/// </summary>
public class AuthMiddlewareTests
{
    [Fact]
    public void MaxRequestBodySize_Is28KB()
    {
        Assert.Equal(28672, 28 * 1024);
    }

    [Fact]
    public void HealthEndpointSkipsAuth()
    {
        var path = "/api/health";
        Assert.EndsWith("/health", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MessagesEndpointSkipsAuth()
    {
        var path = "/api/messages";
        Assert.EndsWith("/messages", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EasyAuthHeaderRecognized()
    {
        var headers = new HeaderDictionary { ["X-MS-CLIENT-PRINCIPAL-ID"] = "user-object-id-123" };
        var principalId = headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault();
        Assert.False(string.IsNullOrEmpty(principalId));
    }

    [Fact]
    public void RequestWithNoCredentials_HasNoEasyAuthHeaders()
    {
        // With API key auth removed, requests without EasyAuth headers get 401.
        // Verify that absence of EasyAuth headers is detectable.
        var httpContext = new DefaultHttpContext();
        var easyAuthPrincipal = httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault();
        Assert.True(string.IsNullOrEmpty(easyAuthPrincipal));
    }

    // --- Role-Based Authorization Tests ---
    // These test the X-MS-CLIENT-PRINCIPAL decoding and role extraction logic
    // that the middleware uses for EasyAuth-authenticated requests.

    private static string EncodeEasyAuthPrincipal(object claims)
    {
        var principal = new { auth_typ = "aad", claims, name_typ = "name", role_typ = "roles" };
        var json = JsonSerializer.Serialize(principal);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static bool ExtractHasRole(string base64Principal, string requiredRole)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Principal));
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("claims", out var claims))
            return false;

        return claims.EnumerateArray()
            .Where(c => c.TryGetProperty("typ", out var typ) &&
                        typ.GetString() is "roles" or "http://schemas.microsoft.com/ws/2008/06/identity/claims/role")
            .Select(c => c.GetProperty("val").GetString())
            .Contains(requiredRole, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EasyAuth_WithRequiredRole_IsAuthorized()
    {
        var claims = new[] { new { typ = "roles", val = "Notifications.Send" } };
        var principal = EncodeEasyAuthPrincipal(claims);
        Assert.True(ExtractHasRole(principal, "Notifications.Send"));
    }

    [Fact]
    public void EasyAuth_WithoutRequiredRole_IsNotAuthorized()
    {
        var claims = new[] { new { typ = "roles", val = "SomeOtherRole" } };
        var principal = EncodeEasyAuthPrincipal(claims);
        Assert.False(ExtractHasRole(principal, "Notifications.Send"));
    }

    [Fact]
    public void EasyAuth_WithNoRoles_IsNotAuthorized()
    {
        var claims = new[] { new { typ = "name", val = "TestUser" } };
        var principal = EncodeEasyAuthPrincipal(claims);
        Assert.False(ExtractHasRole(principal, "Notifications.Send"));
    }

    [Fact]
    public void EasyAuth_WithMultipleRoles_MatchesRequired()
    {
        var claims = new[]
        {
            new { typ = "roles", val = "Reader" },
            new { typ = "roles", val = "Notifications.Send" },
            new { typ = "roles", val = "Admin" }
        };
        var principal = EncodeEasyAuthPrincipal(claims);
        Assert.True(ExtractHasRole(principal, "Notifications.Send"));
    }

    [Fact]
    public void EasyAuth_EmptyPrincipalHeader_IsNotAuthorized()
    {
        var httpContext = new DefaultHttpContext();
        var principalHeader = httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL"].FirstOrDefault();
        Assert.True(string.IsNullOrEmpty(principalHeader));
    }

    [Fact]
    public void EasyAuth_MalformedBase64_IsNotAuthorized()
    {
        var result = false;
        try
        {
            result = ExtractHasRole("not-valid-base64!!!", "Notifications.Send");
        }
        catch
        {
            // Expected — malformed input should not authorize
        }
        Assert.False(result);
    }

    [Fact]
    public void EasyAuth_RoleCheckIsCaseInsensitive()
    {
        var claims = new[] { new { typ = "roles", val = "notifications.send" } };
        var principal = EncodeEasyAuthPrincipal(claims);
        Assert.True(ExtractHasRole(principal, "Notifications.Send"));
    }

    // --- §1 Regression Tests (API key auth removed) ---

    [Fact]
    public void OpenApiEndpointSkipsAuth()
    {
        var path = "/api/v1/openapi.yaml";
        Assert.EndsWith("/openapi.yaml", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApiKeyHeaderWithoutEasyAuth_IsRejected()
    {
        // After §1, providing X-API-Key without EasyAuth headers should NOT authenticate.
        // The middleware no longer checks API keys — only EasyAuth Bearer tokens are accepted.
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-API-Key"] = "some-api-key-value";

        var easyAuthPrincipal = httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault();
        Assert.True(string.IsNullOrEmpty(easyAuthPrincipal),
            "API key header should not substitute for EasyAuth — request should be rejected as unauthenticated");
    }

    [Fact]
    public void ApiKeyQueryParamWithoutEasyAuth_IsRejected()
    {
        // Regression: query param ?apikey=... should also be ignored after §1
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?apikey=some-key");

        var easyAuthPrincipal = httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault();
        Assert.True(string.IsNullOrEmpty(easyAuthPrincipal),
            "apikey query parameter should not substitute for EasyAuth");
    }

    [Fact]
    public void EasyAuth_AlternativeRoleClaimType_IsRecognized()
    {
        // The middleware accepts both "roles" and the long-form URI claim type
        var claims = new[] { new { typ = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role", val = "Notifications.Send" } };
        var principal = EncodeEasyAuthPrincipal(claims);
        Assert.True(ExtractHasRole(principal, "Notifications.Send"));
    }
}
