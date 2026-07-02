using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Integration.Fixtures;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.Storage;

[Collection("Azurite")]
public class UpdownIpAllowlistServiceStorageTests
{
    private readonly AzuriteFixture _azurite;

    public UpdownIpAllowlistServiceStorageTests(AzuriteFixture azurite)
    {
        _azurite = azurite;
    }

    private UpdownIpAllowlistService NewService(string tableBase, IReadOnlyList<string> resolved, Action? onResolve = null)
        => new(_azurite.CreateTableClient(tableBase), (_, _) =>
        {
            onResolve?.Invoke();
            return Task.FromResult(resolved);
        });

    [Fact]
    public async Task Refresh_PersistsResolvedIps_AndReportsDiff()
    {
        var svc = NewService("updownipallowlist1", ["1.2.3.4", "10.0.0.0/8"]);

        var first = await svc.RefreshAsync("operator");
        Assert.Null(first.Error);
        Assert.Equal(2, first.Current.Count);
        Assert.Contains("1.2.3.4", first.Added);

        var stored = await svc.GetAsync();
        Assert.NotNull(stored);
        Assert.Contains("1.2.3.4", stored!.GetCidrs());
        Assert.Contains("10.0.0.0/8", stored.GetCidrs());
        Assert.Equal("operator", stored.RefreshedBy);
        Assert.NotNull(stored.RefreshedAt);
    }

    [Fact]
    public async Task Refresh_ComputesAddedAndRemoved()
    {
        var table = "updownipallowlist2";
        var svc1 = new UpdownIpAllowlistService(_azurite.CreateTableClient(table),
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["1.1.1.1", "2.2.2.2"]));
        await svc1.RefreshAsync("first");

        var svc2 = new UpdownIpAllowlistService(_azurite.CreateTableClient(table),
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["2.2.2.2", "3.3.3.3"]));
        var result = await svc2.RefreshAsync("second");

        Assert.Contains("3.3.3.3", result.Added);
        Assert.Contains("1.1.1.1", result.Removed);
        Assert.DoesNotContain("2.2.2.2", result.Added);
    }

    [Fact]
    public async Task Refresh_DnsFailure_KeepsPreviousList_AndRecordsError()
    {
        var table = "updownipallowlist3";
        var ok = new UpdownIpAllowlistService(_azurite.CreateTableClient(table),
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["1.2.3.4"]));
        await ok.RefreshAsync("seed");

        var failing = new UpdownIpAllowlistService(_azurite.CreateTableClient(table),
            (_, _) => throw new InvalidOperationException("dns down"));
        var result = await failing.RefreshAsync("attempt");

        Assert.Equal("dns down", result.Error);
        var stored = await failing.GetAsync();
        Assert.NotNull(stored);
        Assert.Contains("1.2.3.4", stored!.GetCidrs());   // previous entries preserved
        Assert.Equal("dns down", stored.ResolveError);
    }

    [Fact]
    public async Task GetOrRefresh_RefreshesWhenMissing_ThenServesCache()
    {
        var resolveCount = 0;
        var svc = NewService("updownipallowlist4", ["1.2.3.4"], () => resolveCount++);

        // Missing → refreshes once.
        var e1 = await svc.GetOrRefreshAsync(TimeSpan.FromHours(48), "lazy");
        Assert.NotNull(e1);
        Assert.Contains("1.2.3.4", e1!.GetCidrs());
        Assert.Equal(1, resolveCount);

        // Fresh → served from cache, no second resolve.
        var e2 = await svc.GetOrRefreshAsync(TimeSpan.FromHours(48), "lazy");
        Assert.NotNull(e2);
        Assert.Equal(1, resolveCount);
    }

    [Fact]
    public async Task GetOrRefresh_RefreshesWhenStale()
    {
        var resolveCount = 0;
        var svc = NewService("updownipallowlist5", ["1.2.3.4"], () => resolveCount++);

        await svc.GetOrRefreshAsync(TimeSpan.FromHours(48), "lazy");   // populate (resolve #1)
        Assert.Equal(1, resolveCount);

        // maxAge = zero → anything is "stale" → refresh again.
        await svc.GetOrRefreshAsync(TimeSpan.Zero, "lazy");
        Assert.Equal(2, resolveCount);
    }
}
