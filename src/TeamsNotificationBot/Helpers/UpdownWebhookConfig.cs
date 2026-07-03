namespace TeamsNotificationBot.Helpers;

/// <summary>
/// Shared configuration reads for the updown webhook ingress, so the ingest handler and the
/// startup warm-up service agree on the same values.
/// </summary>
public static class UpdownWebhookConfig
{
    /// <summary>
    /// Max age before the cached source-IP allowlist is considered stale and refreshed. Sourced from
    /// <c>UpdownWebhook__IpAllowlistMaxAgeHours</c> (default 48h, clamped to 1 year to guard
    /// <see cref="TimeSpan.FromHours(double)"/> against a misconfigured overflow).
    /// </summary>
    public static TimeSpan AllowlistMaxAge => TimeSpan.FromHours(AllowlistMaxAgeHours);

    /// <summary>The raw hours value backing <see cref="AllowlistMaxAge"/>.</summary>
    public static int AllowlistMaxAgeHours =>
        int.TryParse(Environment.GetEnvironmentVariable("UpdownWebhook__IpAllowlistMaxAgeHours"), out var v) && v > 0
            ? Math.Min(v, 8760)
            : 48;

    /// <summary>
    /// Source-IP allowlist mode: <c>off</c> | <c>log-only</c> | <c>enforce</c>. Defaults to
    /// <b>enforce</b> — secure by default, so a deployment rejects non-updown source IPs unless
    /// someone deliberately loosens it (set <c>UpdownWebhook__IpFilterMode</c> to <c>off</c>/<c>log-only</c>,
    /// e.g. in local.settings.json for curl testing; a per-deployment override via `az` reverts on the
    /// next infra apply, back to this secure default). Safe to default on: the ingest handler's
    /// empty-list fail-safe still allows when the list hasn't populated, and the startup warm-up (F1)
    /// populates it. Requires the F8 client-IP fix to match real IPs (both ship together).
    /// </summary>
    public static string IpFilterMode
    {
        get
        {
            var mode = Environment.GetEnvironmentVariable("UpdownWebhook__IpFilterMode")?.Trim().ToLowerInvariant();
            return mode is "off" or "log-only" or "enforce" ? mode : "enforce";
        }
    }
}
