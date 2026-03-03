using System.Text.Json;

namespace TeamsNotificationBot.Services;

public static class AliasListCardBuilder
{
    public static string Build(IReadOnlyList<AliasDisplayInfo> aliases, string hostname)
    {
        var body = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = $"\ud83d\udccb Aliases ({aliases.Count})",
                weight = "Bolder",
                size = "Large"
            }
        };

        foreach (var alias in aliases)
        {
            body.Add(new
            {
                type = "TextBlock",
                text = alias.Name,
                weight = "Bolder",
                size = "Medium",
                separator = true,
                spacing = "Medium"
            });

            var facts = new List<object>();

            var (targetEmoji, targetLabel) = alias.TargetType switch
            {
                "personal" => ("\ud83d\udcac", "personal chat"),
                "channel" => ("\ud83d\udce2", "channel"),
                "groupChat" => ("\ud83d\udc65", "group chat"),
                _ => ("\u2753", "target")
            };
            facts.Add(new { title = $"{targetEmoji} {targetLabel}", value = alias.Target });

            if (!string.IsNullOrEmpty(alias.Description))
                facts.Add(new { title = "\ud83d\udcdd description", value = alias.Description });

            if (!string.IsNullOrEmpty(alias.CreatedByName))
                facts.Add(new { title = "\ud83d\udc64 created by", value = alias.CreatedByName });

            facts.Add(new { title = "\ud83d\udd50 created", value = alias.RelativeTime });

            facts.Add(new { title = "\ud83d\udd17 notify", value = $"https://{hostname}/api/v1/notify/{alias.Name}" });

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

public record AliasDisplayInfo(
    string Name,
    string TargetType,
    string Target,
    string? Description,
    string? CreatedByName,
    string RelativeTime);
