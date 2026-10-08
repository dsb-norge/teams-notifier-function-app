# Teams Notification Bot — API Reference

| Field   | Value |
|---------|-------|
| Status  | Active |
| Created | 2026-03-01 |
| Audience | API consumers, platform engineers, monitoring integrations |

---

## 1. Base URL

All API endpoints are served from the Azure Function App:

```
https://<function-app-name>.azurewebsites.net/api
```

Replace `<function-app-name>` with the deployed Function App name for your environment.

---

## 2. Authentication

The API is protected by Entra ID (Azure AD) using OAuth 2.0 Bearer tokens. Callers must acquire a
token with the correct audience and application role before making requests.

| Parameter | Value |
|-----------|-------|
| Authority | `https://login.microsoftonline.com/<tenant-id>` |
| Audience | `api://<api-app-id>` |
| Scope | `api://<api-app-id>/.default` |
| Required role | `Notifications.Send` |

The `Notifications.Send` app role must be assigned in Entra ID to the calling application
identity: a service principal or a managed identity (see Token acquisition below). A request without a valid token gets `401`; one with a valid token but without the role
gets `403`.

The `401` comes from the platform: App Service Authentication (EasyAuth) rejects the request
before it reaches the app, so it has **no** problem+json body and **no** `X-Correlation-Id`
header. Clients should branch on the status code, not the body. As observed on dev:

```http
HTTP/1.1 401 Unauthorized
Content-Length: 0
WWW-Authenticate: Bearer realm="<function-app-name>.azurewebsites.net"
```

The `403` and every other error come from the app, in the format in [§5](#5-error-format). Only
`/api/health`, `/api/v1/openapi.yaml`, `/api/messages` (Bot Framework) and
`/api/v1/ingest/updown/{token}` are reachable without a token.

### Token acquisition (Azure CLI)

`Notifications.Send` is normally an **application** role, assigned to service principals and
managed identities. A user sign-in can't get a token carrying it; Entra ID refuses with
`AADSTS50105`. So `az` has to be signed in **as the calling principal** for this to work:
`az login --service-principal …` for an app registration, `az login --identity` on a host with the
managed identity, or a federated login in CI.

```bash
TOKEN=$(az account get-access-token \
  --resource "api://<api-app-id>" \
  --query accessToken -o tsv)
```

### Token acquisition (client credentials)

```bash
TOKEN=$(curl -s -X POST \
  "https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token" \
  -d "client_id=<caller-app-id>" \
  -d "client_secret=<caller-secret>" \
  -d "scope=api://<api-app-id>/.default" \
  -d "grant_type=client_credentials" \
  | jq -r '.access_token')
```

For a complete guide on setting up authentication, registering callers, and assigning roles, see
[Authentication](authentication.md) and [Access & Roles](access-and-roles.md).

---

## 3. Common Headers

### Request Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes on the Entra ID routes (`/v1/notify`, `/alert`, `/send`, `/checkin`, `/aliases`). Not used on `/health`, `/v1/openapi.yaml`, `/messages` (Bot Framework sends its own JWT) or `/v1/ingest/updown/{token}` (the path token is the credential) | `Bearer <token>` — Entra ID access token with `Notifications.Send` role |
| `Content-Type` | Yes (POST requests) | Must be `application/json` |
| `Idempotency-Key` | No | Client-generated deduplication key on `/v1/notify` and `/v1/send`. See [Idempotency](#7-idempotency). |

### Response Headers

| Header | Description |
|--------|-------------|
| `X-Correlation-Id` | Unique correlation identifier for the request. Include this value when reporting issues. Set on every response the app generates; absent on EasyAuth's `401`, which never reaches the app (see [§2](#2-authentication)). |
| `Retry-After` | Seconds to wait before retrying. Present on `429` responses. |
| `Content-Type` | `application/json` for all JSON responses, `application/yaml` for OpenAPI spec. |

---

## 4. Rate Limiting

The API enforces rate limits to prevent abuse. Two disjoint rules apply:

| Zone | Key | Window / max |
|------|-----|--------------|
| AAD routes (`/v1/notify`, `/alert`, `/send`, `/checkin`, `/aliases`) | authenticated principal (`X-MS-CLIENT-PRINCIPAL-ID`) | 60 s / 60 requests |
| updown ingress (`/v1/ingest/*`) | source IP (`X-Forwarded-For` first hop) | 60 s / 100 requests (default) |

The AAD rule excludes the ingress and `/v1/openapi.yaml`; the ingress is keyed by source IP because
those anonymous requests carry no principal. `/v1/openapi.yaml`, `/health` and `/messages` are not
rate-limited per principal: EasyAuth doesn't check them, so a principal header there could be
forged. Only the request path decides which rule applies; the query string is ignored. When a limit is exceeded, the API returns
`429 Too Many Requests` with a `Retry-After` header indicating the number of seconds to wait before
retrying.

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 12
Content-Type: application/json

{
  "type": "https://httpstatuses.io/429",
  "title": "Too Many Requests",
  "status": 429,
  "detail": "Rate limit exceeded. Try again in 12 seconds.",
  "instance": "/api/v1/notify/ops-alerts"
}
```

---

## 5. Error Format

All error responses from the app follow the [RFC 7807](https://datatracker.ietf.org/doc/html/rfc7807)
Problem Details format. The exception is `401`, which the platform returns without this body
(see [§2](#2-authentication)):

```json
{
  "type": "https://httpstatuses.io/400",
  "title": "Bad Request",
  "status": 400,
  "detail": "Invalid JSON payload.",
  "instance": "/api/v1/notify/my-alias",
  "correlationId": "abc-123-def-456"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `type` | string | URI reference identifying the problem type |
| `title` | string | Short human-readable summary |
| `status` | integer | HTTP status code |
| `detail` | string | Human-readable explanation specific to this occurrence |
| `instance` | string | The request path that generated the error |
| `correlationId` | string | Same value as `X-Correlation-Id` response header |

### Common Status Codes

| Code | Meaning |
|------|---------|
| 202 | Accepted — message queued for delivery |
| 400 | Bad Request — invalid JSON, missing required fields, or validation failure |
| 401 | Unauthorized — missing or invalid Bearer token (returned by EasyAuth, no problem+json body) |
| 403 | Forbidden — valid token but missing required role or feature disabled |
| 404 | Not Found — unknown alias or endpoint, or an alias whose conversation the bot no longer has (the bot was removed from that team or chat) |
| 409 | Conflict — another request with the same `Idempotency-Key` is still being processed, or `replyTo`/`update` refers to a message sent to another alias |
| 413 | Payload Too Large — request body exceeds 28 KB |
| 415 | Unsupported Media Type — Content-Type is not `application/json` |
| 429 | Too Many Requests — rate limit exceeded |
| 500 | Internal Server Error — unexpected failure |

---

## 6. Endpoints

### POST /v1/notify/{alias}

Send a notification message to the Teams conversation identified by `{alias}`.

**Path parameters**

| Parameter | Type | Description |
|-----------|------|-------------|
| `alias` | string | The alias name (e.g., `ops-alerts`, `deploy-status`) |

**Request body**

```json
{
  "message": "Deployment to production completed successfully.",
  "format": "text",
  "metadata": {
    "environment": "production",
    "pipeline": "release-main"
  }
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `message` | string or object | Yes | Message content. String for `text` format; object for `adaptive-card` format. |
| `format` | string | No | `"text"` (default) or `"adaptive-card"` |
| `metadata` | object | No | Key-value pairs for tracing, logged with the message's delivery events (see [Size Limits](#8-size-limits)). Never put secrets here. |
| `replyTo` | string | No | A `messageId` from an earlier `/v1/notify` to this alias: post in that message's thread. See [Threads and updates](#threads-and-updates). |
| `update` | string | No | A `messageId` from an earlier `/v1/notify` to this alias: replace that message instead of posting. Not together with `replyTo`. |
| `mentions` | array | No | People and tags to mention, placed in the message with `<at>key</at>`. See [Mentions](#mentions). |

A `text` message is rendered as Teams markdown. To show a `*` or `_` literally, escape it with a
backslash (`\*not bold\*`). To show an HTML tag such as `<b>` literally, write `&lt;b&gt;` or
`\<b\>`. A `<` or `>` that forms no tag shows as written. Mentions are placed with `<at>key</at>`
(see [Mentions](#mentions)). These are the characters Teams must receive: inside the JSON request
body each backslash is itself escaped, so `\*not bold\*` is sent as `"\\*not bold\\*"`.

For Adaptive Card payloads, `message` must be a valid Adaptive Card JSON object:

```json
{
  "message": {
    "type": "AdaptiveCard",
    "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
    "version": "1.4",
    "body": [
      {
        "type": "TextBlock",
        "text": "Build Succeeded",
        "weight": "Bolder",
        "size": "Medium"
      },
      {
        "type": "TextBlock",
        "text": "Pipeline `release-main` completed in 4m 32s.",
        "wrap": true
      }
    ]
  },
  "format": "adaptive-card"
}
```

**Response — 202 Accepted**

```json
{
  "status": "queued",
  "messageId": "msg-a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "correlationId": "corr-12345678-abcd-ef01-2345-678901234567",
  "timestamp": "2026-01-15T14:30:00.000Z"
}
```

**Errors**: 400, 401, 404, 409, 413, 415, 429

A `404` means either that the alias doesn't exist or that the bot no longer has the conversation
it points to; the `detail` says which. Both are checked before the message is queued.

#### Threads and updates

`replyTo` and `update` let a caller keep one thread per incident: open it with a post, add to it
with replies, and mark it resolved by updating the first post. Both take a `messageId` that an
earlier `/v1/notify` to the **same alias** returned (`409` otherwise).

| The referenced message | `replyTo` | `update` |
|---|---|---|
| Was delivered | Posted in its thread (`postedAs: "reply"`) | Replaced in place (`postedAs: "update"`) |
| Is still queued | Held until it is delivered or failed, at most 10 minutes, then as below | Held the same way |
| Failed | Posted as a new message (`postedAs: "post"`) | Fails, with nothing posted |
| Is unknown or expired | Posted as a new message | `404` |

- A reply goes to the conversation the referenced message went to, even if the alias has been
  repointed since. Replying to a reply stays in the same thread.
- Chats have no threads: a reply to a message in a personal or group chat is an ordinary message
  in that chat (`postedAs: "post"`). `update` works in channels and chats.
- An update replaces the whole message, and may change its format. It gets its own `messageId`;
  further replies and updates should keep referring to the original.
- A reply that fell back to a new message starts a new thread: refer to it for later replies.
  `GET /v1/messages/{messageId}` shows which happened.
- Teams can refuse an update sent at the same moment as a reply in the same message's thread
  (seen as a `404` from Teams). The update is then retried about 30 seconds later, and its status
  stays `queued` until then. Sending the update once the reply is `delivered` avoids the wait.

#### Mentions

`mentions` declares the people and tags a message mentions, and the message places each one with
`<at>key</at>`:

```json
{
  "format": "text",
  "message": "Apply failed after merge. <at>merger</at>, please take a look. <at>oncall</at>",
  "mentions": [
    { "key": "merger", "id": "jane.doe@example.com", "name": "Jane Doe" },
    { "key": "oncall", "tag": "<tag ID>", "name": "On call" }
  ]
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `key` | Yes | What the `<at>key</at>` placements refer to: 1–64 letters, digits, `.`, `_` and `-`, unique in the array. |
| `id` | `id` or `tag` | A person: their Entra object ID (a GUID) or UPN. Not their mail address (see below). |
| `tag` | `id` or `tag` | A tag: its ID, as Microsoft Graph returns it ([teamworkTag](https://learn.microsoft.com/graph/api/resources/teamworktag)). A tag ID seen in the Teams client is not one, and a Graph ID can't be built from it. Get it with the bot's [`ids`](bot-commands.md#ids-person-tag) command, which shows the ID Teams sends when you mention the tag, or from Graph. |
| `name` | For a tag | A tag's display name. For a person, the plain text shown if they aren't in the roster; without it, the `id` is shown. 1–256 characters, without `<`, `>` or control characters. |

- **Placement.** Every `<at>…</at>` in the message must enclose a declared key exactly, and every
  key must be placed at least once, or the request is rejected with `400`. Teams shifts mentions
  onto the wrong people when an `<at>` tag has no matching mention, so none reaches it.
  Placements are matched in the raw text: `&lt;at&gt;` is just text. In a card, placements are
  found in every string value. A key may be placed more than once, and the placements needn't
  follow the order of `mentions`: the bot builds one mention per placement, in the order they
  appear, which is how Teams pairs them.
- **Name people by object ID or UPN, never by mail address.** The two often differ (a UPN can
  be an employee number while mail is `first.last@…`), and a mail address that isn't also the
  UPN finds nobody: the person is written as plain text and listed in `unresolvedMentions`. Only
  the object ID is permanent; a UPN changes only when an administrator renames the account, mail
  more readily. Prefer the object ID where you have it.
- **People** are checked against the roster just before posting: the team's for a channel (for a
  private channel, the channel's own members, so someone in the team but not the channel is
  written as plain text and reported), the
  chat's members for a group chat. A person in the roster is mentioned under the roster's display
  name, so the name shown is always the person pinged. A person who isn't is written as plain
  text (`name`, else `id`) and listed in the message's
  [`unresolvedMentions`](#get-v1messagesmessageid). Rosters are cached for 5 minutes, so someone
  just added to the team can take that long to become mentionable.
- **Tags** aren't checked: that would need Microsoft Graph, which the bot doesn't use. Microsoft
  documents that Teams rejects a message that mentions a tag the team doesn't have; the bot then
  posts it again with the tags as plain text and lists them in `unresolvedMentions`. Microsoft
  also documents that tag mentions aren't supported in private and shared channels
  ([Teams docs](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/conversations/channel-and-group-conversations#work-with-mentions)). Teams limits tag mentions to 2 messages per 5 seconds and 5 per minute
  in a thread; the bot doesn't enforce that, and a throttled burst is delayed, not lost.
- **Where.** People and tags work in channels, people in group chats (a tag there is `400`), and
  nothing in personal chats (`400`). A reply or an update is checked against the conversation it
  goes to: once the referenced message was delivered, that message's. If the alias is repointed
  while the message waits in the queue, what its new conversation can't take is written as plain
  text and listed in `unresolvedMentions`; the message isn't failed for it.
- **Cards.** A card that uses `mentions` can't carry mention entities of its own in
  `msteams.entities` (`400`): the bot adds them. A card without `mentions` is forwarded
  unchanged, as before, and its entities aren't checked.
- **Limits.** At most 20 people and 10 tags per message, and at most 30 placements in all (a key
  placed twice counts twice).
- Replies and updates take `mentions` too; an update resolves them again.

**Example**

```bash
curl -s -X POST \
  "https://<function-app-name>.azurewebsites.net/api/v1/notify/ops-alerts" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"message": "Server rebooted successfully.", "format": "text"}'
```

---

### POST /v1/alert/{alias}

Receive an Azure Monitor alert and deliver it as a formatted Adaptive Card to the conversation
identified by `{alias}`. The request body must conform to the
[Common Alert Schema](https://learn.microsoft.com/en-us/azure/azure-monitor/alerts/alerts-common-schema).

**Path parameters**

| Parameter | Type | Description |
|-----------|------|-------------|
| `alias` | string | The alias name |

**Request body** (Common Alert Schema)

```json
{
  "schemaId": "azureMonitorCommonAlertSchema",
  "data": {
    "essentials": {
      "alertId": "/subscriptions/<sub-id>/providers/Microsoft.AlertsManagement/alerts/<alert-id>",
      "alertRule": "High CPU on web servers",
      "severity": "Sev1",
      "monitorCondition": "Fired",
      "monitoringService": "Platform",
      "signalType": "Metric",
      "firedDateTime": "2026-01-15T14:25:00.000Z",
      "resolvedDateTime": null,
      "description": "CPU usage exceeded 90% for 5 minutes.",
      "alertTargetIDs": [
        "/subscriptions/<sub-id>/resourceGroups/<rg-name>/providers/Microsoft.Compute/virtualMachines/<vm-name>"
      ]
    },
    "alertContext": {}
  }
}
```

The bot renders alerts as color-coded Adaptive Cards based on severity:

| Severity | Color |
|----------|-------|
| Sev0, Sev1 | Red (Attention) |
| Sev2 | Yellow (Warning) |
| Sev3 | Blue (Accent) |
| Sev4 and others | Green (Good) |

**Response — 202 Accepted**

Same format as [POST /v1/notify/{alias}](#post-v1notifyalias).

**Errors**: 400, 404, 415, 429

> **Note:** This endpoint is typically called by an Azure Monitor Action Group configured with
> Entra ID (AAD) authentication, not invoked manually. See the
> [Azure Monitor webhook documentation](https://learn.microsoft.com/en-us/azure/azure-monitor/alerts/action-groups)
> for configuration details.

---

### POST /v1/checkin/{alias}

Send a lightweight health-check message to the conversation identified by `{alias}`. Useful for
smoke tests and scheduled uptime verification.

**Path parameters**

| Parameter | Type | Description |
|-----------|------|-------------|
| `alias` | string | The alias name |

**Request body** (optional)

```json
{
  "source": "smoke-test"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `source` | string | No | Identifier for the caller or test suite |

If the request body is omitted or empty, the check-in proceeds with no source label.

**Response — 202 Accepted**

```json
{
  "status": "queued",
  "messageId": "msg-b2c3d4e5-f6a7-8901-bcde-f12345678901",
  "correlationId": "corr-23456789-bcde-f012-3456-789012345678",
  "timestamp": "2026-01-15T14:35:00.000Z",
  "version": "1.0.0"
}
```

**Errors**: 404, 429

---

### POST /v1/send

Send a message directly to a specific Teams channel, a person, or a group chat by providing the
target type and IDs. This endpoint bypasses alias resolution.

**Request body**

```json
{
  "target": {
    "type": "channel",
    "teamId": "<team-guid>",
    "channelId": "19:<channel-thread-id>@thread.tacv2"
  },
  "message": "Direct message content.",
  "format": "text"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `target` | object | Yes | Target specification (see below) |
| `target.type` | string | Yes | `"channel"`, `"personal"`, or `"groupChat"` |
| `target.teamId` | string | Conditional | The team's Entra group ID. Required for `channel` type; for `personal`, optionally limits the search for the person to this team. |
| `target.channelId` | string | Conditional | Required for `channel` type |
| `target.userId` | string | Conditional | Required for `personal` type: the person's Entra object ID (a GUID) or UPN. See [Direct messages](#direct-messages). |
| `target.chatId` | string | Conditional | Required for `groupChat` type |
| `message` | string | Yes | Message content (plain text or Adaptive Card JSON string) |
| `format` | string | No | `"text"` (default) or `"adaptive-card"` |
| `metadata` | object | No | Key-value pairs for tracing, logged with the message's delivery events (see [Size Limits](#8-size-limits)). Never put secrets here. |
| `update` | string | No | A `messageId` from an earlier `/v1/send` to the same target: replace that message instead of posting. |

**Response — 202 Accepted**

Same format as [POST /v1/notify/{alias}](#post-v1notifyalias).

**Errors**: 400, 401, 404 (`update` of an unknown or expired message, or of one whose conversation the bot no longer has), 409, 429

Takes an `Idempotency-Key` like `/v1/notify`; the key is scoped to the caller and the whole target.

#### Direct messages

```json
{
  "target": { "type": "personal", "userId": "jane.doe@example.com" },
  "format": "text",
  "message": "Apply failed in production."
}
```

- `userId` is the person's Entra object ID or UPN, not their mail address (see
  [Mentions](#mentions) for why). The bot uses its stored one-to-one
  conversation with them if it has one. Otherwise it looks for them in the rosters of the teams
  it is installed in (the first team that has them wins, or only `target.teamId` when given),
  starts a one-to-one chat from there, and stores it for next time. The person doesn't need to
  have installed the app, and the bot needs no Microsoft Graph permission.
- A UPN is found through team rosters only. Someone who installed the app personally but shares
  no team with the bot is reached by object ID, which their stored chat is kept under.
- Someone in no roster is a permanent failure: the message is `failed` at once, without retries
  or a poison-queue alert, with the reason in `error`. The lookup happens at delivery, so the
  request itself gets `202`.
- The message's `target` in [`GET /v1/messages/{messageId}`](#get-v1messagesmessageid) shows the
  person's object ID, whichever form the request used.
- One request is one message to one person; send one request per recipient.

#### Updates

`update` replaces a message an earlier `/v1/send` posted, with the rules of
[Threads and updates](#threads-and-updates) on `/v1/notify`: an update to a message still queued
waits for it, one to a message that failed fails, and an unknown or expired `messageId` is `404`.
The update must name the **same target** as the original, or it is `409`: the same type and IDs,
with `userId` compared without regard to case. A person named by UPN the first time must be named
by the same UPN again, not by object ID. A person's `target.teamId` isn't compared: it only
narrows the search, and an update goes where the original went. There is no `replyTo` on
`/v1/send`; for a person or a group chat there are no threads to reply in.

---

### POST /v1/ingest/updown/{token}

**Anonymous** ingress for [updown.io](https://updown.io) webhooks. Unlike every other `/v1/*`
endpoint, this route is **not** Entra ID-gated — authentication is the high-entropy `{token}` in the
path, which maps to a single conversation target. Create the URL with the **create-webhook** bot
command (see [Bot Commands](bot-commands.md)); the token is shown once and only its SHA-256 is
stored. updown does not sign its requests, so the URL is a bearer secret: keep it out of logs and
**rotate-webhook** if it leaks.

**Path parameters**

| Parameter | Type | Description |
|-----------|------|-------------|
| `token` | string | The secret capability token issued by `create-webhook`. |

**Request body** — updown always sends an **array** of events. Fields are parsed leniently
(unknown fields ignored; unknown/future event types are accepted and skipped):

```json
[{
  "event": "check.down",
  "time": "2026-07-01T10:48:48Z",
  "description": "DOWN: https://example.com since 10:38 (UTC), reason: 500",
  "check": { "token": "xyz0", "url": "https://example.com", "alias": "prod-site", "down": true },
  "downtime": { "details_url": "https://updown.io/downtimes/...", "started_at": "2026-07-01T10:38:48Z" }
}]
```

Recognised `event` types: `check.down`, `check.up`, `check.ssl_invalid`, `check.ssl_valid`,
`check.ssl_expiration`, `check.ssl_renewed`, `check.performance_drop`. Each webhook has a
configurable event filter (default: all except `check.performance_drop`). Each eligible event is
rendered as a color-coded Adaptive Card and delivered to the bound conversation.

**Behaviour**

- Returns **200 OK** on success (updown ignores the body). Retries are de-duplicated on
  `(check token, event, time)`.
- **Malformed or unparseable** bodies are logged and answered **200** (a bad body will not parse on
  retry — this avoids updown's up-to-25× retry storm). Only a transient enqueue failure returns
  **5xx** so a genuinely retryable delivery is retried.
- Unknown token → **404**. Body over the size cap → **413**.
- **Source-IP allowlist:** the endpoint accepts requests only from updown's published IPs
  (`ips.updown.io`) when the `UpdownWebhook__IpFilterMode` setting is `enforce` — otherwise a
  non-updown source IP gets **403**. Default is `log-only` (logs but does not block); `off` disables
  the check. An empty/unresolved list never blocks (fail-safe). See design §17 and the
  `show-ip-allow-list` / `update-ip-allow-list` bot commands.
- Cards contain **no clickable actions** and are labelled *unverified sender*; the only link is the
  updown.io downtime URL (rendered only when it is under `https://updown.io/`).

**Responses**

| Code | Meaning |
|------|---------|
| 200 | Accepted (events enqueued and/or skipped; also returned for unparseable bodies) |
| 403 | Source IP not in the updown allowlist (only when `IpFilterMode=enforce`) |
| 404 | Unknown webhook token |
| 413 | Body exceeds the size cap |
| 429 | Rate limit exceeded (per source IP) |
| 500 | Transient enqueue failure (updown will retry) |

**Example**

```bash
curl -sS -X POST \
  "https://<function-app-name>.azurewebsites.net/api/v1/ingest/updown/<token>" \
  -H "Content-Type: application/json" \
  -d '[{"event":"check.down","time":"2026-07-01T10:48:48Z","check":{"url":"https://example.com","token":"xyz0"}}]'
```

> **Testing:** updown's *recipients → test* page (<https://updown.io/recipients/test>) sends a
> sample payload to the URL — use it to verify delivery before going live. See
> [local-development.md](local-development.md) for a local curl runbook and
> [troubleshooting.md](troubleshooting.md) for the go-live (`log-only` → `enforce`) sequence.

---

### GET /v1/messages/{messageId}

What happened to a message the API queued, and where it went. Works for every `messageId` a
`POST` route returned (`/v1/notify`, `/v1/alert`, `/v1/checkin`, `/v1/send`).

**Response — 200 OK**

```json
{
  "messageId": "msg-a1b2c3d4e5f67890abcdef1234567890",
  "status": "delivered",
  "postedAs": "post",
  "target": {
    "type": "channel",
    "teamId": "<team-guid>",
    "channelId": "19:<channel-thread-id>@thread.tacv2",
    "userId": null,
    "chatId": null
  },
  "unresolvedMentions": [],
  "enqueuedAt": "2026-01-15T14:30:00.000Z",
  "deliveredAt": "2026-01-15T14:30:02.000Z",
  "error": null
}
```

| Field | Description |
|-------|-------------|
| `status` | `queued` (waiting, being sent, or being retried), `delivered`, or `failed` |
| `postedAs` | How it was posted: `post`, `reply` (in an earlier message's thread) or `update` (replaced an earlier message). Null until delivered. |
| `target` | The conversation it went to. Null until delivered. Check it to confirm that an alias still points where you expect. |
| `unresolvedMentions` | The `id` of every [mentioned](#mentions) person not found in the roster, and the `tag` of every tag Teams rejected: each was written as plain text. Empty when every mention went through. |
| `error` | Why it failed, when `status` is `failed` |

A message is `failed` when it can't ever be delivered (its alias was removed, it updates a
message that was never delivered, or its recipient is in no team the bot is installed in), or
when every delivery attempt failed and it went to the poison queue. Records are kept for 180 days (the `DeliveryRecords__RetentionDays` app setting,
see [Authentication §7](authentication.md#7-configuration-reference)).

Delivery is at least once. Two copies of a message can't post it at the same time, but if Table
Storage is unavailable right after a send, the message can be posted a second time a few minutes
later, or its status can stay `queued` although its retries ran out. Teams offers no way to check
whether a post already happened, so this can't be reconciled.

Status reads count against the per-principal [rate limit](#4-rate-limiting), so poll sparingly.

**Errors**: 401, 404 (unknown or expired `messageId`), 429

---

### GET /v1/aliases

List all registered aliases. This endpoint is only available when the application setting
`DEBUG_MODE` is set to `true`.

**Response — 200 OK**

```json
{
  "aliases": [
    {
      "alias": "ops-alerts",
      "description": "Operations team alert channel",
      "targetType": "channel",
      "createdBy": "user@example.com",
      "createdAt": "2026-01-10T09:00:00.000Z",
      "notifyUrl": "https://<function-app-name>.azurewebsites.net/api/v1/notify/ops-alerts"
    },
    {
      "alias": "deploy-status",
      "description": "Deployment notifications",
      "targetType": "channel",
      "createdBy": "user@example.com",
      "createdAt": "2026-01-12T11:30:00.000Z",
      "notifyUrl": "https://<function-app-name>.azurewebsites.net/api/v1/notify/deploy-status"
    }
  ]
}
```

**Errors**: 403 (debug mode disabled), 429

---

### GET /health

Liveness probe for monitoring and load balancers. No authentication required.

**Response — 200 OK**

```json
{
  "status": "ok",
  "version": "1.0.0",
  "timestamp": "2026-01-15T14:40:00.000Z"
}
```

This endpoint is always available and does not count toward rate limits.

---

### GET /v1/openapi.yaml

Returns the OpenAPI 3.0 specification for the API in YAML format. No authentication required.

**Response — 200 OK**

```yaml
openapi: "3.0.3"
info:
  title: "Teams Notification Bot API"
  version: "1.0.0"
paths:
  ...
```

---

## 7. Idempotency

To make a retry safe, `POST /v1/notify/{alias}` and `POST /v1/send` take an `Idempotency-Key`
header with a client-generated value (a UUID, or a hash of whatever identifies the event).

```bash
curl -s -X POST \
  "https://<function-app-name>.azurewebsites.net/api/v1/notify/ops-alerts" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: deploy-run-42-notification" \
  -d '{"message": "Deploy complete.", "format": "text"}'
```

**Behavior:**

- **Replay.** A request with a key that already completed returns the original response (same
  status code, `messageId` and `correlationId`) and queues nothing, even if the alias or its
  conversation has gone since. The body is not compared: a different body under the same key still
  gets the original response. Only an invalid request (`400`, `415`) is rejected before the key is
  looked at.
- **Scope.** A key belongs to the calling principal and the target: the alias on `/v1/notify`,
  the whole `target` on `/v1/send`. The same key from another caller, or for another alias or
  target, is a different key.
- **Concurrency.** The key is claimed before the message is queued. A duplicate that arrives
  while the first request is still running gets `409 Conflict`, and should retry after a moment.
- **Failure.** If queuing fails, the claim is released, so a retry with the same key is processed.
- **Expiry.** A key counts for 7 days from its first use (the `Idempotency__ExpiryHours` app
  setting, see [Authentication §7](authentication.md#7-configuration-reference)). After that the
  same key is processed as new. Expired records are deleted daily.
- **Format.** 1 to 256 printable ASCII characters, else `400`. Any printable character is fine,
  including `/`, `#` and `?`. An empty header counts as absent.
- If no `Idempotency-Key` header is provided, every request is processed independently.

**Recommended key formats:**

| Use case | Example key |
|----------|-------------|
| CI/CD pipeline | `pipeline-<run-id>-<attempt>-<stage>` |
| Scheduled job | `daily-report-2026-01-15` |
| Anything long or composite | a SHA-256 hex digest of the parts |

---

## 8. Size Limits

| Constraint | Limit |
|------------|-------|
| Maximum request body | 28 KB |
| Alias name length | 2 -- 50 characters |
| Alias name format | Lowercase letters, digits, hyphens. Must start and end with a letter or digit. |
| Metadata entries | At most 10 |
| Metadata keys | 1 -- 64 characters: letters, digits, `.`, `_`, `-` |
| Metadata values | Strings of at most 256 characters |
| Idempotency key | 1 -- 256 printable ASCII characters |
| Mentions per message | At most 20 people and 10 tags, and 30 placements (`<at>key</at>`) in all |
| Mention keys | 1 -- 64 characters: letters, digits, `.`, `_`, `-` |
| Mention names | 1 -- 256 characters, without `<`, `>` or control characters |

Requests exceeding the 28 KB body limit receive a `413 Payload Too Large` response. A request
whose `metadata` or `mentions` breaks a limit receives `400 Bad Request`.

Each `metadata` entry is copied into the message's delivery events in Application Insights as a
`meta.<key>` property, which is what the limits keep bounded. See
[Troubleshooting](troubleshooting.md#notification-delivery-trail) for querying them.

---

## See Also

- [Authentication](authentication.md) — Identity model, token acquisition, and trust boundaries
- [Access & Roles](access-and-roles.md) — RBAC assignments and permission reference
- [Bot Commands](bot-commands.md) — Interactive bot commands available in Teams
- [Deployment Guide](deployment-guide.md) — End-to-end deployment walkthrough
