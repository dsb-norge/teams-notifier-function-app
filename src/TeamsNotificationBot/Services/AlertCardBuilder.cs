using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

/// <summary>
/// Builds the Teams card for an Azure Monitor alert in the common alert schema: the
/// essentials, the alert rule's custom properties, and the alert context behind a
/// "Show details" button. The card knows the schema, not what any alert is about: the
/// custom properties and context are flattened by <see cref="AlertFacts"/>, whatever
/// their shape.
/// </summary>
public static partial class AlertCardBuilder
{
    /// <summary>
    /// The card's size as it sits in the queue message, whose envelope escapes every quote
    /// (as <c>"</c>) and which is Base64-encoded and at most 64 KB. 30 KB leaves room for
    /// the envelope, and is far below Teams' message limit (100 KB, counted in UTF-16).
    /// </summary>
    internal const int MaxQueuedCardBytes = 30 * 1024;
    internal const int MaxCustomFacts = 20;
    internal const int MaxContextFacts = 40;
    internal const int MaxValueLength = 500;
    internal const int MaxTitleLength = 100;
    internal const int MaxDescriptionLength = 2000;

    /// <summary>
    /// How far the essentials are cut when they alone would outgrow the budget, which only a
    /// payload built to be odd does (real rule names are short): then the card has room for
    /// nothing else, and these lengths keep even that card well under the budget.
    /// </summary>
    internal const int MinValueLength = 100;
    internal const int MinDescriptionLength = 500;

    /// <summary>
    /// Alert context fields left off the card wherever they appear, because of privacy, size,
    /// or because they repeat what the card already says. Never to hide what one alert type
    /// supposedly doesn't need.
    /// </summary>
    private static readonly HashSet<string> HiddenContext = new(StringComparer.OrdinalIgnoreCase)
    {
        "claims",                    // the caller's token claims: names, IP address, object IDs
        "httpRequest",               // the caller's IP address
        "authorization",             // action and scope, which operationName and the target repeat
        "SearchResults",             // a legacy log alert's whole result table
        "impactedServicesTableRows", // Service Health's HTML rendering of impactedServices
        "defaultLanguageTitle",      // Service Health's title, again
        "defaultLanguageContent",    // Service Health's communication, again
    };

    private static bool IsHiddenContext(string name) =>
        HiddenContext.Contains(name) ||
        // linkToSearchResultsAPI and the like: Log Analytics API URLs a reader can't open.
        (name.StartsWith("linkTo", StringComparison.OrdinalIgnoreCase) &&
         name.EndsWith("API", StringComparison.OrdinalIgnoreCase));

    /// <param name="logger">Where to say that the details couldn't be rendered.</param>
    /// <param name="messageId">The alert's message ID, for that log line.</param>
    public static string Build(CommonAlertPayload alert, ILogger? logger = null, string? messageId = null)
    {
        var essentials = alert.Data?.Essentials ?? new AlertEssentials();

        try
        {
            return BuildWithDetails(essentials, alert.Data);
        }
        catch (Exception ex) // The details are a side concern: a bug in them must not cost the alert, and Azure retries no 500.
        {
            logger?.LogWarning(ex,
                "Alert details could not be rendered; the card carries the essentials only. MessageId={MessageId}",
                messageId);
            return Fit(essentials, [], [], 0) ?? CutHarder(essentials, 0);
        }
    }

    /// <param name="valueLength">How long a fact, the title's name and the target may be.</param>
    /// <param name="descriptionLength">How long the description may be.</param>
    private static List<object> BuildEssentials(AlertEssentials essentials, int valueLength, int descriptionLength)
    {
        var severity = essentials.Severity;
        var resolved = string.Equals(essentials.MonitorCondition, "Resolved", StringComparison.OrdinalIgnoreCase);
        var color = GetTitleColor(severity, resolved);
        var conditionEmoji = resolved ? "\u2705" : "\ud83d\udea8";

        // Azure leaves fields out or sends them as null (a budget alert has no rule, severity
        // or signal type); a fact without a value is noise, so it is left out.
        var facts = new List<object>();
        void Fact(string title, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                facts.Add(new { title, value = Truncate(value, valueLength) });
        }

        Fact("Alert Rule", essentials.AlertRule);
        Fact("Severity", severity);
        Fact("Condition", essentials.MonitorCondition);
        Fact("Signal Type", essentials.SignalType);
        Fact("Fired", essentials.FiredDateTime);
        Fact("Monitoring Service", essentials.MonitoringService);

        // Without a rule name, the monitoring service ("CostAlerts") is the best name there is.
        var name = new[] { essentials.AlertRule, essentials.MonitoringService }
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) is { } found ? Truncate(found, valueLength) : null;

        var bodyItems = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = name is null
                    ? $"{conditionEmoji} Azure Monitor Alert"
                    : $"{conditionEmoji} Azure Monitor Alert: {name}",
                weight = "Bolder",
                size = "Medium",
                wrap = true,
                color
            }
        };

        if (facts.Count > 0)
            bodyItems.Add(new { type = "FactSet", facts });

        if (!string.IsNullOrWhiteSpace(essentials.Description))
        {
            bodyItems.Add(new
            {
                type = "TextBlock",
                text = Truncate(essentials.Description, descriptionLength),
                wrap = true
            });
        }

        var targetResource = essentials.AlertTargetIDs?.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (!string.IsNullOrEmpty(targetResource))
        {
            // Extract just the resource name from the full resource ID
            var resourceName = Truncate(targetResource.TrimEnd('/').Split('/').Last(), valueLength);
            bodyItems.Add(new
            {
                type = "TextBlock",
                text = $"**Target:** {resourceName}",
                wrap = true,
                isSubtle = true
            });
        }

        if (AlertPageUrl(essentials.AlertId) is { } alertPage)
            bodyItems.Add(new { type = "TextBlock", text = $"[Open in Azure portal]({alertPage})", wrap = true });

        return bodyItems;
    }

    /// <summary>
    /// The alert's page in the Azure portal, for an alert that Azure Monitor's alerts
    /// management keeps (metric, log search, activity log and Prometheus alerts). The ID of
    /// anything else (a budget alert's, an action group test's) has no such page. Both IDs in
    /// it are GUIDs, which also keeps the link short: the card can't trim the essentials.
    /// </summary>
    internal static string? AlertPageUrl(string? alertId) =>
        alertId is not null && AlertsManagementAlertId().IsMatch(alertId)
            ? "https://portal.azure.com/#view/Microsoft_Azure_Monitoring_Alerts/AlertDetails.ReactView/alertId~/" +
              Uri.EscapeDataString(alertId)
            : null;

    [GeneratedRegex(@"^/subscriptions/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/providers/Microsoft\.AlertsManagement/alerts/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.IgnoreCase)]
    private static partial Regex AlertsManagementAlertId();

    /// <summary>
    /// A payload value that is a URL on the Azure portal, as a Markdown link target; null for
    /// anything else. Only the portal is linked (a log alert's search results, say), the way the
    /// updown card links updown.io only: any other URL stays text.
    /// </summary>
    internal static string? PortalLinkTarget(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
            !string.Equals(uri.IdnHost, "portal.azure.com", StringComparison.OrdinalIgnoreCase))
            return null;

        // Parentheses would end the Markdown link target early.
        return uri.AbsoluteUri.Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);
    }

    private static string BuildWithDetails(AlertEssentials essentials, AlertData? data)
    {
        var omitted = 0;
        var custom = Capped(AlertFacts.Flatten(data?.CustomProperties), MaxCustomFacts, ref omitted);
        var context = Capped(AlertFacts.Flatten(data?.AlertContext, IsHiddenContext), MaxContextFacts, ref omitted);

        while (true)
        {
            if (Fit(essentials, custom, context, omitted) is { } json)
                return json;
            if (custom.Count == 0 && context.Count == 0)
                return CutHarder(essentials, omitted);

            // Over budget: drop facts from the end, the context's first, since the rule's
            // author put the custom properties there on purpose.
            var from = context.Count > 0 ? context : custom;
            from.RemoveAt(from.Count - 1);
            omitted++;
        }
    }

    /// <summary>The card, if it is within the budget with the essentials cut as usual.</summary>
    private static string? Fit(AlertEssentials essentials, List<AlertFacts.Fact> custom, List<AlertFacts.Fact> context, int omitted)
    {
        var json = Serialize(Compose(BuildEssentials(essentials, MaxValueLength, MaxDescriptionLength), custom, context, omitted));
        return QueuedSize(json) <= MaxQueuedCardBytes ? json : null;
    }

    /// <summary>The essentials alone outgrow the budget: the card has them, cut harder, and nothing else.</summary>
    private static string CutHarder(AlertEssentials essentials, int omitted) =>
        Serialize(Compose(BuildEssentials(essentials, MinValueLength, MinDescriptionLength), [], [], omitted));

    private static List<AlertFacts.Fact> Capped(List<AlertFacts.Fact> facts, int max, ref int omitted)
    {
        if (facts.Count <= max)
            return facts;
        omitted += facts.Count - max;
        return facts.GetRange(0, max);
    }

    private static List<object> Compose(
        List<object> head, List<AlertFacts.Fact> custom, List<AlertFacts.Fact> context, int omitted)
    {
        var body = new List<object>(head);

        if (custom.Count > 0)
        {
            body.Add(new { type = "TextBlock", text = "Custom properties", weight = "Bolder", spacing = "Medium", wrap = true });
            body.Add(FactSet(custom));
        }

        object? footer = omitted == 0 ? null : new
        {
            type = "TextBlock",
            text = omitted == 1 ? "1 more field is not shown." : $"{omitted} more fields are not shown.",
            isSubtle = true,
            wrap = true
        };

        if (context.Count > 0)
        {
            var details = new List<object> { FactSet(context) };
            if (footer is not null)
                details.Add(footer);

            body.Add(new
            {
                type = "ActionSet",
                actions = new[]
                {
                    new { type = "Action.ShowCard", title = "Show details", card = new { type = "AdaptiveCard", body = details } }
                }
            });
        }
        else if (footer is not null)
        {
            body.Add(footer);
        }

        return body;
    }

    private static object FactSet(List<AlertFacts.Fact> facts) => new
    {
        type = "FactSet",
        facts = facts.Select(f => new
        {
            title = Plain(f.Title, MaxTitleLength),
            // A link isn't cut: cut short, it would lead nowhere.
            value = PortalLinkTarget(f.Value) is { } target ? $"[Open in Azure portal]({target})" : Plain(f.Value, MaxValueLength)
        }).ToList()
    };

    /// <summary>
    /// Text from the payload, cut to length and with Markdown link syntax broken up: the
    /// alert context carries text that whoever caused the event controls (an error message,
    /// a resource name), and it must not render as a link whose target differs from its text.
    /// </summary>
    private static string Plain(string text, int max) => Truncate(text, max).Replace("](", "] (", StringComparison.Ordinal);

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--; // don't split a surrogate pair
        return string.Concat(text.AsSpan(0, cut), "\u2026");
    }

    private static string Serialize(List<object> body) =>
        JsonSerializer.Serialize(new { type = "AdaptiveCard", version = "1.4", body });

    /// <summary>The card's UTF-8 size as a JSON string inside the queue message envelope.</summary>
    internal static int QueuedSize(string cardJson) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(cardJson));

    /// <summary>
    /// Green means it's over: only a resolved alert gets it. A firing one is coloured by
    /// severity, and Sev4 or no severity stays the default colour. Activity log alert rules
    /// have no severity setting and always arrive as Sev4, failures included.
    /// </summary>
    private static string GetTitleColor(string? severity, bool resolved) => resolved ? "Good" : severity switch
    {
        "Sev0" or "Sev1" => "Attention",
        "Sev2" => "Warning",
        "Sev3" => "Accent",
        _ => "Default"
    };
}
