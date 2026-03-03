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
        var conditionEmoji = essentials.MonitorCondition.Equals("Resolved", StringComparison.OrdinalIgnoreCase)
            ? "\u2705" : "\ud83d\udea8";

        var facts = new List<object>
        {
            new { title = "Alert Rule", value = essentials.AlertRule },
            new { title = "Severity", value = severity },
            new { title = "Condition", value = essentials.MonitorCondition },
            new { title = "Signal Type", value = essentials.SignalType },
            new { title = "Fired", value = essentials.FiredDateTime }
        };

        if (!string.IsNullOrEmpty(essentials.MonitoringService))
            facts.Add(new { title = "Monitoring Service", value = essentials.MonitoringService });

        var bodyItems = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = $"{conditionEmoji} Azure Monitor Alert: {essentials.AlertRule}",
                weight = "Bolder",
                size = "Medium",
                wrap = true,
                color
            },
            new
            {
                type = "FactSet",
                facts
            }
        };

        if (!string.IsNullOrEmpty(essentials.Description))
        {
            bodyItems.Add(new
            {
                type = "TextBlock",
                text = essentials.Description,
                wrap = true
            });
        }

        var targetResource = essentials.AlertTargetIDs?.FirstOrDefault();
        if (!string.IsNullOrEmpty(targetResource))
        {
            // Extract just the resource name from the full resource ID
            var resourceName = targetResource.Split('/').LastOrDefault() ?? targetResource;
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

    private static string GetSeverityColor(string severity) => severity switch
    {
        "Sev0" or "Sev1" => "Attention",
        "Sev2" => "Warning",
        "Sev3" => "Accent",
        _ => "Good"
    };
}
