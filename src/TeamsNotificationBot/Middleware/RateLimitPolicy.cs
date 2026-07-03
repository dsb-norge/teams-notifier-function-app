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

    /// <summary>
    /// Source-IP identity for the ingest zone. Resolves the client IP from forwarding headers
    /// (<see cref="Helpers.IpMatcher.ClientIpHeaders"/> — X-Forwarded-For, then the App Service
    /// X-Azure-* headers), falling back to <paramref name="remoteIp"/>. Ports are stripped so the
    /// same client shares one key; without this every request keys distinctly and the per-source-IP
    /// limit never triggers. See refinements.md (F8): on Flex + isolated worker the connection is
    /// loopback, so a forwarding header is the only real source.
    /// </summary>
    public static string SourceIpKey(Func<string, string?> getHeader, string? remoteIp)
    {
        var ip = Helpers.IpMatcher.ExtractClientIp(getHeader, remoteIp);
        return $"ingest-ip:{ip ?? "unknown"}";
    }

    public static int ApiPermitLimit() => EnvInt("RateLimit__PermitLimit", DefaultApiPermitLimit);
    public static int ApiIntervalSeconds() => EnvInt("RateLimit__IntervalInSeconds", DefaultApiIntervalSeconds);
    public static int IngestPermitLimit() => EnvInt("RateLimit__Ingest__PermitLimit", DefaultIngestPermitLimit);
    public static int IngestIntervalSeconds() => EnvInt("RateLimit__Ingest__IntervalInSeconds", DefaultIngestIntervalSeconds);

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
}
