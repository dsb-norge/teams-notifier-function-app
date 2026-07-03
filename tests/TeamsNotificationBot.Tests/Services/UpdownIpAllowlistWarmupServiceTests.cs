using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class UpdownIpAllowlistWarmupServiceTests
{
    private static UpdownIpAllowlistWarmupService New(Mock<IUpdownIpAllowlistService> allowlist) =>
        new(allowlist.Object, NullLogger<UpdownIpAllowlistWarmupService>.Instance);

    [Fact]
    public async Task WarmUp_RefreshesAllowlist_WithStartupTag()
    {
        var allowlist = new Mock<IUpdownIpAllowlistService>();
        allowlist.Setup(s => s.GetOrRefreshAsync(It.IsAny<TimeSpan>(), "startup"))
            .ReturnsAsync(new UpdownIpAllowlistEntity { Cidrs = "1.2.3.4,5.6.7.8" });

        await New(allowlist).WarmUpAsync(CancellationToken.None);

        // F1: warm-up must call the staleness-gated refresh (not blind RefreshAsync) tagged "startup".
        allowlist.Verify(s => s.GetOrRefreshAsync(It.IsAny<TimeSpan>(), "startup"), Times.Once);
        allowlist.Verify(s => s.RefreshAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WarmUp_SwallowsExceptions_SoStartupNeverFails()
    {
        var allowlist = new Mock<IUpdownIpAllowlistService>();
        allowlist.Setup(s => s.GetOrRefreshAsync(It.IsAny<TimeSpan>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("dns/storage hiccup"));

        // Must not throw — best-effort warm-up.
        await New(allowlist).WarmUpAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ReturnsWithoutThrowing_AndStopIsNoop()
    {
        var allowlist = new Mock<IUpdownIpAllowlistService>();
        var svc = New(allowlist);

        await svc.StartAsync(CancellationToken.None); // detached; returns immediately
        await svc.StopAsync(CancellationToken.None);
    }
}
