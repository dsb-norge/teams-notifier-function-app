namespace TeamsNotificationBot.Helpers;

/// <summary>Configuration reads for idempotency records.</summary>
public static class IdempotencyConfig
{
    public const int DefaultExpiryHours = 168; // 7 days

    /// <summary>
    /// How long an idempotency record counts. Sourced from <c>Idempotency__ExpiryHours</c>
    /// (default 7 days; clamped to 1 year so a typo can't overflow <see cref="TimeSpan"/>).
    /// </summary>
    public static TimeSpan Expiry => TimeSpan.FromHours(
        int.TryParse(Environment.GetEnvironmentVariable("Idempotency__ExpiryHours"), out var hours) && hours > 0
            ? Math.Min(hours, 8760)
            : DefaultExpiryHours);
}
