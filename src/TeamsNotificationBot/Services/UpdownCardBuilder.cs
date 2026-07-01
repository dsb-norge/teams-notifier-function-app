using System.Text.Json;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

/// <summary>
/// Builds a Teams Adaptive Card (JSON string) from a single updown.io webhook event.
///
/// Trust model: every updown string is untrusted. Cards carry NO actionable elements
/// (no Action.OpenUrl/Submit/Execute) and no external images — the same rules
/// <see cref="Models.AdaptiveCardValidator"/> enforces on user-supplied cards, so the
/// output is validator-clean. The only clickable link is the downtime details URL, and
/// only when it is under https://updown.io/ (domain-gated); the monitored site's own URL
/// is never auto-linked. Missing/null fields are omitted rather than rendered as "null".
/// </summary>
public static class UpdownCardBuilder
{
    public static string Build(UpdownEvent e, string? updownAccountLabel = null)
    {
        var (emoji, color, label) = StyleFor(e.Event);
        var subject = e.Check?.Alias ?? e.Check?.Url ?? "check";

        var body = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = $"{emoji} updown.io: {label} — {subject}",
                weight = "Bolder",
                size = "Medium",
                wrap = true,
                color
            }
        };

        var facts = new List<object>();
        void Fact(string title, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                facts.Add(new { title, value });
        }

        Fact("Check", e.Check?.Url);
        Fact("updown alias", e.Check?.Alias);
        Fact("updown account", updownAccountLabel);

        switch (e.Event)
        {
            case "check.down":
                Fact("Reason", e.Downtime?.Error ?? e.Check?.Error ?? e.Description);
                Fact("Down since", e.Downtime?.StartedAt ?? e.Check?.DownSince);
                break;

            case "check.up":
                Fact("Downtime", HumanizeDuration(e.Downtime?.Duration));
                Fact("Recovered at", e.Downtime?.EndedAt ?? e.Check?.UpSince);
                break;

            case "check.ssl_invalid":
                Fact("SSL error", e.Ssl?.Error ?? e.Description);
                Fact("Issuer", e.Ssl?.Cert?.Issuer);
                Fact("Subject", e.Ssl?.Cert?.Subject);
                break;

            case "check.ssl_valid":
                Fact("Valid until", e.Ssl?.Cert?.To);
                break;

            case "check.ssl_expiration":
                if (e.Ssl?.DaysBeforeExpiration is int days)
                    Fact("Days to expiry", days.ToString());
                Fact("Expires", e.Ssl?.Cert?.To);
                break;

            case "check.ssl_renewed":
                Fact("New cert valid until", e.Ssl?.NewCert?.To);
                Fact("Previous cert expired", e.Ssl?.OldCert?.To);
                break;

            case "check.performance_drop":
                Fact("Apdex dropped", e.ApdexDropped);
                break;
        }

        Fact("Time", e.Time);

        body.Add(new { type = "FactSet", facts });

        // Downtime link — only if it points at updown.io (domain-gated). Never link check.url.
        var detailsUrl = e.Downtime?.DetailsUrl;
        if (IsUpdownUrl(detailsUrl))
        {
            body.Add(new
            {
                type = "TextBlock",
                text = $"[View downtime details on updown.io]({detailsUrl})",
                wrap = true,
                spacing = "Small"
            });
        }

        body.Add(new
        {
            type = "TextBlock",
            text = "source: updown.io (unverified sender)",
            wrap = true,
            isSubtle = true,
            size = "Small",
            spacing = "Small"
        });

        var card = new
        {
            type = "AdaptiveCard",
            version = "1.4",
            body
        };

        return JsonSerializer.Serialize(card);
    }

    private static (string emoji, string color, string label) StyleFor(string? eventType) => eventType switch
    {
        "check.down" => ("🔴", "Attention", "DOWN"),
        "check.up" => ("🟢", "Good", "UP"),
        "check.ssl_invalid" => ("🔒", "Attention", "SSL invalid"),
        "check.ssl_valid" => ("🔒", "Good", "SSL valid"),
        "check.ssl_expiration" => ("⚠️", "Warning", "SSL expiring"),
        "check.ssl_renewed" => ("🔒", "Good", "SSL renewed"),
        "check.performance_drop" => ("📉", "Warning", "Performance drop"),
        _ => ("ℹ️", "Default", eventType ?? "event")
    };

    /// <summary>Only https://updown.io/ URLs are treated as safe to linkify.</summary>
    internal static bool IsUpdownUrl(string? url) =>
        !string.IsNullOrEmpty(url) &&
        url.StartsWith("https://updown.io/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns a downtime length in seconds into a short human string.</summary>
    internal static string? HumanizeDuration(long? seconds)
    {
        if (seconds is null or < 0) return null;
        var s = seconds.Value;
        if (s < 60) return $"{s} second{(s == 1 ? "" : "s")}";

        var minutes = s / 60;
        if (minutes < 60) return $"{minutes} minute{(minutes == 1 ? "" : "s")}";

        var hours = minutes / 60;
        var remMinutes = minutes % 60;
        if (hours < 24)
            return remMinutes == 0
                ? $"{hours} hour{(hours == 1 ? "" : "s")}"
                : $"{hours}h {remMinutes}m";

        var days = hours / 24;
        var remHours = hours % 24;
        return remHours == 0
            ? $"{days} day{(days == 1 ? "" : "s")}"
            : $"{days}d {remHours}h";
    }
}
