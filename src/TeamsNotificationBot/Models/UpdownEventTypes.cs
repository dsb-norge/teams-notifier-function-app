namespace TeamsNotificationBot.Models;

/// <summary>
/// The updown.io webhook event types this app recognises, and the default enabled set.
/// updown may add new types at any time — unknown types are logged and skipped, never rejected.
/// </summary>
public static class UpdownEventTypes
{
    public const string Down = "check.down";
    public const string Up = "check.up";
    public const string SslInvalid = "check.ssl_invalid";
    public const string SslValid = "check.ssl_valid";
    public const string SslExpiration = "check.ssl_expiration";
    public const string SslRenewed = "check.ssl_renewed";
    public const string PerformanceDrop = "check.performance_drop";

    public static readonly IReadOnlyList<string> All =
    [
        Down, Up, SslInvalid, SslValid, SslExpiration, SslRenewed, PerformanceDrop
    ];

    /// <summary>Default filter for a new webhook: everything except performance_drop.</summary>
    public static readonly IReadOnlyList<string> DefaultEnabled =
        All.Where(e => e != PerformanceDrop).ToList();

    public static bool IsKnown(string? eventType) =>
        eventType != null && All.Contains(eventType);
}
