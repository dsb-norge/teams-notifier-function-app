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

            body.Add(new { type = "FactSet", facts });
        }

        var card = new
        {
            type = "AdaptiveCard",
            version = "1.4",
            body
        };

        return JsonSerializer.Serialize(card);
    }
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
