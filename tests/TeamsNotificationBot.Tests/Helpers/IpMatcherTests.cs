using TeamsNotificationBot.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Helpers;

public class IpMatcherTests
{
    private static readonly string[] List = ["1.2.3.4", "10.0.0.0/8", "2001:db8::/32", "2001:db8:cafe::1"];

    [Theory]
    [InlineData("1.2.3.4", true)]           // exact IPv4
    [InlineData("10.5.6.7", true)]          // inside 10.0.0.0/8
    [InlineData("11.0.0.1", false)]         // outside all
    [InlineData("1.2.3.5", false)]          // near the exact IP but not it
    [InlineData("2001:db8::abcd", true)]    // inside IPv6 CIDR
    [InlineData("2001:db8:cafe::1", true)]  // exact IPv6
    [InlineData("2001:dead::1", false)]     // outside IPv6 CIDR
    public void IsAllowed_MatchesIpsAndCidrs(string ip, bool expected)
    {
        Assert.Equal(expected, IpMatcher.IsAllowed(ip, List));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("unknown")]
    public void IsAllowed_InvalidSource_ReturnsFalse(string? ip)
    {
        Assert.False(IpMatcher.IsAllowed(ip, List));
    }

    [Fact]
    public void IsAllowed_EmptyList_ReturnsFalse()
    {
        Assert.False(IpMatcher.IsAllowed("1.2.3.4", []));
    }

    [Fact]
    public void IsAllowed_IgnoresBlankEntries()
    {
        Assert.True(IpMatcher.IsAllowed("1.2.3.4", ["", "  ", "1.2.3.4"]));
    }

    [Fact]
    public void IsAllowed_MalformedEntries_AreSkipped_NotThrown()
    {
        // A junk entry must not throw; a valid one after it still matches.
        Assert.True(IpMatcher.IsAllowed("1.2.3.4", ["garbage/99", "not-a-cidr", "1.2.3.0/24"]));
        Assert.False(IpMatcher.IsAllowed("9.9.9.9", ["garbage", "10.0.0.0/8"]));
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4")]              // bare IPv4
    [InlineData("1.2.3.4:5678", "1.2.3.4")]         // Azure XFF form (ip:port) — port stripped
    [InlineData("2001:db8::1", "2001:db8::1")]      // bare IPv6
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]// bracketed IPv6 + port
    [InlineData("  1.2.3.4  ", "1.2.3.4")]          // trimmed
    public void ParseClientIp_NormalizesToBareIp(string input, string expected)
    {
        Assert.Equal(expected, IpMatcher.ParseClientIp(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("not-an-ip")]
    public void ParseClientIp_InvalidReturnsNull(string? input)
    {
        Assert.Null(IpMatcher.ParseClientIp(input));
    }

    // --- ExtractClientIp (F8): header priority + loopback fallback ---

    private static Func<string, string?> Headers(Dictionary<string, string?> map) =>
        name => map.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void ExtractClientIp_PrefersForwardedForFirstHop()
    {
        var ip = IpMatcher.ExtractClientIp(
            Headers(new() { ["X-Forwarded-For"] = "1.2.3.4:5678, 5.6.7.8", ["X-Azure-ClientIP"] = "9.9.9.9" }),
            "::1");
        Assert.Equal("1.2.3.4", ip);
    }

    [Fact]
    public void ExtractClientIp_FallsThroughToAzureHeaders_WhenForwardedForAbsentOrGarbage()
    {
        // XFF present but unparseable → skip to next header (X-Azure-ClientIP).
        var ip = IpMatcher.ExtractClientIp(
            Headers(new() { ["X-Forwarded-For"] = "garbage", ["X-Azure-ClientIP"] = "203.0.113.7" }),
            "::1");
        Assert.Equal("203.0.113.7", ip);
    }

    [Fact]
    public void ExtractClientIp_UsesSocketIp_WhenOnlyThatIsPresent()
    {
        var ip = IpMatcher.ExtractClientIp(
            Headers(new() { ["X-Azure-SocketIP"] = "198.51.100.9" }), "127.0.0.1");
        Assert.Equal("198.51.100.9", ip);
    }

    [Fact]
    public void ExtractClientIp_FallsBackToRemoteIp_WhenNoHeaders()
    {
        Assert.Equal("192.0.2.1", IpMatcher.ExtractClientIp(_ => null, "192.0.2.1"));
    }

    [Fact]
    public void ExtractClientIp_ReturnsNull_WhenNothingResolvable()
    {
        Assert.Null(IpMatcher.ExtractClientIp(_ => null, null));
        Assert.Null(IpMatcher.ExtractClientIp(_ => "not-an-ip", "also-bad"));
    }
}
