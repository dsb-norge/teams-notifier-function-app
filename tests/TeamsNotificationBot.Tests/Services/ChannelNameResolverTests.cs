using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class ChannelNameResolverTests
{
    [Fact]
    public void Resolve_NonEmptyName_PassesThroughUnchanged()
    {
        var result = ChannelNameResolver.Resolve("utvikling - testkanal", "19:abc@thread.tacv2", "19:team@thread.tacv2");

        Assert.Equal("utvikling - testkanal", result);
    }

    [Fact]
    public void Resolve_NonEmptyName_WinsEvenWhenChannelIsTheTeamThread()
    {
        // A named General (some tenants do return it) must not be relabelled.
        var result = ChannelNameResolver.Resolve("Generelt", "19:team@thread.tacv2", "19:team@thread.tacv2");

        Assert.Equal("Generelt", result);
    }

    [Fact]
    public void Resolve_NullName_ChannelIdEqualsTeamThreadId_ReturnsGeneral()
    {
        var result = ChannelNameResolver.Resolve(null, "19:team@thread.tacv2", "19:team@thread.tacv2");

        Assert.Equal("General", result);
    }

    [Fact]
    public void Resolve_EmptyName_ChannelIdEqualsTeamThreadId_ReturnsGeneral()
    {
        var result = ChannelNameResolver.Resolve("", "19:team@thread.tacv2", "19:team@thread.tacv2");

        Assert.Equal("General", result);
    }

    [Fact]
    public void Resolve_NullName_DifferentIds_ReturnsNull()
    {
        var result = ChannelNameResolver.Resolve(null, "19:abc@thread.tacv2", "19:team@thread.tacv2");

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_NullName_BothIdsNull_ReturnsNull()
    {
        // Guards against a null==null match producing a bogus "General".
        var result = ChannelNameResolver.Resolve(null, null, null);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_NullName_NullChannelId_ReturnsNull()
    {
        var result = ChannelNameResolver.Resolve(null, null, "19:team@thread.tacv2");

        Assert.Null(result);
    }
}
