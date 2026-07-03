using System.Net;

namespace TeamsNotificationBot.Helpers;

/// <summary>
/// Pure source-IP allowlist matching for the updown ingress (design §17). No ASP.NET or Azure
/// dependencies — trivially unit-testable. Supports single IPs and CIDR ranges, IPv4 and IPv6.
/// </summary>
public static class IpMatcher
{
    /// <summary>
    /// True if <paramref name="sourceIp"/> matches any entry in <paramref name="entries"/>.
    /// Entries may be single IPs ("1.2.3.4", "2001:db8::1") or CIDR ranges ("1.2.3.0/24", "2001:db8::/32").
    /// Returns false for a null/blank/unparseable source IP. An empty entry list yields false — the
    /// caller decides the fail-safe policy (see UpdownIngestFunction).
    /// </summary>
    public static bool IsAllowed(string? sourceIp, IEnumerable<string> entries)
    {
        if (string.IsNullOrWhiteSpace(sourceIp) || !IPAddress.TryParse(sourceIp.Trim(), out var ip))
            return false;

        return entries.Any(entry => Matches(entry, ip));
    }

    /// <summary>
    /// Client-IP headers to try, in priority order, before falling back to the connection's remote
    /// address. On Flex Consumption + .NET isolated worker the request is proxied to the worker over
    /// loopback, so <c>RemoteIpAddress</c> is <c>::1</c>/<c>127.0.0.1</c> and the real client IP must
    /// come from a forwarding header.
    ///
    /// <c>CLIENT-IP</c> is first because that is the header the Azure App Service / Functions ARR
    /// front end actually populates on Flex (confirmed empirically for this app, F8) — as
    /// <c>ip:port</c>, which <see cref="ParseClientIp"/> normalises. It is set by the platform (not
    /// forwarded from the caller), so it is also the more trustworthy source for the source-IP
    /// allowlist. <c>X-Forwarded-For</c> and the <c>X-Azure-*</c> variants are kept as fallbacks for
    /// portability to other hosting models (they were absent on Flex). Order matters — first that
    /// parses wins.
    /// </summary>
    public static readonly string[] ClientIpHeaders =
    [
        "CLIENT-IP",
        "X-Forwarded-For",
        "X-Azure-ClientIP",
        "X-Azure-SocketIP",
        "X-Client-IP",
        "X-Real-IP",
    ];

    /// <summary>
    /// Extracts the best client IP from forwarding headers (<see cref="ClientIpHeaders"/>, first hop),
    /// falling back to <paramref name="remoteIp"/>. Returns a bare, normalised IP (port stripped) or
    /// null if nothing parses. <paramref name="getHeader"/> returns the raw header value (comma-joined
    /// if multi-valued) or null when absent — kept as a delegate so the logic is host-agnostic and
    /// unit-testable without an <c>HttpRequest</c>.
    /// </summary>
    public static string? ExtractClientIp(Func<string, string?> getHeader, string? remoteIp)
    {
        foreach (var header in ClientIpHeaders)
        {
            var raw = getHeader(header);
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var firstHop = raw.Split(',')[0];
            if (ParseClientIp(firstHop) is { } ip)
                return ip;
        }

        return ParseClientIp(remoteIp);
    }

    /// <summary>
    /// Normalises a client-IP candidate to a bare IP string, or null if it doesn't parse.
    /// Handles a plain IP ("1.2.3.4", "2001:db8::1") and the "ip:port" / "[ipv6]:port" forms that
    /// Azure's <c>X-Forwarded-For</c> uses — stripping the port so allowlist matching works.
    /// </summary>
    public static string? ParseClientIp(string? candidate)
    {
        var value = candidate?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        if (IPAddress.TryParse(value, out var direct))
            return direct.ToString();

        if (IPEndPoint.TryParse(value, out var endpoint))
            return endpoint.Address.ToString();

        return null;
    }

    private static bool Matches(string? rawEntry, IPAddress ip)
    {
        var entry = rawEntry?.Trim();
        if (string.IsNullOrEmpty(entry))
            return false;

        return entry.Contains('/')
            ? IPNetwork.TryParse(entry, out var network) && network.Contains(ip)
            : IPAddress.TryParse(entry, out var single) && single.Equals(ip);
    }
}
