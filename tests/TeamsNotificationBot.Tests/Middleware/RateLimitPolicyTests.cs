using System.Text.RegularExpressions;
using TeamsNotificationBot.Middleware;
using Xunit;

namespace TeamsNotificationBot.Tests.Middleware;

public class RateLimitPolicyTests
{
    // ThrottlingTroll matches UriPattern case-insensitively against the full URL, query string
    // included (IncomingHttpRequestProxy.Uri), so the tests do the same.
    private const string Host = "https://func-example.azurewebsites.net";

    private static bool Matches(string pattern, string pathAndQuery) =>
        Regex.IsMatch(Host + pathAndQuery, pattern, RegexOptions.IgnoreCase);

    [Theory]
    [InlineData("/api/v1/notify/ops", true)]
    [InlineData("/api/v1/alert/ops", true)]
    [InlineData("/api/v1/send", true)]
    [InlineData("/api/v1/checkin/ops", true)]
    [InlineData("/api/v1/aliases", true)]
    [InlineData("/api/v1/send?x=1", true)]
    [InlineData("/api/v1/ingest/updown/tok", false)] // ingest is excluded from the principal rule
    [InlineData("/api/v1/openapi.yaml", false)] // anonymous: a forged principal header reaches the app
    [InlineData("/API/V1/OPENAPI.YAML", false)]
    [InlineData("/api/v1/openapi.yaml?x=1", false)]
    // A query string must not pull a route into the rule: the match is anchored on the path.
    [InlineData("/api/messages?x=/api/v1/send", false)]
    [InlineData("/api/health?x=/api/v1/send", false)]
    [InlineData("/api/v1/openapi.yaml?x=/api/v1/send", false)]
    [InlineData("/api/v1/ingest/updown/tok?x=/api/v1/send", false)]
    public void ApiUriPattern_ExcludesAnonymousRoutes(string path, bool expected)
    {
        Assert.Equal(expected, Matches(RateLimitPolicy.ApiUriPattern, path));
    }

    [Theory]
    [InlineData("/api/v1/ingest/updown/tok", true)]
    [InlineData("/api/v1/notify/ops", false)]
    [InlineData("/api/v1/send", false)]
    [InlineData("/api/messages?x=/api/v1/ingest/", false)]
    public void IngestUriPattern_MatchesOnlyIngest(string path, bool expected)
    {
        Assert.Equal(expected, Matches(RateLimitPolicy.IngestUriPattern, path));
    }

    [Fact]
    public void PrincipalKey_NullOrEmpty_ReturnsNull_SoRuleIsSkipped()
    {
        Assert.Null(RateLimitPolicy.PrincipalKey(null));
        Assert.Null(RateLimitPolicy.PrincipalKey(""));
        Assert.Equal("oid-123", RateLimitPolicy.PrincipalKey("oid-123"));
    }

    // Build a header accessor from a single header (or from a name→value map).
    private static Func<string, string?> Hdr(string name, string? value) =>
        n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) ? value : null;

    private static Func<string, string?> Hdrs(Dictionary<string, string?> map) =>
        n => map.TryGetValue(n, out var v) ? v : null;

    [Fact]
    public void SourceIpKey_UsesFirstForwardedHop()
    {
        Assert.Equal("ingest-ip:1.2.3.4", RateLimitPolicy.SourceIpKey(Hdr("X-Forwarded-For", "1.2.3.4, 5.6.7.8"), null));
    }

    [Fact]
    public void SourceIpKey_FallsBackToRemoteIp()
    {
        Assert.Equal("ingest-ip:9.9.9.9", RateLimitPolicy.SourceIpKey(_ => null, "9.9.9.9"));
        Assert.Equal("ingest-ip:9.9.9.9", RateLimitPolicy.SourceIpKey(Hdr("X-Forwarded-For", "  "), "9.9.9.9"));
    }

    [Fact]
    public void SourceIpKey_UnknownWhenNothingAvailable()
    {
        Assert.Equal("ingest-ip:unknown", RateLimitPolicy.SourceIpKey(_ => null, null));
    }

    [Fact]
    public void SourceIpKey_StripsAzurePort_SoSameIpSharesOneKey()
    {
        // Azure X-Forwarded-For is "ip:port"; the port must be stripped or each request keys separately
        // and the per-source-IP limit never triggers.
        Assert.Equal("ingest-ip:1.2.3.4", RateLimitPolicy.SourceIpKey(Hdr("X-Forwarded-For", "1.2.3.4:51789"), null));
        Assert.Equal(
            RateLimitPolicy.SourceIpKey(Hdr("X-Forwarded-For", "1.2.3.4:51789"), null),
            RateLimitPolicy.SourceIpKey(Hdr("X-Forwarded-For", "1.2.3.4:52000"), null));
    }

    [Fact]
    public void SourceIpKey_FallsBackToAzureClientIp_WhenNoForwardedFor()
    {
        // F8: on Flex + isolated worker, X-Forwarded-For may be absent and RemoteIpAddress is loopback.
        // The App Service X-Azure-ClientIP header carries the real client IP.
        Assert.Equal("ingest-ip:203.0.113.7", RateLimitPolicy.SourceIpKey(
            Hdrs(new Dictionary<string, string?> { ["X-Azure-ClientIP"] = "203.0.113.7" }), "::1"));
    }

    [Fact]
    public void SourceIpKey_PrefersForwardedFor_OverAzureHeaders()
    {
        Assert.Equal("ingest-ip:198.51.100.5", RateLimitPolicy.SourceIpKey(
            Hdrs(new Dictionary<string, string?>
            {
                ["X-Forwarded-For"] = "198.51.100.5",
                ["X-Azure-ClientIP"] = "203.0.113.7",
            }), "::1"));
    }

    [Fact]
    public void IngestAndApiPatterns_AreDisjoint()
    {
        // No path should match both rules.
        foreach (var path in new[]
        {
            "/api/v1/notify/x", "/api/v1/alert/x", "/api/v1/send",
            "/api/v1/ingest/updown/tok", "/api/v1/ingest/updown/tok?x=/api/v1/send",
            "/api/v1/openapi.yaml"
        })
        {
            var inApi = Matches(RateLimitPolicy.ApiUriPattern, path);
            var inIngest = Matches(RateLimitPolicy.IngestUriPattern, path);
            Assert.False(inApi && inIngest, $"{path} matched both rules");
        }
    }
}
