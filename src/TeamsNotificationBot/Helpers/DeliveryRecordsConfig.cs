namespace TeamsNotificationBot.Helpers;

/// <summary>Configuration reads for delivery records.</summary>
public static class DeliveryRecordsConfig
{
    public const int DefaultRetentionDays = 180;

    /// <summary>
    /// How long a delivery record is kept. A reply or update to an older message can no longer find
    /// it, so this must outlast the longest-open incident a caller tracks. Sourced from
    /// <c>DeliveryRecords__RetentionDays</c> (default 180; clamped to 10 years).
    /// </summary>
    public static TimeSpan Retention => TimeSpan.FromDays(
        int.TryParse(Environment.GetEnvironmentVariable("DeliveryRecords__RetentionDays"), out var days) && days > 0
            ? Math.Min(days, 3650)
            : DefaultRetentionDays);
}
