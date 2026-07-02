using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public interface IUpdownIpAllowlistService
{
    /// <summary>Reads the cached allowlist row, or null if it has never been populated.</summary>
    Task<UpdownIpAllowlistEntity?> GetAsync();

    /// <summary>
    /// Resolves the configured host (default <c>ips.updown.io</c>) and upserts the allowlist row.
    /// On DNS failure, keeps the previous entries and records the error. Returns the diff.
    /// </summary>
    Task<AllowlistRefreshResult> RefreshAsync(string refreshedBy);

    /// <summary>
    /// Returns the cached row, refreshing first if it is missing or older than <paramref name="maxAge"/>.
    /// Used by the ingest handler for lazy-when-stale refresh (design §17.3).
    /// </summary>
    Task<UpdownIpAllowlistEntity?> GetOrRefreshAsync(TimeSpan maxAge, string refreshedBy);
}

public record AllowlistRefreshResult(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Current,
    string? Error);
