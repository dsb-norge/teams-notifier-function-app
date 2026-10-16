using System.Text.Json;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public static class AlertCardBuilder
{
    public static string Build(CommonAlertPayload alert)
    {
        var essentials = alert.Data?.Essentials ?? new AlertEssentials();
        var severity = essentials.Severity;
        var color = GetSeverityColor(severity);
        var conditionEmoji = string.Equals(essentials.MonitorCondition, "Resolved", StringComparison.OrdinalIgnoreCase)
            ? "\u2705" : "\ud83d\udea8";

        // Azure leaves fields out or sends them as null (a budget alert has no rule, severity
        // or signal type); a fact without a value is noise, so it is left out.
        var facts = new List<object>();
        void Fact(string title, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                facts.Add(new { title, value });
        }

        Fact("Alert Rule", essentials.AlertRule);
        Fact("Severity", severity);
        Fact("Condition", essentials.MonitorCondition);
        Fact("Signal Type", essentials.SignalType);
        Fact("Fired", essentials.FiredDateTime);
        Fact("Monitoring Service", essentials.MonitoringService);

        // Without a rule name, the monitoring service ("CostAlerts") is the best name there is.
        var name = new[] { essentials.AlertRule, essentials.MonitoringService }
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

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
                text = essentials.Description,
                wrap = true
            });
        }

        var targetResource = essentials.AlertTargetIDs?.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (!string.IsNullOrEmpty(targetResource))
        {
            // Extract just the resource name from the full resource ID
            var resourceName = targetResource.TrimEnd('/').Split('/').Last();
            bodyItems.Add(new
            {
                type = "TextBlock",
                text = $"**Target:** {resourceName}",
                wrap = true,
                isSubtle = true
            });
        }

        var card = new
        {
            type = "AdaptiveCard",
            version = "1.4",
            body = bodyItems
        };

        return JsonSerializer.Serialize(card);
    }

    private static string GetSeverityColor(string? severity) => severity switch
    {
        "Sev0" or "Sev1" => "Attention",
        "Sev2" => "Warning",
        "Sev3" => "Accent",
        _ => "Good"
    };
}
