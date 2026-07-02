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
