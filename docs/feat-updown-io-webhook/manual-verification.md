# Manual verification plan: updown.io webhook ingress

A curl-driven runbook to verify the ingest feature against a **local** (`func host start` +
Azurite) or **deployed** instance. Companion to [design.md](./design.md) /
[implementation-plan.md](./implementation-plan.md).

The ingest route is **anonymous** — unlike `/api/v1/notify`, no EasyAuth headers are needed. That
is exactly what makes these checks runnable with plain curl.

---

## 0. Prerequisites

```bash
# Local
cd src/TeamsNotificationBot
./setup-local.sh offline
azurite --silent --location /tmp/azurite --skipApiVersionCheck &
func host start                      # http://localhost:7071
BASE=http://localhost:7071

# Deployed
BASE=https://<function-app-name>.azurewebsites.net
```

In **offline** local mode, delivery to Teams is simulated (`TEAMS_INTEGRATION_DISABLED`), so a
"success" means: `200` returned + a message enqueued on `notifications` (verify with Azurite /
`queue-status`) — not an actual Teams card. For end-to-end Teams delivery use `online` mode or a
deployed instance bound to a real channel.

---

## 1. Create a webhook token (in Teams)

In the target Teams channel, message the bot:

```
create-webhook updown
```

Expect a reply with a **one-time URL** and a short **id**:

```
https://<host>/api/v1/ingest/updown/<TOKEN>
```

Export it:

```bash
TOKEN='<paste TOKEN>'
URL="$BASE/api/v1/ingest/updown/$TOKEN"
```

> Local without Teams: create a row directly, or use a test seam if the build provides one. Note
> the token is stored **only as SHA-256** — you cannot recover it from storage, only from the reply.

---

## 2. Positive: each event type

Each payload is an **array** (updown always sends an array). `Content-Type: application/json`.
Expect **HTTP 200** and one enqueued `adaptive-card` message (unless the event is filtered out).

### 2a. check.down  (red card, "down since", reason)

```bash
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$URL" -H 'Content-Type: application/json' -d '[{
  "event": "check.down",
  "time": "2026-07-01T10:48:48Z",
  "description": "DOWN: https://updown.io/ since 10:38:48 (UTC), reason: 418 I'"'"'m a teapot",
  "check": { "token": "xyz0", "url": "https://updown.io", "type": "https", "alias": null,
    "uptime": 100.0, "down": true, "down_since": "2026-07-01T10:43:48Z", "up_since": null,
    "error": "418 I'"'"'m a teapot", "period": 30, "last_status": 418, "apdex_t": 0.25 },
  "downtime": { "id": "6a44f090706306086d4e09bc",
    "details_url": "https://updown.io/downtimes/6a44f090706306086d4e09bc",
    "error": "418 I'"'"'m a teapot", "started_at": "2026-07-01T10:38:48Z",
    "ended_at": null, "duration": null, "partial": null }
}]'
```

### 2b. check.up  (green, humanized duration, updown.io downtime link)

```bash
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$URL" -H 'Content-Type: application/json' -d '[{
  "event": "check.up",
  "time": "2026-07-01T10:48:48Z",
  "description": "UP: https://updown.io/ since 10:48:33 (UTC), after being down for 10 minutes",
  "check": { "token": "xyz0", "url": "https://updown.io", "type": "https", "alias": null,
    "down": false, "up_since": "2026-06-01T10:48:48Z", "last_status": 200 },
  "downtime": { "id": "6a44f090706306086d4e09be",
    "details_url": "https://updown.io/downtimes/6a44f090706306086d4e09be",
    "error": "418 I'"'"'m a teapot", "started_at": "2026-07-01T10:38:48Z",
    "ended_at": "2026-07-01T10:48:33Z", "duration": 585, "partial": null }
}]'
```

### 2c. check.ssl_invalid

```bash
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$URL" -H 'Content-Type: application/json' -d '[{
  "event": "check.ssl_invalid",
  "time": "2026-07-01T10:48:48Z",
  "description": "The SSL certificate served by updown.io is not valid (error code 20: unable to get local issuer certificate)",
  "check": { "token": "xyz0", "url": "https://updown.io", "type": "https" },
  "ssl": { "cert": { "subject": "updown.io", "issuer": "Let'"'"'s Encrypt Authority X3 (Let'"'"'s Encrypt)",
    "from": "2018-09-08T21:00:18Z", "to": "2018-12-07T21:00:18Z", "algorithm": "SHA-256 with RSA encryption" },
    "error": "error code 20: unable to get local issuer certificate" }
}]'
```

### 2d. check.ssl_valid

```bash
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$URL" -H 'Content-Type: application/json' -d '[{
  "event": "check.ssl_valid", "time": "2026-07-01T10:48:48Z",
  "description": "The SSL certificate served by updown.io is now valid",
  "check": { "token": "xyz0", "url": "https://updown.io", "type": "https" },
  "ssl": { "cert": { "subject": "updown.io", "issuer": "Let'"'"'s Encrypt Authority X3 (Let'"'"'s Encrypt)",
    "from": "2018-09-08T21:00:18Z", "to": "2018-12-07T21:00:18Z", "algorithm": "SHA-256 with RSA encryption" } }
}]'
```

### 2e. check.ssl_expiration  (warning, days_before_expiration)

```bash
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$URL" -H 'Content-Type: application/json' -d '[{
  "event": "check.ssl_expiration", "time": "2026-07-01T10:48:48Z",
  "description": "The SSL certificate served by updown.io will expire in 7 days",
  "check": { "token": "xyz0", "url": "https://updown.io", "type": "https" },
  "ssl": { "cert": { "subject": "updown.io", "issuer": "Let'"'"'s Encrypt Authority X3 (Let'"'"'s Encrypt)",
    "from": "2018-09-08T21:00:18Z", "to": "2018-12-07T21:00:18Z", "algorithm": "SHA-256 with RSA encryption" },
    "days_before_expiration": 7 }
}]'
```

### 2f. check.ssl_renewed

```bash
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$URL" -H 'Content-Type: application/json' -d '[{
  "event": "check.ssl_renewed", "time": "2026-07-01T10:48:48Z",
  "description": "The SSL certificate served by updown.io was renewed",
  "check": { "token": "xyz0", "url": "https://updown.io", "type": "https" },
  "ssl": {
    "new_cert": { "subject": "updown.io", "issuer": "Let'"'"'s Encrypt Authority X3 (Let'"'"'s Encrypt)",
      "from": "2018-09-08T21:00:18Z", "to": "2019-03-07T21:00:18Z", "algorithm": "SHA-256 with RSA encryption" },
    "old_cert": { "subject": "updown.io", "issuer": "Let'"'"'s Encrypt Authority X3 (Let'"'"'s Encrypt)",
      "from": "2018-09-08T21:00:18Z", "to": "2018-12-07T21:00:18Z", "algorithm": "SHA-256 with RSA encryption" } }
}]'
```

### 2g. check.performance_drop  (DISABLED by default → expect 200, NOT enqueued)

```bash
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$URL" -H 'Content-Type: application/json' -d '[{
  "event": "check.performance_drop", "time": "2026-07-01T10:48:48Z",
  "description": "Apdex of https://updown.io/ dropped 47%",
  "check": { "token": "xyz0", "url": "https://updown.io", "type": "https", "apdex_t": 0.25 },
  "apdex_dropped": "47%",
  "last_metrics": { "2023-03-12T07:00:00Z": { "apdex": 0.51 } }
}]'
```

> To verify `performance_drop` *can* be enabled: `configure-webhook <id> --events check.down,check.up,check.performance_drop`,
> re-POST 2g, confirm it now enqueues.

---

## 3. Negative & edge cases

| # | Command | Expect |
|---|---|---|
| 3a | POST to `$BASE/api/v1/ingest/updown/deadbeefwrongtoken` with a valid body | **404**, nothing enqueued, log shows rejected (hash prefix, no token) |
| 3b | POST malformed JSON: `-d '{not json'` | **200** (stops retries), Error log, nothing enqueued |
| 3c | POST a non-array object `-d '{"event":"check.down"}'` | **200**, Error log, nothing enqueued |
| 3d | POST `-d '[]'` (empty array) | **200**, nothing enqueued |
| 3e | POST unknown event `-d '[{"event":"check.some_future_thing","time":"2026-07-01T10:48:48Z","check":{"url":"https://x"}}]'` | **200**, logged + skipped, nothing enqueued |
| 3f | POST the **same** `check.down` body twice (same token+event+time) | second is **200** but deduped → only one enqueued |
| 3g | POST > body cap (e.g. `python3 -c "print('['+'\"x\":1,'*20000+']')"`) | **413** |
| 3h | Wrong `Content-Type: text/plain` | 415 or graceful reject per handler policy |

```bash
# 3a unknown token
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$BASE/api/v1/ingest/updown/deadbeefwrongtoken" \
  -H 'Content-Type: application/json' -d '[{"event":"check.down","time":"2026-07-01T10:48:48Z","check":{"url":"https://x"}}]'
# 3b malformed
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$URL" -H 'Content-Type: application/json' -d '{not json'
```

---

## 4. Verify enqueue / delivery

```bash
# Bot command (any instance): counts per queue
#   queue-status         → notifications should increment per accepted, non-filtered event
#   queue-peek notifications-poison   → should stay empty (no delivery failures)
```

Local with Azurite: inspect the `notifications` queue via Azure Storage Explorer or the Azurite
REST endpoint; confirm `Format":"adaptive-card"` and a `target` matching the webhook's channel.

Online/deployed with a real bound channel: confirm the **card renders** with correct colour, the
facts (check url as plain text, updown account label, time), the **downtime link clickable only for
`updown.io`**, and the "unverified sender" footer.

---

## 5. Rate limiting

```bash
# Fire > limit quickly from one source; expect 429 + Retry-After after the threshold
for i in $(seq 1 150); do
  curl -sS -o /dev/null -w '%{http_code} ' -X POST "$URL" -H 'Content-Type: application/json' \
    -d '[{"event":"check.down","time":"2026-07-01T10:48:48Z","check":{"url":"https://x","token":"xyz0"}}]'
done; echo
# Expect a run of 200s then 429s. Inspect one 429:
curl -sS -D - -o /dev/null -X POST "$URL" -H 'Content-Type: application/json' -d '[]' | grep -i retry-after
```

Regression: `/api/v1/notify/<alias>` without a token still returns **401** (AAD gate intact).

```bash
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$BASE/api/v1/notify/anything" \
  -H 'Content-Type: application/json' -d '{"message":"x"}'   # → 401
```

---

## 6. Logging / telemetry

- Set `UpdownWebhook__DebugLogPayload=true` (local: `local.settings.json`; deployed: app setting),
  restart, POST 2a, and confirm the **raw body** appears in logs, **sanitized**, tagged with
  correlation id + webhook id — and **no token** in the log line.
- Confirm App Insights `requests` show the ingest URL with the token segment **redacted**
  (`/api/v1/ingest/updown/***`). KQL:
  ```kusto
  requests | where url contains "/api/v1/ingest/updown/" | project timestamp, url, resultCode
  ```
  The `url` must not contain the real token.

---

## 7. Real end-to-end from updown (pre-go-live)

Use updown's built-in test: **<https://updown.io/recipients/test>** — configure the recipient URL
as the create-webhook URL and send a test payload. Confirm a card lands in Teams **and** capture
the **source IP** from our logs (this is the data we need before deciding on IP-allowlist
enforcement — design D6). KQL for source IPs:

```kusto
traces | where message contains "ingest" and message contains "SourceIp" | project timestamp, message
```

---

## Fixtures

The JSON bodies in §2 are the canonical fixtures (verbatim from
<https://updown.io/api#webhooks>, timestamps normalized). Copy them into
`tests/TeamsNotificationBot.Tests/fixtures/updown/` as `check.down.json`, `check.up.json`, etc.,
for the automated tests referenced in the implementation plan.
