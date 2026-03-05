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

The `Notifications.Send` app role must be assigned to the calling service principal or user in
Entra ID. Requests without a valid token or without the required role receive a `401` or `403`
response.

### Token acquisition (Azure CLI)

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
| `Authorization` | Yes (except `/health`, `/v1/openapi.yaml`) | `Bearer <token>` — Entra ID access token with `Notifications.Send` role |
| `Content-Type` | Yes (POST requests) | Must be `application/json` |
| `Idempotency-Key` | No | Client-generated deduplication key. See [Idempotency](#7-idempotency). |

### Response Headers

| Header | Description |
|--------|-------------|
| `X-Correlation-Id` | Unique correlation identifier for the request. Include this value when reporting issues. |
| `Retry-After` | Seconds to wait before retrying. Present on `429` responses. |
| `Content-Type` | `application/json` for all JSON responses, `application/yaml` for OpenAPI spec. |

---

## 4. Rate Limiting

The API enforces per-principal rate limits to prevent abuse:

| Parameter | Value |
|-----------|-------|
| Window | 60 seconds (fixed) |
| Max requests | 60 per authenticated principal |
| Scope | All endpoints combined |

When the limit is exceeded, the API returns `429 Too Many Requests` with a `Retry-After` header
indicating the number of seconds to wait before retrying.

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

All error responses follow the [RFC 7807](https://datatracker.ietf.org/doc/html/rfc7807)
Problem Details format:

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
| 401 | Unauthorized — missing or invalid Bearer token |
| 403 | Forbidden — valid token but missing required role or feature disabled |
| 404 | Not Found — unknown alias or endpoint |
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
| `metadata` | object | No | Key-value pairs (string values only) attached to the message for tracing |

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

**Errors**: 400, 401, 404, 413, 415, 429

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

Send a message directly to a specific Teams channel, personal chat, or group chat by providing
the target type and IDs. This endpoint bypasses alias resolution.

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
| `target.teamId` | string | Conditional | Required for `channel` type |
| `target.channelId` | string | Conditional | Required for `channel` type |
| `target.userId` | string | Conditional | Required for `personal` type |
| `target.chatId` | string | Conditional | Required for `groupChat` type |
| `message` | string | Yes | Message content (plain text or Adaptive Card JSON string) |
| `format` | string | No | `"text"` (default) or `"adaptive-card"` |
| `metadata` | object | No | Key-value pairs (string values only) attached to the message for tracing |

**Response — 202 Accepted**

Same format as [POST /v1/notify/{alias}](#post-v1notifyalias).

**Errors**: 400, 401, 429

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

To prevent duplicate message delivery, callers can include an `Idempotency-Key` header with a
unique client-generated value (e.g., a UUID).

```bash
curl -s -X POST \
  "https://<function-app-name>.azurewebsites.net/api/v1/notify/ops-alerts" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: deploy-run-42-notification" \
  -d '{"message": "Deploy complete.", "format": "text"}'
```

**Behavior:**

- If the same `Idempotency-Key` is sent again, the API returns the cached response from the
  original request (same `messageId`, `correlationId`, and status code).
- The key is scoped per operation type (e.g., `notify`). The same key used across different
  aliases within the same operation type will match.
- If no `Idempotency-Key` header is provided, every request is processed independently.
- Idempotency records are stored in Azure Table Storage. There is currently no automatic
  expiry — keys persist until manually cleaned up.

**Recommended key formats:**

| Use case | Example key |
|----------|-------------|
| CI/CD pipeline | `pipeline-<run-id>-<stage>` |
| Scheduled job | `daily-report-2026-01-15` |
| Alert forwarding | `alert-<alert-id>` |

---

## 8. Size Limits

| Constraint | Limit |
|------------|-------|
| Maximum request body | 28 KB |
| Alias name length | 2 -- 50 characters |
| Alias name format | Lowercase letters, digits, hyphens. Must start and end with a letter or digit. |
| Metadata keys | String keys, string values only |
| Idempotency key length | 1 -- 256 characters |

Requests exceeding the 28 KB body limit receive a `413 Payload Too Large` response.

---

## See Also

- [Authentication](authentication.md) — Identity model, token acquisition, and trust boundaries
- [Access & Roles](access-and-roles.md) — RBAC assignments and permission reference
- [Bot Commands](bot-commands.md) — Interactive bot commands available in Teams
- [Deployment Guide](deployment-guide.md) — End-to-end deployment walkthrough
