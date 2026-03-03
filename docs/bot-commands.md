# Teams Notification Bot — Bot Commands

| Field   | Value |
|---------|-------|
| Status  | Active |
| Created | 2026-03-01 |
| Audience | Bot users, team administrators, platform engineers |

---

## 1. Overview

The bot responds to text commands sent through Microsoft Teams. How you invoke a command depends on
the conversation scope:

| Scope | How to invoke |
|-------|---------------|
| **Team channel** | @mention the bot followed by the command: `@<bot-display-name> help` |
| **Personal chat** | Send the command directly: `help` |
| **Group chat** | Send the command directly: `help` |

The bot is installed in three Teams scopes: `team`, `personal`, and `groupChat`. Not all commands
are available in every scope — see [Command Availability by Scope](#6-command-availability-by-scope)
for the full matrix.

Commands are case-insensitive. Arguments are separated by spaces.

---

## 2. General Commands

### help

Displays a help overview with links to specific topics. Use `help <topic>` for detailed guidance
on a specific area.

```
help              — overview and topic list
help aliases      — alias management commands
help endpoints    — API endpoint reference
help queues       — poison queue management
help diagnostics  — health checks and troubleshooting
```

**Example response:**

```
Teams Notification Bot — Help

Available topics:
  help aliases      Alias management commands
  help endpoints    API endpoint reference
  help queues       Poison queue management
  help diagnostics  Health checks and troubleshooting

Type "help <topic>" for details.
```

---

### checkin

Performs a health check and confirms the bot is reachable.

**Example response:**

```
Bot is online | Version: 1.1.0 | Time: 2026-01-15 14:30:00Z
```

---

## 3. Alias Management

Aliases map a human-readable name to a specific Teams conversation, allowing external systems to
send notifications via the API without needing internal conversation identifiers. See the
[API Reference](api-reference.md) for the corresponding HTTP endpoints.

### set-alias `<name>` `[description]`

Creates or updates an alias that points to the current conversation (channel, personal chat, or
group chat). If the alias already exists in this conversation, its description is updated. If the
alias exists in a different conversation, the command fails with an error.

**Arguments:**

| Argument | Required | Description |
|----------|----------|-------------|
| `name` | Yes | Alias name (2--50 characters, lowercase letters, digits, hyphens; must start and end with a letter or digit) |
| `description` | No | Free-text description of the alias purpose |

**Example:**

```
@<bot-display-name> set-alias ops-alerts Production operations alerts
```

**Response:**

```
Alias ops-alerts set for this channel.
Notify: https://<function-app-name>.azurewebsites.net/api/v1/notify/ops-alerts
Alert:  https://<function-app-name>.azurewebsites.net/api/v1/alert/ops-alerts
```

**Validation errors:**

- `"Alias name must be 2-50 characters, lowercase letters, digits, and hyphens only."` — invalid
  format
- `"Alias 'ops-alerts' is already assigned to a different conversation."` — alias exists elsewhere

---

### create-alias

Opens an interactive Adaptive Card form for creating a new alias. The card prefills the alias name
from the channel or chat name when possible, making it quicker to set up standard aliases.

The card includes:

- **Alias name** text input (prefilled, editable)
- **Description** text input
- **Submit** button

This command provides the same functionality as `set-alias` but with a guided form experience.

---

### remove-alias `<name>`

Deletes the specified alias. The alias must be assigned to the current conversation.

**Example:**

```
@<bot-display-name> remove-alias ops-alerts
```

**Response:**

```
Alias ops-alerts removed.
```

**Errors:**

- `"Alias 'ops-alerts' not found."` — alias does not exist
- `"Alias 'ops-alerts' belongs to a different conversation."` — alias exists but is not assigned here

---

### list-aliases

Displays all registered aliases as an Adaptive Card. Each alias entry shows:

| Field | Description |
|-------|-------------|
| Alias name | The alias identifier |
| Target type | `channel`, `personal`, or `groupChat` |
| Description | User-provided description |
| Created by | User who created the alias |
| Created | Relative timestamp (e.g., "3 days ago") |
| Notify URL | Full endpoint URL for the `/v1/notify/{alias}` endpoint |

The card is sorted alphabetically by alias name.

---

## 4. Queue Operations

These commands manage poison queue messages — messages that failed processing after 5 consecutive
attempts. Poison queue monitoring is important for identifying delivery issues and ensuring no
notifications are silently lost.

### queue-status

Shows the current message count for all queues.

**Example response:**

```
Queue Status:
  notifications:          0
  notifications-poison:   2
  botoperations:          0
  botoperations-poison:   0
```

A warning indicator is displayed next to any poison queue that contains messages.

---

### queue-peek `<queue>` `[count]`

Preview messages in a poison queue without removing them.

**Arguments:**

| Argument | Required | Default | Description |
|----------|----------|---------|-------------|
| `queue` | Yes | — | Queue name: `notifications-poison` or `botoperations-poison` |
| `count` | No | 5 | Number of messages to preview (max 32) |

**Example:**

```
@<bot-display-name> queue-peek notifications-poison 3
```

**Response:**

Each message is displayed as a card showing the message content (truncated to 200 characters),
enqueue time, and dequeue count.

**Errors:**

- `"Invalid queue name. Use 'notifications-poison' or 'botoperations-poison'."` — unrecognized
  queue

---

### queue-retry `<queue>` `[count]`

Moves messages from a poison queue back to the source queue for reprocessing. Messages are moved
in FIFO order.

**Arguments:**

| Argument | Required | Default | Description |
|----------|----------|---------|-------------|
| `queue` | Yes | — | Queue name: `notifications-poison` or `botoperations-poison` |
| `count` | No | 1 | Number of messages to retry |

**Example:**

```
@<bot-display-name> queue-retry notifications-poison 5
```

**Response:**

```
Moved 5 message(s) from notifications-poison to notifications.
```

---

### queue-retry-all `<queue>`

Moves all messages from a poison queue back to the source queue. Use with caution — if the
underlying issue has not been resolved, messages will fail again and return to the poison queue.

**Example:**

```
@<bot-display-name> queue-retry-all notifications-poison
```

**Response:**

```
Moved 12 message(s) from notifications-poison to notifications.
```

---

## 5. Other Commands

### setup-guide

Displays an Adaptive Card with step-by-step instructions for configuring API authentication. The
card includes:

- Audience and scope values for token acquisition
- Required app role (`Notifications.Send`)
- A curl example for sending a test notification
- Webhook configuration instructions for Azure Monitor Action Groups

This is a convenient reference for platform engineers onboarding to the notification API.

---

### delete-post

Delete a message previously sent by the bot. To use this command, reply to the bot's message in a
team channel thread and type `delete-post`.

**Constraints:**

- Only works in **team channels** (not personal or group chat)
- Only deletes messages **sent by the bot** — cannot delete user messages
- Must be sent as a **reply** in the thread containing the target message

**Example:**

In a channel thread where the bot posted a notification, reply with:

```
@<bot-display-name> delete-post
```

**Response:**

```
Message deleted.
```

---

## 6. Command Availability by Scope

Not all commands are available in every conversation scope. Queue management commands that modify
data are restricted to team channels and personal chat where administrative access is appropriate.

| Command | Team | Personal | Group Chat |
|---------|:----:|:--------:|:----------:|
| help | Yes | Yes | Yes |
| checkin | Yes | Yes | Yes |
| set-alias | Yes | Yes | Yes |
| create-alias | Yes | Yes | Yes |
| remove-alias | Yes | Yes | Yes |
| list-aliases | Yes | Yes | Yes |
| setup-guide | Yes | Yes | Yes |
| queue-status | Yes | Yes | -- |
| queue-peek | Yes | Yes | -- |
| queue-retry | Yes | -- | -- |
| queue-retry-all | Yes | -- | -- |
| delete-post | Yes | -- | -- |

**Legend:** `Yes` = available, `--` = not available in this scope.

**Rationale:**

- `queue-retry` and `queue-retry-all` are restricted to team channels because they modify queue
  state and should be performed by team members with visibility into the channel context.
- `delete-post` requires a channel thread context to identify the target message.
- `queue-status` and `queue-peek` are read-only and available in both team and personal scopes for
  monitoring convenience.

---

## 7. Proactive Notifications

In addition to responding to commands, the bot delivers proactive notifications when messages arrive
through the API. These appear in the conversation associated with the target alias.

### Text notifications

Plain text messages are delivered as-is in the conversation. The message appears as a standard bot
message.

### Adaptive Card notifications

Custom Adaptive Card payloads are rendered inline in the conversation. Cards support rich
formatting, images, action buttons, and other elements defined by the
[Adaptive Cards schema](https://adaptivecards.io/).

### Azure Monitor alert cards

Alerts received through the `/v1/alert/{alias}` endpoint are rendered as structured Adaptive Cards
with color-coded severity indicators:

| Severity | Color | Use case |
|----------|-------|----------|
| Sev0 | Red | Critical — immediate action required |
| Sev1 | Red | Error — urgent attention needed |
| Sev2 | Yellow | Warning — investigate soon |
| Sev3 | Blue | Informational |
| Sev4 | Blue | Verbose |

The card displays the alert rule name, severity, signal type, fired time, and affected resources
extracted from the Common Alert Schema payload.

### Poison queue alert cards

When messages fail processing and land in a poison queue, the bot can deliver a warning card to
a designated monitoring conversation. These cards include the queue name, message count, and
a prompt to investigate using the [queue commands](#4-queue-operations).

---

## See Also

- [API Reference](api-reference.md) — HTTP endpoints for sending notifications programmatically
- [Authentication](authentication.md) — Identity model and token acquisition
- [Access & Roles](access-and-roles.md) — Permissions and role assignments
- [Deployment Guide](deployment-guide.md) — End-to-end deployment walkthrough
