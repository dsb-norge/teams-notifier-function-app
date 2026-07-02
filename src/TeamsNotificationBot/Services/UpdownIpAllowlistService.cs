using System.Net;
using Azure;
using Azure.Data.Tables;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

/// <summary>
/// Maintains the cached updown source-IP allowlist (design §17). Resolves <c>ips.updown.io</c> via
/// DNS (A + AAAA) and persists the result in the <c>updownipallowlist</c> table. The DNS resolver is
/// injectable so the refresh logic is unit-testable without real network access.
/// </summary>
public class UpdownIpAllowlistService : IUpdownIpAllowlistService
{
    private const string Partition = "updown";
    private const string Row = "current";
    private const string DefaultHost = "ips.updown.io";

    // DNS resolve happens on the (synchronous) lazy-refresh path from the ingest handler, so bound it —
    // a stuck resolver must not hang the request. On timeout the catch below keeps the previous list.
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

    private readonly TableClient _tableClient;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>> _resolve;

    public UpdownIpAllowlistService(
        TableClient tableClient,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? resolver = null)
    {
        _tableClient = tableClient;
        _resolve = resolver ?? DefaultResolveAsync;
    }

    private static string Host =>
        Environment.GetEnvironmentVariable("UpdownWebhook__IpAllowlistHost") is { Length: > 0 } h ? h : DefaultHost;

    public async Task<UpdownIpAllowlistEntity?> GetAsync()
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<UpdownIpAllowlistEntity>(Partition, Row);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<UpdownIpAllowlistEntity?> GetOrRefreshAsync(TimeSpan maxAge, string refreshedBy)
    {
        var existing = await GetAsync();
        var stale = existing?.RefreshedAt is not { } at || (DateTimeOffset.UtcNow - at) > maxAge;
        if (!stale)
            return existing;

        await RefreshAsync(refreshedBy);
        return await GetAsync();
    }

    public async Task<AllowlistRefreshResult> RefreshAsync(string refreshedBy)
    {
        var existing = await GetAsync();
        var previous = existing?.GetCidrs() ?? [];

        IReadOnlyList<string> resolved;
        try
        {
            using var cts = new CancellationTokenSource(ResolveTimeout);
            resolved = await _resolve(Host, cts.Token);
        }
        catch (Exception ex)
        {
            // Keep the previous entries; record the error so show-ip-allow-list surfaces the degraded state.
            var errEntity = existing ?? new UpdownIpAllowlistEntity();
            errEntity.PartitionKey = Partition;
            errEntity.RowKey = Row;
            errEntity.Source = Host;
            errEntity.ResolveError = ex.Message;
            await _tableClient.UpsertEntityAsync(errEntity);
            return new AllowlistRefreshResult([], [], previous, ex.Message);
        }

        var current = resolved
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var added = current.Except(previous, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = previous.Except(current, StringComparer.OrdinalIgnoreCase).ToList();

        await _tableClient.UpsertEntityAsync(new UpdownIpAllowlistEntity
        {
            PartitionKey = Partition,
            RowKey = Row,
            Cidrs = string.Join(',', current),
            Source = Host,
            RefreshedAt = DateTimeOffset.UtcNow,
            RefreshedBy = refreshedBy,
            ResolveError = string.Empty
        });

        return new AllowlistRefreshResult(added, removed, current, null);
    }

    private static async Task<IReadOnlyList<string>> DefaultResolveAsync(string host, CancellationToken ct)
    {
        // DNS resolution only (no connection) — goes through the platform resolver / firewall DNS
        // proxy, so it needs no outbound egress allow-rule to updown. Returns both A and AAAA.
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        return addresses.Select(a => a.ToString()).ToList();
    }
}
