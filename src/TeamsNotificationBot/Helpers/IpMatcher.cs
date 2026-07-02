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

        foreach (var raw in entries)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry))
                continue;

            if (entry.Contains('/'))
            {
                if (IPNetwork.TryParse(entry, out var network) && network.Contains(ip))
                    return true;
            }
            else if (IPAddress.TryParse(entry, out var single) && single.Equals(ip))
            {
                return true;
            }
        }

        return false;
    }
}
