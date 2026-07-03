namespace TeamsNotificationBot.Services;

public static class HelpTextBuilder
{
    public static string Overview() =>
        "**Teams Notification Bot** delivers notifications from external systems " +
        "to Teams conversations.\n\n" +
        "Notifications are routed via **aliases** \u2014 named targets that map to a " +
        "specific channel, personal chat, or group chat. External systems send " +
        "notifications to the bot's API using the alias name.\n\n" +
        "External monitoring (e.g. [updown.io](https://updown.io)) can also push alerts straight " +
        "into a conversation via a per-channel **webhook** \u2014 run **help webhooks**.\n\n" +
        "**Help topics:**\n" +
        "- **help aliases** \u2014 managing notification targets\n" +
        "- **help endpoints** \u2014 API endpoints for sending notifications\n" +
        "- **help webhooks** \u2014 updown.io monitoring webhook ingress\n" +
        "- **help queues** \u2014 poison queue monitoring and retry\n" +
        "- **help diagnostics** \u2014 health checks and troubleshooting\n\n" +
        "For any single command, run **help <command>** \u2014 e.g. **help configure-webhook** \u2014 for its " +
        "full usage and arguments.";

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
        $"- `POST https://{hostname}/api/v1/ingest/updown/{{token}}` \u2014 anonymous updown.io webhook ingress (run **help webhooks**)\n" +
        $"- `GET  https://{hostname}/api/v1/aliases` \u2014 list all aliases (JSON; debug mode only)\n" +
        $"- `GET  https://{hostname}/api/health` \u2014 bot health status (public)\n\n" +
        "The `notify`, `alert`, `send`, and `checkin` endpoints require **Entra ID authentication** " +
        "(run **setup-guide** for setup). `/api/health` is public. The updown ingress is authenticated " +
        "by its per-webhook secret token, not Entra ID \u2014 see **help webhooks**.";

    public static string Webhooks() =>
        "**updown.io webhooks** let the external monitoring service push uptime/SSL alerts into " +
        "this conversation as Adaptive Cards — no Entra ID token required.\n\n" +
        "Each webhook has its own **secret URL** containing a high-entropy token. That token is " +
        "the only thing protecting the endpoint (updown does not sign its requests), so:\n" +
        "- The URL is shown **once** at creation — store it in updown, don't paste it back in chat.\n" +
        "- If it leaks, run **rotate-webhook** to invalidate the old one.\n" +
        "- The token is never logged and only its hash is stored.\n\n" +
        "Cards carry **no clickable buttons** and are labelled *unverified sender*. Only the updown.io " +
        "downtime link is clickable.\n\n" +
        "**Commands** (run in the target channel/chat):\n" +
        "- **create-webhook** `account <updown account> description <description>` — create a webhook for " +
        "this conversation; account + description are required (they help humans track it); returns the secret URL once\n" +
        "- **list-webhooks** — show configured webhooks (id, target, events, last received) — never the secret\n" +
        "- **show-webhook** `<id>` — show one webhook's details (same info as the list, for a single id)\n" +
        "- **configure-webhook** `<id>` `<description|account|events>` `<value>` — update settings\n" +
        "  - `events` takes a comma list, e.g. `check.down,check.up,check.ssl_expiration`, or `all`\n" +
        "- **rotate-webhook** `<id>` — issue a new URL, invalidating the old token\n" +
        "- **remove-webhook** `<id>` — delete the webhook\n" +
        "- **show-ip-allow-list** `updown` — show the source-IP allowlist (mode, entries, last refresh)\n" +
        "- **update-ip-allow-list** `updown` — refresh the allowlist from updown's published IPs\n\n" +
        "The ingress is also protected by a **source-IP allowlist** of updown's published IPs " +
        "(`ips.updown.io`). It has three modes (app setting `UpdownWebhook__IpFilterMode`): `off`, " +
        "`log-only` (default — logs but never blocks), and `enforce` (rejects non-updown IPs). " +
        "The list refreshes automatically when stale, and on demand via **update-ip-allow-list**.\n\n" +
        "**Events** (default = all except `check.performance_drop`): `check.down`, `check.up`, " +
        "`check.ssl_invalid`, `check.ssl_valid`, `check.ssl_expiration`, `check.ssl_renewed`, " +
        "`check.performance_drop`.\n\n" +
        "**Setup:** paste the URL into updown as a webhook recipient. Send a test from " +
        "updown's *recipients → test* page to verify delivery before going live.";

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

    /// <summary>
    /// Detailed help for a single command (<c>help &lt;command&gt;</c>), or null if the name isn't a
    /// known command. Event lists are sourced from <see cref="Models.UpdownEventTypes"/> so they can't
    /// drift from the code.
    /// </summary>
    public static string? CommandHelp(string command)
    {
        var allEvents = string.Join(", ", Models.UpdownEventTypes.All);
        var defaultEvents = string.Join(", ", Models.UpdownEventTypes.DefaultEnabled);

        return command switch
        {
            // --- Aliases ---
            "set-alias" =>
                "**set-alias** `<name>` `[description]`\n\n" +
                "Create or update an alias that points at **this** conversation. External systems then " +
                "deliver to it via `POST /api/v1/notify/<name>`.\n\n" +
                "- `name` — the alias (letters, digits, `-`, `_`); used as the URL path segment.\n" +
                "- `description` — optional free text shown in **list-aliases**.",
            "create-alias" =>
                "**create-alias**\n\nOpens an interactive card to create an alias for this conversation " +
                "(an alternative to **set-alias**). Fill in the name + description and submit.",
            "remove-alias" =>
                "**remove-alias** `<name>`\n\nDelete an alias. External calls to that alias stop resolving. " +
                "Does not affect other aliases pointing at the same conversation.",
            "list-aliases" =>
                "**list-aliases**\n\nShow every alias with its target conversation, description, and who " +
                "created it. Read-only.",

            // --- Webhooks (updown.io ingress) ---
            "create-webhook" =>
                "**create-webhook** `[updown]` `account <account>` `description <description>`\n\n" +
                "Create an updown.io webhook for **this** conversation and return its secret ingest URL " +
                "**once**. Both fields are required — they help humans manage multiple updown accounts:\n" +
                "- `account` — the updown account this webhook belongs to (free text, e.g. an email like " +
                "`ops@dsb.no`; may contain `/` and `@`).\n" +
                "- `description` — what it's for (free text). Must come after `account`.\n\n" +
                $"Newly created webhooks enable these events by default: {defaultEvents} " +
                "(i.e. all except `check.performance_drop`). Change them later with **configure-webhook**.\n\n" +
                "Example: `create-webhook account ops@dsb.no description Prod uptime + SSL`",
            "configure-webhook" =>
                "**configure-webhook** `<id>` `<field>` `<value>` — update one field of an existing webhook. " +
                "The confirmation shows the value **before → after** (or reports it unchanged).\n\n" +
                "Fields:\n" +
                "- `description` (alias `desc`) `<text>` — human note.\n" +
                "- `account` `<label>` — the updown account label (free text; may contain `/`, `@`).\n" +
                "- `events` `<list|all>` — comma-separated event filter, or `all`.\n\n" +
                $"Valid events: {allEvents}.\n" +
                $"Default when a webhook is created: {defaultEvents} (all except `check.performance_drop`).\n\n" +
                "Examples:\n" +
                "- `configure-webhook <id> description Production site health`\n" +
                "- `configure-webhook <id> account prod-monitoring / ops@dsb.no`\n" +
                "- `configure-webhook <id> events check.down,check.up,check.ssl_expiration` (or `all`)",
            "list-webhooks" or "list-webhook" =>
                "**list-webhooks**\n\nShow all configured webhooks — id, target, account, description, " +
                "enabled events, created-by, and last-received. Never shows the secret URL/token. Use " +
                "**show-webhook** `<id>` for a single one.",
            "show-webhook" =>
                "**show-webhook** `<id>`\n\nShow one webhook's details — the same facts as **list-webhooks** " +
                "but for a single id (handy when the list is long). Never shows the secret.",
            "remove-webhook" =>
                "**remove-webhook** `<id>`\n\nDelete a webhook. Its token stops working immediately and " +
                "updown deliveries to that URL start failing. Irreversible — create a new one to replace it.",
            "rotate-webhook" =>
                "**rotate-webhook** `<id>`\n\nIssue a **new** secret URL for an existing webhook and " +
                "invalidate the old token. Use if a URL may have leaked. Paste the new URL into updown; " +
                "the id, target, and event filter are unchanged.",

            // --- Source-IP allowlist ---
            "show-ip-allow-list" or "show-ip-allowlist" =>
                "**show-ip-allow-list** `updown`\n\nShow the updown source-IP allowlist: mode " +
                "(`off` / `log-only` / `enforce`), entry count, when it last refreshed, and the CIDRs. " +
                "The list is populated automatically (at startup and on ingest) and refreshed when stale.",
            "update-ip-allow-list" or "update-ip-allowlist" =>
                "**update-ip-allow-list** `updown`\n\nForce an immediate refresh of the source-IP allowlist " +
                "from updown's published IPs (`ips.updown.io`), reporting added/removed entries. Normally " +
                "unnecessary — the list refreshes on its own — but useful right after updown changes IPs.",

            // --- Queues ---
            "queue-status" =>
                "**queue-status**\n\nShow message counts across all work and poison queues " +
                "(`notifications`, `botoperations`, and their `-poison` companions). First stop when " +
                "something looks stuck.",
            "queue-peek" =>
                "**queue-peek** `<queue>` `[N]`\n\nPreview up to `N` messages (default a small batch) from a " +
                "queue **without** removing them. Use on a `-poison` queue to see why messages failed.",
            "queue-retry" =>
                "**queue-retry** `<queue>` `[N]`\n\nMove up to `N` messages from a poison queue back to its " +
                "work queue for reprocessing. Use **queue-retry-all** to retry everything.",
            "queue-retry-all" =>
                "**queue-retry-all** `<queue>`\n\nMove **all** messages from a poison queue back for " +
                "reprocessing. Prefer **queue-retry** `<queue>` `[N]` when you only want a few.",

            // --- Diagnostics / misc ---
            "checkin" =>
                "**checkin**\n\nVerify the bot is running — replies with the app version and a timestamp. " +
                "Also usable as an application heartbeat via `POST /api/v1/checkin/<alias>`.",
            "setup-guide" =>
                "**setup-guide**\n\nStep-by-step Entra ID setup for API callers (app registration, the " +
                "`Notifications.Send` app role, and the token request) so external systems can call the " +
                "`notify`/`alert`/`send`/`checkin` endpoints.",
            "delete-post" =>
                "**delete-post**\n\nDelete a bot message in a channel. **Reply to — or quote —** the bot " +
                "message you want gone and send `delete-post`. The bot can only delete messages it sent.",
            "help" =>
                "**help** `[topic|command]`\n\n" +
                "- `help` — overview.\n" +
                "- `help <topic>` — a section: **aliases**, **endpoints**, **webhooks**, **queues**, " +
                "**diagnostics**.\n" +
                "- `help <command>` — detailed help for a single command, e.g. **help configure-webhook**.",
            _ => null
        };
    }

    public static string UnknownCommand(string text) =>
        $"Unknown command: `{text}`\n\nRun **help** to see available topics.";
}
