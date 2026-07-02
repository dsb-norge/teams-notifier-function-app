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
}
