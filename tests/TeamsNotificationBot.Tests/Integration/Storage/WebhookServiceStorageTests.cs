using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Integration.Fixtures;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.Storage;

[Collection("Azurite")]
public class WebhookServiceStorageTests
{
    private readonly WebhookService _service;

    public WebhookServiceStorageTests(AzuriteFixture azurite)
    {
        _service = new WebhookService(azurite.CreateTableClient("webhooktokens"));
    }

    private Task<WebhookCreateResult> CreateChannelAsync() =>
        _service.CreateAsync("updown", "channel", "team-1", "channel-1", null, null,
            "test description", "test@dsb.no", "oid-1", "Tester");

    [Fact]
    public async Task Create_Then_ResolveByToken_RoundTrips()
    {
        var created = await CreateChannelAsync();

        var resolved = await _service.ResolveByTokenAsync(created.Token);

        Assert.NotNull(resolved);
        Assert.Equal(created.Id, resolved.Id);
        Assert.Equal("channel", resolved.TargetType);
        Assert.Equal("team-1", resolved.TeamId);
        Assert.Equal("channel-1", resolved.ChannelId);
        // Default filter: all except performance_drop
        Assert.True(resolved.IsEventEnabled("check.down"));
        Assert.False(resolved.IsEventEnabled("check.performance_drop"));
    }

    [Fact]
    public async Task Token_IsNeverStoredInPlaintext()
    {
        var created = await CreateChannelAsync();

        // RowKey is the hash, not the token; the token appears in no stored field.
        Assert.Equal(WebhookService.Sha256Hex(created.Token), created.Entity.RowKey);
        Assert.NotEqual(created.Token, created.Entity.RowKey);

        var resolved = await _service.ResolveByTokenAsync(created.Token);
        Assert.NotNull(resolved);
        Assert.DoesNotContain(created.Token, new[]
        {
            resolved.RowKey, resolved.Id, resolved.Description, resolved.UpdownAccount,
            resolved.EnabledEvents, resolved.TeamId, resolved.ChannelId
        });
    }

    [Fact]
    public async Task ResolveByToken_WrongToken_ReturnsNull()
    {
        await CreateChannelAsync();
        Assert.Null(await _service.ResolveByTokenAsync("not-a-real-token"));
    }

    [Fact]
    public async Task GetById_And_List_FindTheWebhook()
    {
        var created = await CreateChannelAsync();

        var byId = await _service.GetByIdAsync(created.Id);
        Assert.NotNull(byId);
        Assert.Equal(created.Id, byId.Id);

        var list = await _service.ListAsync();
        Assert.Contains(list, w => w.Id == created.Id);
    }

    [Fact]
    public async Task Configure_UpdatesFields()
    {
        var created = await CreateChannelAsync();

        var ok = await _service.ConfigureAsync(created.Id, "prod site", "prod-acct / ops@dsb.no",
            new[] { "check.ssl_expiration", "check.ssl_renewed" });
        Assert.True(ok);

        var updated = await _service.GetByIdAsync(created.Id);
        Assert.NotNull(updated);
        Assert.Equal("prod site", updated.Description);
        Assert.Equal("prod-acct / ops@dsb.no", updated.UpdownAccount);
        Assert.True(updated.IsEventEnabled("check.ssl_expiration"));
        Assert.False(updated.IsEventEnabled("check.down"));
    }

    [Fact]
    public async Task Configure_UnknownId_ReturnsFalse()
    {
        Assert.False(await _service.ConfigureAsync("00000000", "x", null, null));
    }

    [Fact]
    public async Task Rotate_InvalidatesOldToken_IssuesNew()
    {
        var created = await CreateChannelAsync();

        var newToken = await _service.RotateByIdAsync(created.Id);

        Assert.NotNull(newToken);
        Assert.NotEqual(created.Token, newToken);
        // Old token no longer resolves; new token does; id is preserved.
        Assert.Null(await _service.ResolveByTokenAsync(created.Token));
        var viaNew = await _service.ResolveByTokenAsync(newToken!);
        Assert.NotNull(viaNew);
        Assert.Equal(created.Id, viaNew.Id);
    }

    [Fact]
    public async Task Rotate_UnknownId_ReturnsNull()
    {
        Assert.Null(await _service.RotateByIdAsync("deadbeef"));
    }

    [Fact]
    public async Task Remove_DeletesWebhook()
    {
        var created = await CreateChannelAsync();

        Assert.True(await _service.RemoveByIdAsync(created.Id));
        Assert.Null(await _service.ResolveByTokenAsync(created.Token));
        Assert.Null(await _service.GetByIdAsync(created.Id));
        Assert.False(await _service.RemoveByIdAsync(created.Id)); // already gone
    }

    [Fact]
    public async Task TouchLastReceived_SetsTimestamp()
    {
        var created = await CreateChannelAsync();
        var entity = await _service.ResolveByTokenAsync(created.Token);
        Assert.NotNull(entity);
        Assert.Null(entity.LastReceivedAt);

        await _service.TouchLastReceivedAsync(entity);

        var after = await _service.GetByIdAsync(created.Id);
        Assert.NotNull(after?.LastReceivedAt);
    }
}
