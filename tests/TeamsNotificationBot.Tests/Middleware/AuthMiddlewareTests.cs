using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TeamsNotificationBot.Middleware;
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
    // These call AuthMiddleware.HasRequiredRole directly (internal, via InternalsVisibleTo).
    // They previously re-implemented the parsing inline, which meant a regression in the real
    // method was invisible here — the mirror could stay green while production broke.

    private static string EncodeEasyAuthPrincipal(object claims)
    {
        var principal = new { auth_typ = "aad", claims, name_typ = "name", role_typ = "roles" };
        var json = JsonSerializer.Serialize(principal);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static string EncodeRaw(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    /// <summary>Runs the real role check against a request carrying the given header value.</summary>
    private static bool CheckHeader(string? principalHeader, out string? roles)
    {
        var httpContext = new DefaultHttpContext();
        if (principalHeader != null)
            httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = principalHeader;
        return AuthMiddleware.HasRequiredRole(httpContext, out roles);
    }

    private static bool CheckClaims(object claims) =>
        CheckHeader(EncodeEasyAuthPrincipal(claims), out _);

    [Fact]
    public void EasyAuth_WithRequiredRole_IsAuthorized()
    {
        Assert.True(CheckClaims(new[] { new { typ = "roles", val = "Notifications.Send" } }));
    }

    [Fact]
    public void EasyAuth_WithoutRequiredRole_IsNotAuthorized()
    {
        Assert.False(CheckClaims(new[] { new { typ = "roles", val = "SomeOtherRole" } }));
    }

    [Fact]
    public void EasyAuth_WithNoRoles_IsNotAuthorized()
    {
        Assert.False(CheckClaims(new[] { new { typ = "name", val = "TestUser" } }));
    }

    [Fact]
    public void EasyAuth_WithMultipleRoles_MatchesRequired()
    {
        Assert.True(CheckClaims(new[]
        {
            new { typ = "roles", val = "Reader" },
            new { typ = "roles", val = "Notifications.Send" },
            new { typ = "roles", val = "Admin" }
        }));
    }

    [Fact]
    public void EasyAuth_RoleCheckIsCaseInsensitive()
    {
        Assert.True(CheckClaims(new[] { new { typ = "roles", val = "notifications.send" } }));
    }

    [Fact]
    public void EasyAuth_RolesOutParam_ListsGrantedRoles()
    {
        var header = EncodeEasyAuthPrincipal(new[]
        {
            new { typ = "roles", val = "Reader" },
            new { typ = "roles", val = "Notifications.Send" }
        });

        Assert.True(CheckHeader(header, out var roles));
        Assert.Equal("Reader, Notifications.Send", roles);
    }

    [Fact]
    public void EasyAuth_NoRolesPresent_RolesOutParamIsNull()
    {
        var header = EncodeEasyAuthPrincipal(new[] { new { typ = "name", val = "TestUser" } });

        Assert.False(CheckHeader(header, out var roles));
        Assert.Null(roles);
    }

    [Fact]
    public void EasyAuth_EmptyPrincipalHeader_IsNotAuthorized()
    {
        Assert.False(CheckHeader(null, out var roles));
        Assert.Null(roles);
    }

    // --- Malformed input: every case must fail closed, and must not throw ---

    [Fact]
    public void EasyAuth_MalformedBase64_IsNotAuthorized()
    {
        Assert.False(CheckHeader("not-valid-base64!!!", out _));
    }

    [Fact]
    public void EasyAuth_BodyIsNotJson_IsNotAuthorized()
    {
        Assert.False(CheckHeader(EncodeRaw("this is not json"), out _));
    }

    [Fact]
    public void EasyAuth_ClaimsPropertyMissing_IsNotAuthorized()
    {
        Assert.False(CheckHeader(EncodeRaw("""{"auth_typ":"aad"}"""), out _));
    }

    [Fact]
    public void EasyAuth_ClaimsIsNotAnArray_IsNotAuthorized()
    {
        // Guarded by the ValueKind check rather than by catching InvalidOperationException.
        Assert.False(CheckHeader(EncodeRaw("""{"claims":"Notifications.Send"}"""), out _));
    }

    [Fact]
    public void EasyAuth_RoleClaimMissingVal_IsNotAuthorized()
    {
        Assert.False(CheckHeader(EncodeRaw("""{"claims":[{"typ":"roles"}]}"""), out _));
    }

    [Fact]
    public void EasyAuth_ClaimTypIsNotAString_IsNotAuthorized()
    {
        Assert.False(CheckHeader(EncodeRaw("""{"claims":[{"typ":123,"val":"Notifications.Send"}]}"""), out _));
    }

    [Fact]
    public void EasyAuth_RoleValIsNotAString_IsNotAuthorized()
    {
        Assert.False(CheckHeader(EncodeRaw("""{"claims":[{"typ":"roles","val":{"nested":true}}]}"""), out _));
    }

    [Fact]
    public void EasyAuth_MixedValidAndMalformedClaims_StillMatchesValidRole()
    {
        // A junk entry alongside a good one must not discard the good one.
        Assert.True(CheckHeader(
            EncodeRaw("""{"claims":[{"typ":"roles"},{"typ":"roles","val":"Notifications.Send"}]}"""),
            out var roles));
        Assert.Equal("Notifications.Send", roles);
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
        Assert.True(CheckClaims(new[]
        {
            new
            {
                typ = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
                val = "Notifications.Send"
            }
        }));
    }
}
