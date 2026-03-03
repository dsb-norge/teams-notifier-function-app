namespace TeamsNotificationBot.Services;

public static class HelpTextBuilder
{
    public static string Overview() =>
        "**Teams Notification Bot** delivers notifications from external systems " +
        "to Teams conversations.\n\n" +
        "Notifications are routed via **aliases** \u2014 named targets that map to a " +
        "specific channel, personal chat, or group chat. External systems send " +
        "notifications to the bot's API using the alias name.\n\n" +
        "**Help topics:**\n" +
        "- **help aliases** \u2014 managing notification targets\n" +
        "- **help endpoints** \u2014 API endpoints for sending notifications\n" +
        "- **help queues** \u2014 poison queue monitoring and retry\n" +
        "- **help diagnostics** \u2014 health checks and troubleshooting";

    public static string Aliases() =>
        "**Aliases** are named routing targets. Each alias points to a specific " +
        "Teams conversation (channel, personal chat, or group chat). External " +
        "systems use the alias name in the API URL to deliver notifications.\n\n" +
        "Example: alias `ops-alerts` \u2192 #operations channel " +
        "\u2192 `POST /api/v1/notify/ops-alerts`\n\n" +
        "**Commands:**\n" +
        "- **set-alias** `<name>` `[description]` \u2014 create/update alias for this conversation\n" +
        "- **create-alias** \u2014 interactive form for alias creation\n" +
        "- **remove-alias** `<name>` \u2014 delete an alias\n" +
        "- **list-aliases** \u2014 show all aliases with details";

    public static string Endpoints(string hostname) =>
        "The bot exposes HTTP API endpoints that external systems use to send notifications:\n\n" +
        $"- `POST https://{hostname}/api/v1/notify/{{alias}}` \u2014 send notification to an alias (markdown or Adaptive Card)\n" +
        $"- `POST https://{hostname}/api/v1/alert/{{alias}}` \u2014 receive Azure Monitor alert webhooks\n" +
        $"- `POST https://{hostname}/api/v1/send` \u2014 send to a specific conversation by reference\n" +
        $"- `POST https://{hostname}/api/v1/checkin/{{alias}}` \u2014 application heartbeat check-in\n" +
        $"- `GET  https://{hostname}/api/v1/aliases` \u2014 list all aliases (JSON)\n" +
        $"- `GET  https://{hostname}/api/health` \u2014 bot health status\n\n" +
        "All endpoints require **Entra ID authentication**. Run **setup-guide** for auth setup instructions.";

    public static string Queues() =>
        "Messages that fail processing are moved to **poison queues**. " +
        "The bot monitors these and sends alerts automatically. " +
        "You can also inspect and retry failed messages manually.\n\n" +
        "**Queues:** `notifications-poison`, `botoperations-poison`\n\n" +
        "**Commands:**\n" +
        "- **queue-status** \u2014 show message counts across all queues\n" +
        "- **queue-peek** `<queue>` `[N]` \u2014 preview messages without removing them\n" +
        "- **queue-retry** `<queue>` `[N]` \u2014 move messages back for reprocessing\n" +
        "- **queue-retry-all** `<queue>` \u2014 retry all messages in a poison queue";

    public static string Diagnostics() =>
        "**Diagnostic commands:**\n\n" +
        "- **checkin** \u2014 verify the bot is running (shows version and timestamp)\n" +
        "- **setup-guide** \u2014 Entra ID authentication setup for API callers\n" +
        "- **delete-post** \u2014 reply to a bot message in a channel to delete it\n" +
        "- **help** `[topic]` \u2014 this help system";

    public static string UnknownCommand(string text) =>
        $"Unknown command: `{text}`\n\nRun **help** to see available topics.";
}
