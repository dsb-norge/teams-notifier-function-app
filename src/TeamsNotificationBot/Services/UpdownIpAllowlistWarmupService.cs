using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Helpers;

namespace TeamsNotificationBot.Services;

/// <summary>
/// Populates the updown source-IP allowlist at worker startup so it is warm before the first
/// webhook — no bot command or deploy step required (refinements.md F1). Calls the staleness-gated
/// <see cref="IUpdownIpAllowlistService.GetOrRefreshAsync"/>, so on cold starts where the row is
/// already fresh it's just a table read; only a missing/stale row triggers a DNS resolve + write.
///
/// The warm-up is fire-and-forget and best-effort: it must not delay worker readiness or fail
/// startup, and the ingest handler still refreshes lazily, so a transient failure self-heals on the
/// first webhook. Under Flex per-function scaling this runs on every instance group's worker;
/// staleness-gating keeps that to one real resolve until the row goes stale.
/// </summary>
public class UpdownIpAllowlistWarmupService : IHostedService
{
    private readonly IUpdownIpAllowlistService _allowlist;
    private readonly ILogger<UpdownIpAllowlistWarmupService> _logger;

    public UpdownIpAllowlistWarmupService(
        IUpdownIpAllowlistService allowlist,
        ILogger<UpdownIpAllowlistWarmupService> logger)
    {
        _allowlist = allowlist;
        _logger = logger;
    }

    /// <summary>
    /// Refreshes the allowlist if stale/missing. Best-effort — swallows and logs any failure so a
    /// DNS/storage hiccup can never break startup. Awaitable so it is deterministically testable;
    /// <see cref="StartAsync"/> runs it detached.
    /// </summary>
    internal async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _allowlist.GetOrRefreshAsync(UpdownWebhookConfig.AllowlistMaxAge, "startup");
            _logger.LogInformation(
                "updown IP allowlist warm-up complete. Entries={Count}",
                result?.GetCidrs().Count ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "updown IP allowlist startup warm-up failed — will self-heal on first ingest.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Detached so DNS/table I/O never delays worker readiness or the first request.
        _ = Task.Run(() => WarmUpAsync(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
