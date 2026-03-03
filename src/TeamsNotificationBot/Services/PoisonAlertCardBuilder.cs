using System.Text.Json;

namespace TeamsNotificationBot.Services;

public static class PoisonAlertCardBuilder
{
    public static string Build(string sourceQueue, string? messageExcerpt, DateTimeOffset? enqueuedTime)
    {
        var facts = new List<object>
        {
            new { title = "Source Queue", value = sourceQueue },
            new { title = "Detected At", value = DateTimeOffset.UtcNow.ToString("u") }
        };

        if (enqueuedTime.HasValue)
            facts.Add(new { title = "Originally Enqueued", value = enqueuedTime.Value.ToString("u") });

        var bodyItems = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = "\u26a0\ufe0f Poison Queue Alert",
                weight = "Bolder",
                size = "Medium",
                color = "Attention",
                wrap = true
            },
            new
            {
                type = "TextBlock",
                text = $"A message failed processing and was moved to **{sourceQueue}**.",
                wrap = true
            },
            new
            {
                type = "FactSet",
                facts
            }
        };

        if (!string.IsNullOrEmpty(messageExcerpt))
        {
            var truncated = messageExcerpt.Length > 500
                ? messageExcerpt[..500] + "..."
                : messageExcerpt;

            bodyItems.Add(new
            {
                type = "TextBlock",
                text = "**Message Excerpt:**",
                wrap = true
            });
            bodyItems.Add(new
            {
                type = "TextBlock",
                text = $"```\n{truncated}\n```",
                wrap = true,
                fontType = "Monospace"
            });
        }

        bodyItems.Add(new
        {
            type = "TextBlock",
            text = "Use **queue-status** and **queue-retry** commands to manage poison messages.",
            wrap = true,
            isSubtle = true
        });

        var card = new
        {
            type = "AdaptiveCard",
            version = "1.4",
            body = bodyItems
        };

        return JsonSerializer.Serialize(card);
    }
}
