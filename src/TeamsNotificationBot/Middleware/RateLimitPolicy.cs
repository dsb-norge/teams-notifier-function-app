namespace TeamsNotificationBot.Middleware;

/// <summary>
/// Rate-limit routing + identity keys for ThrottlingTroll, extracted as pure helpers so the
/// behaviour is unit-testable without spinning up the host.
///
/// Two disjoint zones:
///  - AAD routes (<see cref="ApiUriPattern"/>): keyed by EasyAuth principal, EXCLUDING the anonymous
///    ingest sub-path (negative lookahead) so an anonymous ingest call — which has no principal —
///    is never bucketed under this rule.
///  - updown ingest (<see cref="IngestUriPattern"/>): keyed by source IP, since it is now a public
///    anonymous endpoint (see docs/feat-updown-io-webhook/design.md §10).
/// </summary>
public static class RateLimitPolicy
{
    public const string ApiUriPattern = "/api/v1/(?!ingest/).*";
    public const string IngestUriPattern = "/api/v1/ingest/.*";

    public const int DefaultApiPermitLimit = 60;
    public const int DefaultApiIntervalSeconds = 60;
    public const int DefaultIngestPermitLimit = 100;
    public const int DefaultIngestIntervalSeconds = 60;

    /// <summary>Principal-keyed identity; null means "no principal" so ThrottlingTroll skips the rule.</summary>
    public static string? PrincipalKey(string? principalId) =>
        string.IsNullOrEmpty(principalId) ? null : principalId;

    /// <summary>Source-IP identity for the ingest zone (first X-Forwarded-For hop, else remote IP).</summary>
    public static string SourceIpKey(string? xForwardedFor, string? remoteIp)
    {
        var ip = xForwardedFor?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(ip))
            ip = remoteIp;
        return $"ingest-ip:{(string.IsNullOrEmpty(ip) ? "unknown" : ip)}";
    }

    public static int ApiPermitLimit() => EnvInt("RateLimit__PermitLimit", DefaultApiPermitLimit);
    public static int ApiIntervalSeconds() => EnvInt("RateLimit__IntervalInSeconds", DefaultApiIntervalSeconds);
    public static int IngestPermitLimit() => EnvInt("RateLimit__Ingest__PermitLimit", DefaultIngestPermitLimit);
    public static int IngestIntervalSeconds() => EnvInt("RateLimit__Ingest__IntervalInSeconds", DefaultIngestIntervalSeconds);

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
}
