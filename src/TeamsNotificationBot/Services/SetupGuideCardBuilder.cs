using System.Text.Json;

namespace TeamsNotificationBot.Services;

public static class SetupGuideCardBuilder
{
    public static string Build(string apiAppId, string hostname)
    {
        var audience = $"api://{apiAppId}";
        var scope = $"api://{apiAppId}/.default";
        var notifyUrl = $"https://{hostname}/api/v1/notify/{{alias}}";
        var alertUrl = $"https://{hostname}/api/v1/alert/{{alias}}";

        var card = new
        {
            type = "AdaptiveCard",
            version = "1.4",
            body = new object[]
            {
                new
                {
                    type = "TextBlock",
                    text = "API Authentication Setup Guide",
                    weight = "Bolder",
                    size = "Large",
                    wrap = true
                },
                new
                {
                    type = "TextBlock",
                    text = "This bot's API endpoints use Entra ID (Azure AD) authentication via EasyAuth. " +
                           "Callers must present a valid Bearer token with the required app role.",
                    wrap = true
                },
                // Section 1: Service Principal / Client Credentials
                new
                {
                    type = "TextBlock",
                    text = "1. Service Principal Setup",
                    weight = "Bolder",
                    size = "Medium",
                    wrap = true
                },
                new
                {
                    type = "FactSet",
                    facts = new object[]
                    {
                        new { title = "Audience", value = audience },
                        new { title = "Scope", value = scope },
                        new { title = "Required Role", value = "Notifications.Send" }
                    }
                },
                new
                {
                    type = "TextBlock",
                    text = "Create a service principal (or use an existing one), then have a tenant admin " +
                           "grant the **Notifications.Send** app role assignment to it.",
                    wrap = true
                },
                // Section 2: Azure Monitor Action Group
                new
                {
                    type = "TextBlock",
                    text = "2. Azure Monitor Action Group (Webhook)",
                    weight = "Bolder",
                    size = "Medium",
                    wrap = true
                },
                new
                {
                    type = "FactSet",
                    facts = new object[]
                    {
                        new { title = "Webhook URI", value = alertUrl },
                        new { title = "Enable AAD Auth", value = "Yes" },
                        new { title = "AAD Resource URI", value = audience },
                        new { title = "Use Common Schema", value = "Yes" }
                    }
                },
                // Section 3: Manual Testing
                new
                {
                    type = "TextBlock",
                    text = "3. Manual Testing",
                    weight = "Bolder",
                    size = "Medium",
                    wrap = true
                },
                new
                {
                    type = "TextBlock",
                    text = "Get a token and call the notify endpoint:",
                    wrap = true
                },
                new
                {
                    type = "TextBlock",
                    text = $"```\nTOKEN=$(az account get-access-token \\\n  --resource {audience} \\\n  --query accessToken -o tsv)\n\ncurl -X POST {notifyUrl} \\\n  -H \"Authorization: Bearer $TOKEN\" \\\n  -H \"Content-Type: application/json\" \\\n  -d '{{\"message\": \"Hello from API\"}}'\n```",
                    wrap = true,
                    fontType = "Monospace"
                },
                // Section 4: Endpoints
                new
                {
                    type = "TextBlock",
                    text = "4. Endpoint Templates",
                    weight = "Bolder",
                    size = "Medium",
                    wrap = true
                },
                new
                {
                    type = "FactSet",
                    facts = new object[]
                    {
                        new { title = "Notify", value = notifyUrl },
                        new { title = "Alert", value = alertUrl }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(card);
    }
}
