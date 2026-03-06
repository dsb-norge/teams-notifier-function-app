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

        var body = new List<object>();

        // Title
        body.Add(TextBlock("Setup Guide — Sending Notifications via API", "Bolder", "Large"));

        // Overview
        body.Add(TextBlock(
            "This guide walks you through connecting your application or Azure Monitor alerts " +
            "to this notification bot. Once set up, your systems can send messages to any Teams " +
            "channel where an alias has been configured."));

        body.Add(TextBlock(
            "There are two ways to send notifications: **programmatically** (from your own code, " +
            "scripts, or pipelines) or **automatically** from Azure Monitor alerts. Both require " +
            "authentication — read on for step-by-step instructions."));

        body.Add(Separator());

        // Section 1: How Authentication Works
        body.Add(TextBlock("How Authentication Works", "Bolder", "Medium"));

        body.Add(TextBlock(
            "This bot's API is protected by **Entra ID (Azure AD)**. Every request must include " +
            "an access token proving the caller is authorized. Think of it like a keycard: your " +
            "application gets a token from Entra ID, and the bot checks that token before " +
            "accepting the request."));

        body.Add(TextBlock(
            "The token must be issued for the correct **audience** (this bot's API) and the caller " +
            "must have been granted the **Notifications.Send** role. Without this role, requests " +
            "will be rejected with a 403 Forbidden error."));

        body.Add(TextBlock("Key values for your configuration:", "Bolder"));

        body.Add(new
        {
            type = "FactSet",
            facts = new object[]
            {
                new { title = "Audience (Resource ID)", value = audience },
                new { title = "Scope (for token requests)", value = scope },
                new { title = "Required Role", value = "Notifications.Send" }
            }
        });

        body.Add(Separator());

        // Section 2: Setting Up a Service Principal
        body.Add(TextBlock("Option A — Send Notifications from Code or Pipelines", "Bolder", "Medium"));

        body.Add(TextBlock(
            "To call the API from your own application, CI/CD pipeline, or script, you need " +
            "a **service principal** (an identity for your application in Entra ID). " +
            "If your team already has one, you can reuse it."));

        body.Add(TextBlock("**Step 1: Get a service principal**"));
        body.Add(TextBlock(
            "If you don't already have one, ask your Entra ID administrator to create an " +
            "app registration for your application. The service principal is the identity " +
            "that will authenticate against this bot's API."));

        body.Add(TextBlock("**Step 2: Request the Notifications.Send role**"));
        body.Add(TextBlock(
            "A tenant administrator must grant your service principal the **Notifications.Send** " +
            "app role on this bot's API application. This is a one-time setup — once granted, " +
            "your application can request tokens and call the API."));

        body.Add(TextBlock("**Step 3: Get a token and call the API**"));
        body.Add(TextBlock(
            "Your application requests a token from Entra ID using the client credentials flow " +
            "(client ID + secret or certificate). The token is then sent as a Bearer token in " +
            "the Authorization header of your HTTP request."));

        body.Add(Separator());

        // Section 3: Endpoints
        body.Add(TextBlock("API Endpoints", "Bolder", "Medium"));

        body.Add(TextBlock(
            "Replace `{alias}` with the alias name configured in this bot. Use `list-aliases` " +
            "to see available aliases."));

        body.Add(new
        {
            type = "FactSet",
            facts = new object[]
            {
                new { title = "Send a notification", value = notifyUrl },
                new { title = "Forward an Azure alert", value = alertUrl }
            }
        });

        body.Add(TextBlock(
            "The **notify** endpoint accepts a simple JSON message. " +
            "The **alert** endpoint accepts Azure Monitor's Common Alert Schema " +
            "and renders it as a formatted Adaptive Card."));

        body.Add(Separator());

        // Section 4: Testing with curl
        body.Add(TextBlock("Quick Test — Try It from the Command Line", "Bolder", "Medium"));

        body.Add(TextBlock(
            "If you have the Azure CLI installed and your account has the Notifications.Send " +
            "role, you can test the API right away. Run these commands in your terminal:"));

        body.Add(TextBlock("**1. Get an access token:**"));
        body.Add(new
        {
            type = "TextBlock",
            text = $"```\naz account get-access-token \\\n  --resource {audience} \\\n  --query accessToken -o tsv\n```",
            wrap = true,
            fontType = "Monospace"
        });

        body.Add(TextBlock(
            "This asks Entra ID for a token scoped to this bot's API. " +
            "Copy the output — that's your Bearer token."));

        body.Add(TextBlock("**2. Send a test notification:**"));
        body.Add(new
        {
            type = "TextBlock",
            text = $"```\nTOKEN=$(az account get-access-token \\\n  --resource {audience} \\\n  --query accessToken -o tsv)\n\ncurl -X POST {notifyUrl} \\\n  -H \"Authorization: Bearer $TOKEN\" \\\n  -H \"Content-Type: application/json\" \\\n  -d '{{\"message\": \"Hello from the API!\"}}'\n```",
            wrap = true,
            fontType = "Monospace"
        });

        body.Add(TextBlock(
            "If everything is configured correctly, you should see the message appear in the " +
            "Teams channel mapped to that alias. A 200 response means success. " +
            "A 403 means the Notifications.Send role has not been granted to your identity."));

        body.Add(Separator());

        // Section 5: Azure Monitor Action Group
        body.Add(TextBlock("Option B — Forward Azure Monitor Alerts Automatically", "Bolder", "Medium"));

        body.Add(TextBlock(
            "Azure Monitor can send alerts directly to this bot using a **webhook action** " +
            "in an Action Group. When an alert fires, Azure Monitor calls the bot's alert " +
            "endpoint, and the bot posts a formatted card to the configured Teams channel."));

        body.Add(TextBlock("**How to configure the Action Group webhook:**"));

        body.Add(TextBlock(
            "In the Azure Portal, find your Action Group (or create a new one), add a " +
            "webhook action, and configure it with these settings:"));

        body.Add(new
        {
            type = "FactSet",
            facts = new object[]
            {
                new { title = "Webhook URI", value = alertUrl },
                new { title = "Enable AAD Auth", value = "Yes" },
                new { title = "AAD Resource URI", value = audience },
                new { title = "Use Common Schema", value = "Yes" }
            }
        });

        body.Add(TextBlock(
            "**Enable AAD Auth** tells Azure Monitor to attach an Entra ID token to the " +
            "webhook call. The **AAD Resource URI** (audience) must match this bot's API " +
            "registration so the token is accepted. **Common Schema** ensures the alert " +
            "payload is in a standard format the bot can parse into a readable card."));

        body.Add(TextBlock(
            "The webhook's built-in service principal " +
            "(\"Azure Notification Service\", part of the Azure platform) must also be " +
            "granted the **Notifications.Send** role — ask your Entra ID administrator."));

        body.Add(Separator());

        // Section 6: Where to Learn More
        body.Add(TextBlock("Need More Details?", "Bolder", "Medium"));

        body.Add(TextBlock(
            "The bot's documentation repository has comprehensive guides covering " +
            "authentication, API reference, roles and permissions, deployment, and " +
            "troubleshooting. Ask your administrator for the repository URL, or check " +
            "the following docs:"));

        body.Add(TextBlock(
            "- **Authentication Guide** — explains all identities, token flows, and " +
            "EasyAuth configuration in detail\n" +
            "- **API Reference** — full endpoint specs, request/response examples, " +
            "error codes, rate limits, and idempotency\n" +
            "- **Access & Roles** — which Azure, Entra ID, and Teams roles are needed " +
            "and who should have them\n" +
            "- **Troubleshooting** — common errors and how to resolve them"));

        var card = new
        {
            type = "AdaptiveCard",
            version = "1.4",
            body = body.ToArray()
        };

        return JsonSerializer.Serialize(card);
    }

    private static object TextBlock(string text, string? weight = null, string? size = null)
    {
        // Build as dictionary to avoid serializing null properties
        var block = new Dictionary<string, object>
        {
            ["type"] = "TextBlock",
            ["text"] = text,
            ["wrap"] = true
        };
        if (weight != null) block["weight"] = weight;
        if (size != null) block["size"] = size;
        return block;
    }

    private static object Separator()
    {
        return new
        {
            type = "TextBlock",
            text = " ",
            spacing = "Medium",
            separator = true
        };
    }
}
