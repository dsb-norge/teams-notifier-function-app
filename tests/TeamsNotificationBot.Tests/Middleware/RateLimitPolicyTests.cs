using System.Text.RegularExpressions;
using TeamsNotificationBot.Middleware;
using Xunit;

namespace TeamsNotificationBot.Tests.Middleware;

public class RateLimitPolicyTests
{
    private static bool Matches(string pattern, string path) => Regex.IsMatch(path, pattern);

    [Theory]
    [InlineData("/api/v1/notify/ops", true)]
    [InlineData("/api/v1/alert/ops", true)]
    [InlineData("/api/v1/send", true)]
    [InlineData("/api/v1/checkin/ops", true)]
    [InlineData("/api/v1/ingest/updown/tok", false)] // ingest is excluded from the principal rule
    public void ApiUriPattern_ExcludesIngest(string path, bool expected)
    {
        Assert.Equal(expected, Matches(RateLimitPolicy.ApiUriPattern, path));
    }

    [Theory]
    [InlineData("/api/v1/ingest/updown/tok", true)]
    [InlineData("/api/v1/notify/ops", false)]
    [InlineData("/api/v1/send", false)]
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

    [Fact]
    public void SourceIpKey_UsesFirstForwardedHop()
    {
        Assert.Equal("ingest-ip:1.2.3.4", RateLimitPolicy.SourceIpKey("1.2.3.4, 5.6.7.8", null));
    }

    [Fact]
    public void SourceIpKey_FallsBackToRemoteIp()
    {
        Assert.Equal("ingest-ip:9.9.9.9", RateLimitPolicy.SourceIpKey(null, "9.9.9.9"));
        Assert.Equal("ingest-ip:9.9.9.9", RateLimitPolicy.SourceIpKey("  ", "9.9.9.9"));
    }

    [Fact]
    public void SourceIpKey_UnknownWhenNothingAvailable()
    {
        Assert.Equal("ingest-ip:unknown", RateLimitPolicy.SourceIpKey(null, null));
    }

    [Fact]
    public void IngestAndApiPatterns_AreDisjoint()
    {
        // No path should match both rules.
        foreach (var path in new[]
        {
            "/api/v1/notify/x", "/api/v1/alert/x", "/api/v1/send",
            "/api/v1/ingest/updown/tok"
        })
        {
            var inApi = Matches(RateLimitPolicy.ApiUriPattern, path);
            var inIngest = Matches(RateLimitPolicy.IngestUriPattern, path);
            Assert.False(inApi && inIngest, $"{path} matched both rules");
        }
    }
}
