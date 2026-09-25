using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Middleware;
using Xunit;

namespace TeamsNotificationBot.Tests.Middleware;

/// <summary>
/// Tests for AuthMiddleware. The pure checks (<c>IsAuthExempt</c>, <c>HasRequiredRole</c>) are
/// called directly; request-level behavior runs the real <c>Invoke</c> through
/// <see cref="InvokeAsync"/>, which hands it a mocked FunctionContext carrying a DefaultHttpContext.
/// </summary>
public class AuthMiddlewareTests
{
    // FunctionContext.GetHttpContext() (Worker.Extensions.Http.AspNetCore) is nothing more than a
    // lookup of this Items key. The constant is internal to that package, so it is mirrored here;
    // if a package bump renames it, every Invoke test fails with next-called / wrong-status.
    private const string HttpContextItemsKey = "HttpRequestContext";

    /// <summary>Runs the real middleware on <paramref name="httpContext"/>; returns whether it called next.</summary>
    private static async Task<bool> InvokeAsync(HttpContext httpContext)
    {
        var functionContext = new Mock<FunctionContext>();
        functionContext.Setup(c => c.Items).Returns(new Dictionary<object, object>
        {
            [HttpContextItemsKey] = httpContext
        });
        var nextCalled = false;
        await new AuthMiddleware(NullLogger<AuthMiddleware>.Instance).Invoke(
            functionContext.Object, _ => { nextCalled = true; return Task.CompletedTask; });
        return nextCalled;
    }

    /// <summary>A protected-route POST from a caller EasyAuth authenticated with the required role.</summary>
    private static DefaultHttpContext AuthorizedPost(long contentLength)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/api/v1/notify/some-alias";
        httpContext.Request.ContentLength = contentLength;
        httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"] = "caller-object-id";
        httpContext.Request.Headers[PrincipalHeader] = EncodeEasyAuthPrincipal(
            new[] { new { typ = "roles", val = "Notifications.Send" } });
        return httpContext;
    }

    // --- Request body size limit (28 KB, the Teams message size limit) ---

    [Fact]
    public async Task RequestBody_AtLimit_Proceeds()
    {
        var httpContext = AuthorizedPost(28 * 1024);

        Assert.True(await InvokeAsync(httpContext));
        Assert.NotEqual(StatusCodes.Status413PayloadTooLarge, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task RequestBody_AboveLimit_Returns413WithoutCallingNext()
    {
        var httpContext = AuthorizedPost(28 * 1024 + 1);

        Assert.False(await InvokeAsync(httpContext));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, httpContext.Response.StatusCode);
    }

    // --- Auth-exempt routes ---
    // These call AuthMiddleware.IsAuthExempt directly. The earlier versions asserted string methods
    // against literal paths and so could not notice that the real check used suffix matching.

    [Theory]
    [InlineData("/api/messages")]
    [InlineData("/api/health")]
    [InlineData("/api/v1/openapi.yaml")]
    [InlineData("/api/HEALTH")]
    [InlineData("/api/v1/ingest/updown/abc123")]
    public void IsAuthExempt_AnonymousRoutes(string path)
    {
        Assert.True(AuthMiddleware.IsAuthExempt(path));
    }

    [Theory]
    // "health" and "messages" are valid alias names: a suffix match exempted these.
    [InlineData("/api/v1/notify/health")]
    [InlineData("/api/v1/notify/messages")]
    [InlineData("/api/v1/alert/health")]
    [InlineData("/api/v1/checkin/messages")]
    [InlineData("/api/v1/send")]
    [InlineData("/api/v1/aliases")]
    [InlineData("/api/v1/notify/x/v1/ingest/updown/y")]
    [InlineData("/api/v1/ingest/updown")]
    [InlineData("/api/health/")]
    [InlineData("")]
    public void IsAuthExempt_ProtectedRoutes(string path)
    {
        Assert.False(AuthMiddleware.IsAuthExempt(path));
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

    private const string PrincipalHeader = "X-MS-CLIENT-PRINCIPAL";

    /// <summary>Runs the real role check against a request carrying the given header value.</summary>
    private static bool CheckHeader(string? principalHeader, out string? roles)
    {
        var httpContext = new DefaultHttpContext();
        if (principalHeader != null)
            httpContext.Request.Headers[PrincipalHeader] = principalHeader;
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

    // HasRequiredRole guards with string.IsNullOrEmpty, so both inputs are worth pinning
    // separately: header absent, and header present but empty.

    [Fact]
    public void EasyAuth_MissingPrincipalHeader_IsNotAuthorized()
    {
        Assert.False(CheckHeader(null, out var roles));
        Assert.Null(roles);
    }

    [Fact]
    public void EasyAuth_EmptyPrincipalHeader_IsNotAuthorized()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[PrincipalHeader] = string.Empty;

        // Guard the guard: HeaderDictionary drops a key assigned an empty StringValues, which
        // would quietly turn this into a second copy of the missing-header test above.
        Assert.True(httpContext.Request.Headers.ContainsKey(PrincipalHeader));

        Assert.False(AuthMiddleware.HasRequiredRole(httpContext, out var roles));
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
    public async Task ApiKeyHeaderWithoutEasyAuth_IsRejected()
    {
        // After §1, providing X-API-Key without EasyAuth headers should NOT authenticate.
        // The middleware no longer checks API keys — only EasyAuth Bearer tokens are accepted.
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/v1/notify/some-alias";
        httpContext.Request.Headers["X-API-Key"] = "some-api-key-value";

        Assert.False(await InvokeAsync(httpContext), "API key header must not substitute for EasyAuth");
        Assert.Equal(StatusCodes.Status401Unauthorized, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyQueryParamWithoutEasyAuth_IsRejected()
    {
        // Regression: query param ?apikey=... should also be ignored after §1
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/v1/notify/some-alias";
        httpContext.Request.QueryString = new QueryString("?apikey=some-key");

        Assert.False(await InvokeAsync(httpContext), "apikey query parameter must not substitute for EasyAuth");
        Assert.Equal(StatusCodes.Status401Unauthorized, httpContext.Response.StatusCode);
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
