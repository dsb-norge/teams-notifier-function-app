using System.Text.Json;

namespace TeamsNotificationBot.Services;

public static class CreateAliasCardBuilder
{
    public static string Build(string? suggestedAlias = null, string? suggestedDescription = null)
    {
        var card = new
        {
            type = "AdaptiveCard",
            version = "1.4",
            body = new object[]
            {
                new
                {
                    type = "TextBlock",
                    text = "Create Notification Alias",
                    weight = "Bolder",
                    size = "Medium",
                    wrap = true
                },
                new
                {
                    type = "TextBlock",
                    text = "Create a routing alias for this conversation. Aliases are used in API endpoint URLs to deliver notifications.",
                    wrap = true,
                    isSubtle = true
                },
                new
                {
                    type = "TextBlock",
                    text = "Alias Name",
                    weight = "Bolder"
                },
                new
                {
                    type = "Input.Text",
                    id = "aliasName",
                    placeholder = "e.g. devops-alerts",
                    value = suggestedAlias ?? "",
                    regex = "^[a-z0-9][a-z0-9\\-]{0,48}[a-z0-9]$",
                    errorMessage = "2-50 chars: lowercase letters, digits, hyphens. Must start/end with letter or digit."
                },
                new
                {
                    type = "TextBlock",
                    text = "Description (optional)",
                    weight = "Bolder"
                },
                new
                {
                    type = "Input.Text",
                    id = "aliasDescription",
                    placeholder = "e.g. DevOps pipeline alerts",
                    value = suggestedDescription ?? "",
                    isMultiline = false
                }
            },
            actions = new object[]
            {
                new
                {
                    type = "Action.Submit",
                    title = "Create Alias",
                    data = new { action = "createAlias" }
                }
            }
        };

        return JsonSerializer.Serialize(card);
    }

    /// <summary>
    /// Derives a suggested alias name from the conversation context.
    /// Channel: channel name lowercased and hyphenated.
    /// Personal: first name lowercased.
    /// GroupChat: empty.
    /// </summary>
    public static string? DeriveAlias(string? conversationType, string? channelName = null, string? userName = null)
    {
        switch (conversationType)
        {
            case "channel" when !string.IsNullOrWhiteSpace(channelName):
            {
                // "DevOps Alerts" -> "devops-alerts"
                var alias = channelName.Trim().ToLowerInvariant()
                    .Replace(' ', '-')
                    .Replace("_", "-");
                // Remove invalid chars
                alias = System.Text.RegularExpressions.Regex.Replace(alias, @"[^a-z0-9\-]", "");
                // Collapse multiple hyphens
                alias = System.Text.RegularExpressions.Regex.Replace(alias, @"-{2,}", "-");
                // Trim hyphens from ends
                alias = alias.Trim('-');
                return alias.Length >= 2 ? alias : null;
            }
            case "personal" when !string.IsNullOrWhiteSpace(userName):
            {
                var firstName = userName.Trim().Split(' ')[0].ToLowerInvariant();
                firstName = System.Text.RegularExpressions.Regex.Replace(firstName, @"[^a-z0-9]", "");
                return firstName.Length >= 2 ? firstName : null;
            }
            default:
                return null;
        }
    }
}
