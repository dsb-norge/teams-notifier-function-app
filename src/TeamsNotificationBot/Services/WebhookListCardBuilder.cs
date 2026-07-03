using System.Text.Json;

namespace TeamsNotificationBot.Services;

/// <summary>
/// Renders the configured webhooks as an Adaptive Card. Never includes the token/URL — those are
/// secrets shown only once at creation. The short <c>Id</c> is used for manage commands.
/// </summary>
public static class WebhookListCardBuilder
{
    public static string Build(IReadOnlyList<WebhookDisplayInfo> webhooks)
    {
        var body = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = $"🔌 Webhooks ({webhooks.Count})",
                weight = "Bolder",
                size = "Large"
            }
        };

        foreach (var w in webhooks)
        {
            body.Add(new
            {
                type = "TextBlock",
                text = $"`{w.Id}` — {w.Source}",
                weight = "Bolder",
                size = "Medium",
                separator = true,
                spacing = "Medium"
            });
            body.Add(new { type = "FactSet", facts = Facts(w) });
        }

        return SerializeCard(body);
    }

    /// <summary>Renders a single webhook (same facts as the list) for the <c>show-webhook</c> command.</summary>
    public static string BuildSingle(WebhookDisplayInfo w)
    {
        var body = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = $"🔌 Webhook `{w.Id}` — {w.Source}",
                weight = "Bolder",
                size = "Large"
            },
            new { type = "FactSet", facts = Facts(w) }
        };

        return SerializeCard(body);
    }

    private static List<object> Facts(WebhookDisplayInfo w)
    {
        var facts = new List<object> { new { title = "🎯 target", value = w.TargetLabel } };

        if (!string.IsNullOrEmpty(w.Description))
            facts.Add(new { title = "📝 description", value = w.Description });

        if (!string.IsNullOrEmpty(w.UpdownAccount))
            facts.Add(new { title = "👤 updown account", value = w.UpdownAccount });

        facts.Add(new { title = "🔔 events", value = w.EnabledEvents });

        if (!string.IsNullOrEmpty(w.CreatedByName))
            facts.Add(new { title = "➕ created by", value = w.CreatedByName });

        facts.Add(new { title = "🕐 created", value = w.RelativeCreated });
        facts.Add(new { title = "📥 last received", value = w.LastReceived });

        return facts;
    }

    private static string SerializeCard(List<object> body) =>
        JsonSerializer.Serialize(new { type = "AdaptiveCard", version = "1.4", body });
}

public record WebhookDisplayInfo(
    string Id,
    string Source,
    string TargetLabel,
    string? Description,
    string? UpdownAccount,
    string EnabledEvents,
    string? CreatedByName,
    string RelativeCreated,
    string LastReceived);
