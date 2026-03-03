using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Integration.Fixtures;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.Services;

[Collection("Azurite")]
public class AliasServiceIntegrationTests
{
    private readonly AliasService _service;

    public AliasServiceIntegrationTests(AzuriteFixture azurite)
    {
        var tableClient = azurite.CreateTableClient("aliases");
        _service = new AliasService(tableClient);
    }

    [Fact]
    public async Task SetAndGet_RoundTrips_AllProperties()
    {
        var entity = new AliasEntity
        {
            TargetType = "channel",
            TeamId = "team-123",
            ChannelId = "channel-456",
            Description = "Integration test alias",
            CreatedBy = "oid-789",
            CreatedByName = "Test User",
            CreatedAt = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero)
        };

        await _service.SetAliasAsync("roundtrip-test", entity);
        var result = await _service.GetAliasAsync("roundtrip-test");

        Assert.NotNull(result);
        Assert.Equal("alias", result.PartitionKey);
        Assert.Equal("roundtrip-test", result.RowKey);
        Assert.Equal("channel", result.TargetType);
        Assert.Equal("team-123", result.TeamId);
        Assert.Equal("channel-456", result.ChannelId);
        Assert.Equal("Integration test alias", result.Description);
        Assert.Equal("oid-789", result.CreatedBy);
        Assert.Equal("Test User", result.CreatedByName);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero), result.CreatedAt);
    }

    [Fact]
    public async Task GetAlias_CaseInsensitive()
    {
        var entity = new AliasEntity
        {
            TargetType = "channel",
            TeamId = "team-1",
            ChannelId = "ch-1",
            Description = "Case test",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _service.SetAliasAsync("MyAlias", entity);
        var result = await _service.GetAliasAsync("MYALIAS");

        Assert.NotNull(result);
        Assert.Equal("myalias", result.RowKey);
        Assert.Equal("Case test", result.Description);
    }

    [Fact]
    public async Task GetAlias_NotFound_ReturnsNull()
    {
        var result = await _service.GetAliasAsync("nonexistent-alias");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllAliases_ReturnsAll()
    {
        await _service.SetAliasAsync("all-a", new AliasEntity { TargetType = "channel", TeamId = "t1", ChannelId = "c1", Description = "A", CreatedAt = DateTimeOffset.UtcNow });
        await _service.SetAliasAsync("all-b", new AliasEntity { TargetType = "personal", UserId = "u1", Description = "B", CreatedAt = DateTimeOffset.UtcNow });
        await _service.SetAliasAsync("all-c", new AliasEntity { TargetType = "groupChat", ChatId = "g1", Description = "C", CreatedAt = DateTimeOffset.UtcNow });

        var result = await _service.GetAllAliasesAsync();

        Assert.True(result.Count >= 3);
        Assert.Contains(result, e => e.RowKey == "all-a");
        Assert.Contains(result, e => e.RowKey == "all-b");
        Assert.Contains(result, e => e.RowKey == "all-c");
    }

    [Fact]
    public async Task SetAlias_Upsert_Overwrites()
    {
        await _service.SetAliasAsync("upsert-test", new AliasEntity
        {
            TargetType = "channel",
            TeamId = "team-old",
            ChannelId = "ch-old",
            Description = "Original",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _service.SetAliasAsync("upsert-test", new AliasEntity
        {
            TargetType = "channel",
            TeamId = "team-new",
            ChannelId = "ch-new",
            Description = "Updated",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var result = await _service.GetAliasAsync("upsert-test");

        Assert.NotNull(result);
        Assert.Equal("team-new", result.TeamId);
        Assert.Equal("ch-new", result.ChannelId);
        Assert.Equal("Updated", result.Description);
    }

    [Fact]
    public async Task RemoveAlias_ThenNotFound()
    {
        await _service.SetAliasAsync("remove-test", new AliasEntity
        {
            TargetType = "channel",
            TeamId = "t",
            ChannelId = "c",
            Description = "To be removed",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var removed = await _service.RemoveAliasAsync("remove-test");
        Assert.True(removed);

        var removedAgain = await _service.RemoveAliasAsync("remove-test");
        Assert.False(removedAgain);

        var result = await _service.GetAliasAsync("remove-test");
        Assert.Null(result);
    }
}
