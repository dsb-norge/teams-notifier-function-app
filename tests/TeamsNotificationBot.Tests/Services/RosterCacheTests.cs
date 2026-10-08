using System.Text.Json;
using Microsoft.Agents.Core.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class RosterCacheTests
{
    private const string ObjectId = "0b5f8a8e-1c1e-4f43-9a37-2f6b1b0c9d11";

    private static ChannelAccount Member(string id, string? objectId, string? upn)
    {
        var member = new ChannelAccount { Id = id, Name = id, AadObjectId = objectId };
        if (upn != null)
            member.Properties["userPrincipalName"] = JsonSerializer.SerializeToElement(upn);
        return member;
    }

    [Fact]
    public void Roster_FindsByObjectId_InAnyCaseOrFormat()
    {
        var roster = new Roster([Member("29:jane", ObjectId, "jane@example.com")]);

        Assert.Equal("29:jane", roster.Find(ObjectId)?.Id);
        Assert.Equal("29:jane", roster.Find(ObjectId.ToUpperInvariant())?.Id);
        Assert.Equal("29:jane", roster.Find("{" + ObjectId + "}")?.Id);
    }

    [Fact]
    public void Roster_FindsByObjectId_WhenTeamsSendsItAsObjectId()
    {
        // The whole-roster call, which group chats use, sends "objectId", not "aadObjectId".
        var member = new ChannelAccount { Id = "29:pal", Name = "Pal" };
        member.Properties["objectId"] = JsonSerializer.SerializeToElement(ObjectId);

        var roster = new Roster([member]);

        Assert.Equal("29:pal", roster.Find(ObjectId.ToUpperInvariant())?.Id);
        Assert.Equal(ObjectId, Roster.ObjectIdOf(member));
    }

    [Fact]
    public void Roster_FindsByUpn_IgnoringCase()
    {
        var roster = new Roster([Member("29:jane", ObjectId, "Jane.Doe@Example.com")]);

        Assert.Equal("29:jane", roster.Find("jane.doe@example.com")?.Id);
    }

    [Fact]
    public void Roster_DoesNotMatchOtherFields()
    {
        var roster = new Roster([Member("29:jane", ObjectId, "jane@example.com")]);

        Assert.Null(roster.Find("29:jane"));
        Assert.Null(roster.Find("someone@example.com"));
        Assert.Null(roster.Find("11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void Roster_SkipsMembersWithoutIdentifiers()
    {
        var roster = new Roster([Member("29:bot", null, null), null!]);

        Assert.Null(roster.Find(ObjectId));
    }

    [Fact]
    public async Task Cache_ReadsARosterOnce_WithinItsLifetime()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var cache = new RosterCache(time);
        var reads = 0;
        Task<IEnumerable<ChannelAccount>> Load()
        {
            reads++;
            return Task.FromResult<IEnumerable<ChannelAccount>>([Member("29:jane", ObjectId, null)]);
        }

        await cache.GetAsync("19:channel", Load);
        time.Advance(RosterCache.Lifetime - TimeSpan.FromSeconds(1));
        var roster = await cache.GetAsync("19:channel", Load);

        Assert.Equal(1, reads);
        Assert.NotNull(roster.Find(ObjectId));
    }

    [Fact]
    public async Task Cache_ReadsAgain_AfterItsLifetime()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var cache = new RosterCache(time);
        var reads = 0;
        Task<IEnumerable<ChannelAccount>> Load()
        {
            reads++;
            return Task.FromResult<IEnumerable<ChannelAccount>>([]);
        }

        await cache.GetAsync("19:channel", Load);
        time.Advance(RosterCache.Lifetime);
        await cache.GetAsync("19:channel", Load);

        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Cache_KeepsRostersPerConversation()
    {
        var cache = new RosterCache(new ManualTime(DateTimeOffset.UtcNow));

        await cache.GetAsync("19:a", () => Task.FromResult<IEnumerable<ChannelAccount>>([Member("29:a", ObjectId, null)]));
        var other = await cache.GetAsync("19:b", () => Task.FromResult<IEnumerable<ChannelAccount>>([]));

        Assert.Null(other.Find(ObjectId));
    }

    [Fact]
    public async Task Cache_DoesNotKeepAFailedRead()
    {
        var cache = new RosterCache(new ManualTime(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            cache.GetAsync("19:a", () => throw new HttpRequestException("boom")));
        var roster = await cache.GetAsync("19:a",
            () => Task.FromResult<IEnumerable<ChannelAccount>>([Member("29:a", ObjectId, null)]));

        Assert.NotNull(roster.Find(ObjectId));
    }

    [Fact]
    public async Task Cache_ConcurrentMisses_ShareOneRead()
    {
        var cache = new RosterCache(new ManualTime(DateTimeOffset.UtcNow));
        var release = new TaskCompletionSource<IEnumerable<ChannelAccount>>();
        var reads = 0;
        Task<IEnumerable<ChannelAccount>> Load()
        {
            Interlocked.Increment(ref reads);
            return release.Task;
        }

        var waiting = Enumerable.Range(0, 8).Select(_ => cache.GetAsync("19:channel", Load)).ToList();
        release.SetResult([Member("29:jane", ObjectId, null)]);
        var rosters = await Task.WhenAll(waiting);

        Assert.Equal(1, reads);
        Assert.All(rosters, r => Assert.NotNull(r.Find(ObjectId)));
    }

    [Fact]
    public async Task Cache_DropsExpiredRosters_WhenItReadsAnother()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var cache = new RosterCache(time);
        Task<IEnumerable<ChannelAccount>> Empty() => Task.FromResult<IEnumerable<ChannelAccount>>([]);

        await cache.GetAsync("19:a", Empty);
        await cache.GetAsync("19:b", Empty);
        time.Advance(RosterCache.Lifetime);
        await cache.GetAsync("19:c", Empty);

        Assert.Equal(1, cache.Count);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
