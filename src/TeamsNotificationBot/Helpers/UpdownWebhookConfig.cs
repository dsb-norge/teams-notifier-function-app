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
}
