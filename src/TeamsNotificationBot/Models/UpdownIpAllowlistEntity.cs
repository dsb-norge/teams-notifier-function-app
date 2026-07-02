using Azure;
using Azure.Data.Tables;

namespace TeamsNotificationBot.Models;

/// <summary>
/// Cached updown source-IP allowlist (design §17). A single row holds the set of IPs/CIDRs resolved
/// from <c>ips.updown.io</c>, persisted so it survives cold starts and is shared across instances
/// without each one resolving. Refreshed lazily-when-stale by the ingest handler and on demand via
/// the <c>update-ip-allow-list</c> bot command.
/// </summary>
public class UpdownIpAllowlistEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "updown";
    public string RowKey { get; set; } = "current";

    /// <summary>Comma-joined resolved IPs/CIDRs (IPv4 + IPv6).</summary>
    public string Cidrs { get; set; } = string.Empty;

    public string Source { get; set; } = "ips.updown.io";

    public DateTimeOffset? RefreshedAt { get; set; }

    /// <summary>"lazy" (ingest-triggered) or an operator name (from update-ip-allow-list).</summary>
    public string RefreshedBy { get; set; } = string.Empty;

    /// <summary>Last DNS resolve error; empty on success. Surfaced by show-ip-allow-list.</summary>
    public string ResolveError { get; set; } = string.Empty;

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public IReadOnlyList<string> GetCidrs() =>
        string.IsNullOrWhiteSpace(Cidrs)
            ? []
            : Cidrs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
