# updown.io webhook ingress — refinements & operational-verification findings

Living backlog of refinements discovered **after** the 1.6.0 release, during operational
verification of the feature in the **dev** environment (`func-ikt-ops-teams-notifier-dev`,
sub `ss12-IKT-DEV`). Sources: operator (Peder) manual testing in Teams, Claude's live
Azure/App-Insights verification, and a GitHub Advanced Security / code-quality scan.

Status legend: **OPEN** (needs decision/fix) · **PROPOSED** (fix designed, awaiting go) ·
**FIXED** · **WON'T FIX** (with rationale).

> Nothing here is implemented in app code yet unless marked FIXED. The only code change made
> alongside this doc is the **module hardening** (§M1), which was explicitly greenlit.

---

## Severity summary (most important first)

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| F8 | Source IP is always `::1`/`127.0.0.1` — real client IP never seen | **HIGH** | 1.7.0 verified: **still broken** on Flex — header-dump diagnostic added |
| F9 | Webhook token logged in **cleartext** in App Insights (redaction ineffective) | **HIGH** | 1.7.0 verified: worker traces **redacted ✅**; host `requests.url` **still leaks** (residual) |
| F11 | Outbound Teams send throttled (429) under burst — no Retry-After/backoff | Medium | FIXED (backoff); Retry-After honoring pending deploy-confirm |
| F1 | IP allowlist not populated automatically at boot/deploy | Medium | FIXED |
| F7 | `delete-post` does nothing on quoted / older cards | Medium | FIXED (quote parse); activity-id persistence not done |
| F3 | `create-webhook` doesn't capture (and require) account + description | Medium | FIXED (verified live 1.7.0) |
| F2 | Unexplained webhooks in dev created by `AppValidation-…` identity | Medium (hygiene) | cleanup done; origin narrowed (operational) |
| F5 | `help <command>` doesn't work for individual commands | Low | FIXED |
| F4 | No `show-webhook <id>` command | Low | FIXED (verified live 1.7.0) |
| F6 | `configure-webhook` doesn't show before/after values | Low | FIXED |
| F10 | Card dates render US `MM/DD/YYYY`; times lack a timezone | Low | FIXED (verified live 1.7.0) |
| G* | GHAS / CodeQL / AI scan findings | mostly noise | see §G |

---

## F8 — Source IP is always localhost; real client IP is never seen  **[HIGH — still broken on Flex; discovery in progress]**

> **1.7.0 live re-verification (2026-07-03):** the multi-header fix is deployed, but an external POST
> from `91.229.21.100` **still logs `127.0.0.1`/`::1`** — so none of `X-Forwarded-For` /
> `X-Azure-ClientIP` / `X-Azure-SocketIP` reach the isolated worker on Flex. The real client IP is
> simply not available to the worker in any header we tried. Enforce-mode IP filtering and per-source-IP
> rate limiting therefore **cannot work** (everything keys to one loopback bucket).
>
> **Diagnostic added (this branch):** `LogHeaderDiagnostics` on the ingest handler logs the IP-carrying
> request headers + all header names, gated by `UpdownWebhook__DebugLogHeaders=true` (off by default,
> never logs Authorization/Cookie values). A debug build deployed to dev + one external POST will reveal
> which header (if any) actually carries the client IP.
>
> **Two outcomes:** (a) a header *does* carry it → point `IpMatcher.ClientIpHeaders` at it, done; or
> (b) *no* header carries it on Flex → accept the platform limit: keep IP filtering at `log-only`/`off`
> (don't rely on `enforce`), and **re-key ingest rate-limiting to per-token / webhook-id** instead of
> per-source-IP, with the token as the primary control. Prior code (multi-header read) is unit-tested
> (`IpMatcherTests`, `RateLimitPolicyTests`); the final fix + its test land after discovery.

### Observation
Operator's ingest tests logged `SourceIp=127.0.0.1` and `SourceIp=::1`. Claude then sent a
webhook POST **from a genuine external host** (`91.229.21.100`, via DSB egress) with a bogus
token; App Insights recorded:

```
updown webhook source IP not in allowlist (log-only — not blocked). SourceIp=::1, CorrelationId=ea94…
Rejected updown webhook: unknown token. TokenHashPrefix=1263876f, SourceIp=::1, CorrelationId=ea94…
```

The app logged `::1` — **not** the real client IP `91.229.21.100`.

### Root cause
The source-IP extraction in `UpdownIngestFunction.Run` reads `X-Forwarded-For` then falls back
to the connection's remote address:

```csharp
// src/TeamsNotificationBot/Functions/UpdownIngestFunction.cs:65-67
var xffFirstHop = req.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault();
var sourceIp = IpMatcher.ParseClientIp(xffFirstHop)
               ?? IpMatcher.ParseClientIp(req.HttpContext.Connection.RemoteIpAddress?.ToString())
               ?? "unknown";
```

On **Flex Consumption + .NET isolated worker (ASP.NET Core integration)** the Functions host
proxies the request to the worker over loopback, so `RemoteIpAddress` is `::1`/`127.0.0.1`, and
`X-Forwarded-For` is **not present** in the worker's `HttpRequest.Headers` (if it were, we'd see
`91.229.21.100`). So the code always falls through to the loopback address.

### Impact — this is the important part
Two of the three app-layer defenses that justified opening the site to `0.0.0.0/0` at the network
layer (see the dev-wlzs `allowed_caller_rules` rationale) are **currently ineffective**:

1. **IP allowlist filtering** — in `enforce` mode every request would be seen as `::1`, which is
   never in the updown allowlist, so **all** updown webhooks would be rejected. In the current
   `log-only` default it "works" only because it never blocks. Enforce mode is unusable until
   this is fixed.
2. **Per-source-IP rate limiting** — `RateLimitPolicy.SourceIpKey` uses the same XFF-first logic
   (`src/TeamsNotificationBot/Middleware/RateLimitPolicy.cs:34-39`), so **all** ingest traffic
   keys to a single bucket (`ingest-ip:::1` / `ingest-ip:unknown`) — a global limit, not per-IP.

That leaves only the **per-webhook token** as an effective control. The token has good entropy,
so the endpoint is not wide open — but the defense-in-depth we designed is degraded.

### Proposed fix
1. **Identify the correct header empirically.** Add a one-shot debug that logs the full request
   header set on the ingest endpoint (gated behind the existing `UpdownWebhook__DebugLogPayload`
   or a new `__DebugLogHeaders` flag), deploy to dev, send one external request, read the header
   names. Candidates on App Service/Functions: `X-Forwarded-For`, `X-Azure-ClientIP`,
   `X-Azure-SocketIP`, `X-Client-IP`.
2. **Fix `ParseClientIp` source order** to read whichever header actually carries the client IP,
   with `X-Forwarded-For` first hop as the primary and the Azure `X-Azure-*` headers as fallback.
   Apply the same fix to `RateLimitPolicy.SourceIpKey`.
3. **Re-verify** enforce mode + per-IP rate limiting from an external host before relying on them.
4. **If no header carries the real client IP on Flex** (possible platform limitation): revisit the
   security model — either rate-limit per **token/webhook-id** instead of per-IP, and/or reconsider
   whether `enforce` mode is achievable, and document that the token is the primary control.

### Verified by
Claude, external POST from `91.229.21.100` → logged `::1` (2026-07-02T12:26Z). Microsoft Learn
"IP addresses in Azure Functions" + isolated-worker guide consulted; neither guarantees XFF to the
worker, so empirical header discovery is required.

---

## F9 — Webhook token logged in cleartext in App Insights  **[HIGH — worker-side FIXED; host-side residual]**

> **Resolution (this branch):** `TokenRedactingTelemetryInitializer` now scrubs `TraceTelemetry.Message`
> and any `ISupportProperties` custom properties (e.g. hosting `RequestPath`), in addition to
> `RequestTelemetry.Url`/`Name` — so all **worker-emitted** telemetry (incl. the ASP.NET Core
> "Request starting/finished" traces) is redacted. **Residual:** the host-generated `requests`
> telemetry for an isolated-worker HTTP trigger isn't reachable from a worker initializer; the token
> may still appear in `requests.url`. Verify post-deploy; if it persists, mitigate host-side (host.json
> / RBAC on App Insights read) — tracked as an F9 follow-up. Regression tests added
> (`TokenRedactingTelemetryInitializerTests`).

### Observation (manual-verification §6)
The plan requires the ingest URL's token segment to be redacted to `/api/v1/ingest/updown/***`
in telemetry. It is **not**. App Insights on dev shows the raw token in:

- **`requests.url`**: `https://…/api/v1/ingest/updown/CLAUDE-OPSVERIFY-BOGUSTOKEN-1782995203` (404)
- **`traces`** (ASP.NET Core hosting): `Request starting HTTP/1.1 POST http://localhost:35149/api/v1/ingest/updown/<token>` and `Request finished HTTP/1.1 POST https://…/api/v1/ingest/updown/<token> - 404`

The token is a **bearer secret** (capability URL). Anyone with App Insights read access (broad) can
harvest live tokens and POST arbitrary alerts into the target Teams channels. This is a real secret
exposure — arguably the most serious finding, since the token is the *primary* control (see F8).

### Root cause
`TokenRedactingTelemetryInitializer` (`Helpers/TokenRedactingTelemetryInitializer.cs`) only handles
`RequestTelemetry` (rewrites `.Url`/`.Name`). Two gaps:
1. **It ignores `TraceTelemetry`** — the ASP.NET Core `Microsoft.AspNetCore.Hosting.Diagnostics`
   "Request starting/finished" logs carry the full URL in the message string and are never scrubbed.
2. **HTTP request telemetry is host-generated in the isolated-worker model** — a telemetry
   initializer registered in the *worker's* DI (`Program.cs:152-153`) doesn't apply to telemetry the
   **Functions host** emits, so `requests.url` is never redacted by it. (This matches the observation:
   `requests.url` still shows the raw token.)

### Proposed fix
- **Scrub worker-side traces:** add a `TraceTelemetry` branch to the initializer that runs the same
  regex over `telemetry.Message` (and the `RequestPath`/`{OriginalFormat}` custom properties). And/or
  raise the `Microsoft.AspNetCore.Hosting` log category to `Warning` so the request-logging Info
  traces (which embed the URL) aren't emitted at all — cheaper and also removes the localhost-URL
  noise.
- **Host-side `requests`:** a worker initializer can't fix these. Options: (a) confirm whether the
  Functions host request telemetry can be scrubbed via `host.json` / a host-level filter; (b) failing
  that, treat the URL path as non-secret and move the secret — but updown only allows a fixed URL, so
  the token must stay in the path; (c) mitigate by restricting App Insights read RBAC and documenting
  the residual exposure. Needs a short spike.
- **Regression test:** add an automated test asserting the initializer redacts both `RequestTelemetry`
  and `TraceTelemetry`, and a manual re-check of `requests`/`traces` after the fix.

### Verified by
Claude, App Insights query 2026-07-02 — raw bogus token present in both `requests.url` and hosting
`traces`. Code inspected: initializer only branches on `RequestTelemetry`.

---

## F11 — Outbound Teams send throttled (429) under burst  **[Medium — backoff FIXED; Retry-After pending deploy-confirm]**

### Observation (manual-verification §5, 1.7.0 on dev)
During the burst test, App Insights logged `ReplyToActivity operation returned an invalid status code
'(429) TooManyRequests' - RemoteError: Too many requests., Throttled` (×2). That's **Bot Framework
throttling the bot's outbound sends**. Cards still arrived and **nothing hit the poison queue**, so the
queue-retry absorbed it — but only by luck of a brief throttle.

### Root cause
`SendProactiveAdaptiveCardAsync` / `SendMessageAsync` → `ContinueConversationAsync` → `SendActivityAsync`;
a 429 throws and propagates to `QueueProcessorFunction`, which logs "Failed to deliver" and **rethrows**
(`QueueProcessorFunction.cs:116-121`). The queue then re-delivers on its fixed 30 s visibility timeout,
up to `maxDequeueCount` (5) — **ignoring the BF `Retry-After`**. A sustained throttle (> ~2.5 min) would
exhaust the dequeue budget and **poison the card**.

### Resolution (this branch)
`ThrottleRetry.ExecuteAsync` wraps both outbound sends in `BotService`: on a detected 429 it retries the
whole send (fresh request each attempt — no request-reuse pitfall) with **capped exponential backoff**
(1 s, 2 s, 4 s … ≤ 20 s, up to 4 attempts), then rethrows on a persistent throttle so the queue still
retries later. 429 is detected via `HttpRequestException.StatusCode` or the connector's
"…'(429) TooManyRequests'…" message. Unit-tested (detection incl. inner-exception + message forms,
capped backoff, retry-then-succeed, rethrow-after-max, no-retry-on-non-throttle).

**Pending deploy-confirm:** the current exception surface doesn't expose a parsed `Retry-After`, so we
back off exponentially rather than honoring the exact header. The retry logs `ExceptionType` — a
deployed build will reveal the SDK's real 429 shape and whether a precise `Retry-After` is available to
honor (then we can upgrade backoff → Retry-After). End-to-end confirmation rides on the F8 debug deploy.

---

## F1 — IP allowlist not populated automatically at boot/deploy  **[Medium — FIXED]**

> **Resolution (this branch):** added `UpdownIpAllowlistWarmupService` (`IHostedService`) that runs the
> staleness-gated `GetOrRefreshAsync` at worker startup — populating the list before the first webhook,
> with no bot command or deploy step. It is fire-and-forget (never delays readiness) and best-effort
> (swallows/logs failures; the ingest lazy-refresh still self-heals). Config read shared via
> `UpdownWebhookConfig.AllowlistMaxAge` (used by both the warm-up and the ingest handler). Tests:
> `UpdownIpAllowlistWarmupServiceTests`, `UpdownWebhookConfigTests`. No `app-requirements.json` change
> (not a Function trigger). Chose the startup-warm-up over the "hoist refresh regardless of mode"
> option because the operator's need was boot-time population, not just first-webhook.

### Observation (operator, pics 1–2)
After the 1.6.0 deploy, `show-ip-allow-list updown` reported **Mode: log-only, Entries: 0, Last
refreshed: never**. A manual `update-ip-allow-list updown` populated 22 entries. Operator wants
population to be **automatic** — no bot command, no deploy-step command issuance.

### Root cause
Nothing calls a refresh at startup. Only two call sites populate the list, neither automatic at
boot:
- Manual bot command `update-ip-allow-list` → `RefreshAsync` (`TeamsBotHandler.cs:~790`).
- A **lazy** `GetOrRefreshAsync` inside the ingest handler, but it is gated behind
  `if (ipMode != "off")` and only runs when a webhook actually arrives
  (`UpdownIngestFunction.cs:72-75`). Before the first qualifying request the table row doesn't
  exist.

There is **no** `IHostedService` / startup warm-up (`Program.cs` registers the service as a plain
singleton whose constructor does no I/O), and no timer trigger. `show-ip-allow-list` only reads
(`GetAsync`), so it reports "never" until something writes the row.

### Proposed fix (recommended: (b) + optional (a))
- **(b) primary:** hoist the `GetOrRefreshAsync` call in the ingest handler so it runs on every
  ingest **regardless of `ipMode`** (keep enforcement/rejection gated on mode). First real webhook
  self-populates; staleness-gating (default 48 h) self-heals; DNS already bounded to 5 s; empty-list
  fail-safe means a slow/failed resolve never drops an alert. One-line move, respects per-function
  scaling.
- **(a) optional:** add a best-effort `IHostedService` that calls `GetOrRefreshAsync` (not
  `RefreshAsync`, so staleness-gating avoids repeated resolves across cold starts) on startup —
  **only if** we want the list warm before the *first* webhook (needed for a clean switch to
  `enforce`). Must swallow/log failures so a DNS hiccup can't fail worker startup. Caveat: under
  Flex per-function scaling this runs on every instance's cold start (bounded to a table read when
  fresh).
- **Avoid (c)** a timer-triggered function — adds a new trigger + `app-requirements.json` churn and
  fights the per-function-scaling / no-singleton constraints for no benefit over (b).

Note: F1 depends on **F8** to be meaningful for `enforce` mode (an allowlist you can't match
against is moot).

---

## F7 — `delete-post` does nothing on quoted / older cards  **[Medium — quote-parse FIXED]**

> **Resolution (this branch):** `delete-post` now resolves its target as
> `ExtractQuotedMessageId(Activity.Text)` → `ReplyToId` → thread-root `;messageid=`. Quoting a card
> (the natural gesture for an older post) embeds `<quoted messageId="…">` in the text but doesn't set
> `ReplyToId`, so it previously did nothing; the new `TeamsMessageParsing.ExtractQuotedMessageId`
> parser (unit-tested) picks it up. Guidance updated to "reply to — or quote — a bot message". The
> optional larger enhancement (persist sent activity ids for `delete-post <id>` / "delete last N")
> is **not** done — deferred unless product wants command-driven deletion without a reply/quote.

### Observation (operator, pic 5)
`delete-post` on an Azure Monitor alert card posted by the **previous app version (1.5)**, issued
under 1.6, did nothing. Operator asked whether conversation references are lost across reboots.

### Root cause — NOT a persistence/reboot problem
Conversation references **are** persisted (`conversationreferences` table) and survive restarts and
version changes. The real cause is that `delete-post` is **reply-scoped and stateless w.r.t. the
target card**:
- It deletes the activity identified by the incoming command's `ReplyToId`
  (`TeamsBotHandler.cs:1218`), with a fallback that parses `;messageid=<id>` from the thread
  conversation id (`:1220-1235`). Both come from the *current* turn, not storage.
- The bot **never persists the activity id of cards it posts** — the `ResourceResponse.Id` from
  `SendActivityAsync` is discarded (`BotService.cs:102`), and `ConversationReferenceEntity` has no
  activity-id column. So there's nothing to target an old card by.

App Insights confirms the mechanism precisely:
- 12:03:40Z — operator sent `delete-post` as a **quoted** message
  (`Received message from Teams: <quoted messageId="1782992989940"/>\ndelete-post`) → **no DELETE
  call was issued** (a Teams "quote" does not populate `ReplyToId`).
- 12:06:23Z & 12:10:13Z — `delete-post` sent as a **proper reply** → a real
  `DELETE …/activities/<id>` was issued.

So "does nothing on the old card" = the operator reached the old card via **quote** (it's not the
latest in-thread message), and the handler ignores the `<quoted messageId="…">` payload.

### Proposed fix
- **Primary:** parse the `<quoted messageId="…">` value from the incoming activity (Teams embeds it
  in the message entities/attachments for quoted replies) and use it as an additional target source,
  ahead of the `;messageid=` fallback. This makes "quote the card → delete-post" work, which is the
  natural gesture for an older card.
- **Messaging:** improve the "reply to a bot message" guidance (`TeamsBotHandler.cs:1237-1244`) and
  the delete-failure catch (`:1246-1259`) to mention quoting.
- **Optional (larger):** persist sent activity ids (capture `ResourceResponse.Id` at
  `BotService.cs:102`) to enable `delete-post <id>` / "delete last N alerts" without replying. New
  table + retention concern + `app-requirements.json` change — only if product wants it.

Do **not** frame this as "lost references on reboot" — that's not what happened.

---

## F3 — `create-webhook` doesn't capture / require account + description  **[Medium — FIXED]**

> **Resolution (this branch):** `create-webhook` now requires a keyed grammar
> `create-webhook [updown] account <account> description <description>` (parsed by the unit-tested
> `WebhookCommandParser` from the original text, preserving casing for emails/labels). Both are passed
> into `IWebhookService.CreateAsync` (new `description`/`updownAccount` params) and echoed in the
> confirmation; still editable via `configure-webhook`. Help text updated. Tests: parser (valid,
> optional `updown`, slashed/email account, non-greedy split, usage/source errors) + handler
> (pass-through, explicit `updown`, missing-field rejection). Also added the G4.2 explicit-`updown`
> positive test.

### Observation (operator, pic 4)
Design intended two human-facing free-text fields at creation: **updownAccount** (e.g. the account
email, for tracking multiple updown accounts) and **description**. `create-webhook` currently
captures neither. Operator wants both **required at creation**, still editable via
`configure-webhook`.

### Current state
- `WebhookTokenEntity` **already has** `Description` and `UpdownAccount` fields
  (`WebhookTokenEntity.cs:34,36-37`); the list card already displays them; `configure-webhook`
  already edits them (`WebhookService.ConfigureAsync`). The gap is **creation intake only**.
- `HandleCreateWebhookAsync` (`TeamsBotHandler.cs:517-560`) parses only an optional `source` token
  and calls `CreateAsync(...)` without account/description; `IWebhookService.CreateAsync` /
  `WebhookService.CreateAsync` have no params for them.

### Proposed fix
1. Add `description` + `updownAccount` params to `IWebhookService.CreateAsync` +
   `WebhookService.CreateAsync`; set them on the entity.
2. Rework `HandleCreateWebhookAsync` parsing to require both. Because both are free-text (the
   account can contain `@`/`/`), use a **keyed grammar** parsed from the *original* text (preserve
   casing, like `configure-webhook` does), e.g.
   `create-webhook [updown] account <label> description <text>` — reject with a usage message if
   either is missing/blank. This requires passing `text` (not just `command`) into the handler
   (call site `TeamsBotHandler.cs:~129`).
3. Update the confirmation to echo account + description; update `HelpTextBuilder.Webhooks()` usage
   line; update tests (`TeamsBotHandlerWebhookCommandsTests.cs:75,101,112` construct
   `create-webhook` with no args) + add a missing-field negative test.

Entropy/alias constraints already satisfied (id is 8 hex from `RandomNumberGenerator`; target
derived from conversation context; alias not part of the command).

---

## F2 — Unexplained webhooks in dev (`AppValidation-…` identity)  **[Medium — hygiene, OPEN]**

### Observation (operator, pic 3)
`list-webhooks` showed **5** webhooks. Four were "created by
`AppValidation-20260626-c8687a94-9852-4fb5-93c9-f6a8c3385114`" (ids `ce791d09`, `d07505c0`,
`a433d7ae`, `d75bcc23`). Operator asked: did Claude create these?

### Findings
- **No — Claude did not create them.** Claude never issued a bot command and has no Storage
  data-plane access (confirmed 403 on the table).
- App Insights over **30 days** shows **only two** webhook creations, both by "Schmedling, Leif
  Peder" (`b4c8bad8` at 11:32:31Z, `5ee9199b` at 12:05:07Z). There is **no creation trace** for the
  four `AppValidation-…` webhooks — no `Received message from Teams: create-webhook`, no
  `Webhook '…' created …`.
- The pre-1.6.0 dev app (1.5.1) didn't have this feature, so they can't predate today's deploy;
  yet 1.6.0 logs every bot-driven creation and these aren't logged.

### Conclusion (investigated — operational, no code change)
Follow-up evidence narrows it down:
- **`AppValidation-…` is not an Azure identity** — `az ad sp/app show` on the GUID returns "does not
  exist", and no `AppValidation*` service principal exists. So it's a **synthetic Bot Framework sender
  name** (`from.name`) minted by some tool; the GUID is a run-id, the `20260626` a mint date.
- **The only data-plane writer on the dev storage is the app's own Managed Identity**
  (`5f0ca180-…`, the sole `Storage Table Data Contributor`). So the rows were written **through the
  app** (`WebhookService.CreateAsync`), i.e. by `create-webhook` **bot activities** posted to
  `/api/messages` — not by a direct table write and not by Claude.
- The missing "created" traces are consistent with **App Insights adaptive sampling** (host.json caps
  at 20 items/s) dropping them during a burst.

**Most likely origin:** an automated bot-validation / integration harness that posts synthetic
`create-webhook` activities to the **dev** bot (the name pattern + burst timing fit). `AppValidation`
is in none of the three repos, so it's an external tool/runner.

### Action
- **Clean up:** ✅ done — operator removed the `AppValidation-…` webhooks (and the F9 test webhook).
- **Investigate origin (open, operational):** find the harness posting synthetic activities to the
  dev bot's `/api/messages` and point it at ephemeral infra instead. Watch for recurrence; if it
  reappears, the create is now captured with source IP + correlation (F8/F9 fixes) for tracing.

---

## F5 — `help <command>` doesn't work per-command  **[Low — FIXED]**

> **Resolution (this branch):** added `HelpTextBuilder.CommandHelp(command)` with detailed per-command
> help for every routed command, and `HandleHelpAsync` now consults it before the "unknown topic"
> fallback — so `help configure-webhook`, `help create-webhook`, etc. work. Webhook help spells out
> each argument; the event list + default set are sourced from `UpdownEventTypes` (no drift).
> `Overview()` and the fallback advertise `help <command>`. Tests: per-command help content, a
> completeness guard asserting every routed command has an entry, unknown→null, and handler dispatch.

### Current state
`HandleHelpAsync` (`TeamsBotHandler.cs:186-204`) dispatches only **section topics** (`aliases`,
`endpoints`, `webhooks`, `queues`, `diagnostics`, or overview). `help configure-webhook`,
`help create-webhook`, etc. fall through to "Unknown help topic". **No** command has per-command
help.

### Proposed fix
Extend `HandleHelpAsync` with a command-name lookup (dictionary/switch keyed on the full command
name) consulted before the default fallback, and add per-command help builders to `HelpTextBuilder`
— each spelling out purpose, exact grammar, and **every argument**. For webhook commands
specifically: explain `description | account | events`; for `events`, enumerate **all** of
`UpdownEventTypes.All` and state the default set (**all except `check.performance_drop`**) and the
`all` keyword — **sourced programmatically from `UpdownEventTypes`** so it can't drift (today
`HelpTextBuilder.cs:68-70` hardcodes the list). Advertise `help <command>` in `Overview()` and the
fallback. Include `help help` and `help show-webhook` (F4).

---

## F4 — No `show-webhook <id>` command  **[Low — FIXED]**

> **Resolution (this branch):** added `show-webhook <id>` (routed via `StartsWith`, uses the existing
> `GetByIdAsync`, "not found" when absent). Refactored `WebhookListCardBuilder` — extracted the shared
> per-webhook FactSet into `Facts()` and added `BuildSingle(WebhookDisplayInfo)`; the handler's
> display-info mapping is now the shared `ToDisplayInfo`. Added to `help webhooks`. Tests: handler
> (found→card, not-found, no-id usage) + card builder (single-card content, no secret; list plural
> header). **No app-requirements change** — the Teams manifest command menu is a curated subset
> (`create-webhook` + `list-webhooks` only); `show-webhook` needs an `<id>` arg that doesn't suit a
> menu hint, so it stays discoverable via help, consistent with configure/rotate/remove.

### Current state
Confirmed absent. `list-webhooks` can get long; there's no single-webhook view. `IWebhookService`
already has `GetByIdAsync`. `WebhookListCardBuilder` renders a per-webhook FactSet that is exactly
the single-webhook surface.

### Proposed fix
Route `show-webhook` (via `StartsWith`) → new `HandleShowWebhookAsync`: `EnsureWebhookAccessAsync`,
parse `<id>` (usage error if missing), `GetByIdAsync`, "not found" when null, else render a single
card. Refactor the per-webhook FactSet out of `WebhookListCardBuilder` into a shared helper +
`BuildSingle(WebhookDisplayInfo)`. Add to help.

---

## F6 — `configure-webhook` shows no before/after  **[Low — FIXED]**

> **Resolution (this branch):** `configure-webhook` now reads the entity up front (reporting
> not-found early), captures the old value of the changed field, and confirms
> `✅ Webhook <id> — <field>: before … → after …`, or `ℹ️ … <field> unchanged (…)` when identical.
> Tests: before/after shown, unchanged reported, not-found reported (+ existing configure tests
> updated for the up-front read).

### Current state
Confirmation is one flat line reporting only the field name
(`✅ Webhook **{id}** updated ({field}).`, `TeamsBotHandler.cs:628-630`). No before value, no after
value, no "unchanged" case. `ConfigureAsync` returns only a `bool`.

### Proposed fix
`GetByIdAsync` before mutating; capture old value of the field(s) being changed; emit
`{field}: \`{old}\` → \`{new}\``, and when equal, `{field} unchanged (\`{old}\`)`. Optionally change
`ConfigureAsync` to return the prior entity to avoid the extra read. Update tests
(`TeamsBotHandlerWebhookCommandsTests.cs:194,203`).

---

## F10 — Card dates render US-format; times lack a timezone  **[Low — FIXED]**

> **Resolution (this branch):** the card passed raw ISO-8601 timestamps, which the Teams client
> auto-localizes (ambiguous `MM/DD/YYYY`, no timezone). `UpdownCardBuilder.FormatTimestamp` now
> pre-formats every timestamp (Time, Down since, Recovered at, cert dates) to an explicit
> `yyyy-MM-dd HH:mm:ss UTC` (InvariantCulture, normalized to UTC) — a non-ISO string the client leaves
> alone; unparseable values pass through unchanged. Tests: ISO→UTC (incl. offset normalization),
> invalid/empty passthrough, and the per-event card assertions updated. The minor duration truncation
> (585 s → "9 minutes") is left as-is — defensible ("at least N minutes") and matches existing tests.

### Observation (manual-verification §4, real cards delivered to a Teams PM)
All six event cards rendered correctly (colour, facts, downtime link gated to `updown.io`, "unverified
sender" footer). Two date/time presentation issues:

1. **Dates are `MM/DD/YYYY` (US format).** Source `2026-07-02T…` (2 July) renders as **`07/02/2026`**;
   cert `2018-12-07` renders `12/07/2018`. For a Norwegian audience this is ambiguous/misleading —
   `07/02/2026` reads as *7 February*. Should be ISO 8601 (`2026-07-02`) or `DD.MM.YYYY`.
2. **Times have no timezone.** Cards show bare `10:48:48` (source was UTC `…Z`). No `UTC`/offset shown
   and no conversion to Europe/Oslo — ambiguous.
3. Minor: downtime duration truncates — `585s` rendered "9 minutes" (updown's own description said
   "10 minutes"). Cosmetic; round-to-nearest would match better.

### Proposed fix
Format dates as ISO `yyyy-MM-dd` (or `dd.MM.yyyy`) and render times with an explicit `UTC` suffix (or
convert to Europe/Oslo with an offset) in `UpdownCardBuilder`. Consider `CultureInfo.InvariantCulture`
+ explicit format strings so it doesn't depend on the host locale. Small, contained change.

### Verified by
Operator screenshot of all six cards (2026-07-02); data values correct, formatting as above.

---

## §G — GitHub Advanced Security / code-quality triage  **(Claude's judgment)**

Operator said "you be the judge of what to fix/ignore."

### G1 — "Generic catch clause" (CodeQL, 9 findings) — **WON'T FIX (8) / tidy (1)**
All are intentional best-effort or fail-safe catches with logging, consistent with the codebase's
established pattern:
- `UpdownIpAllowlistService.cs:72` — DNS-resolve fail-safe that **keeps the previous list** and
  records `ResolveError`. Deliberate (design). **Keep.**
- `AuthMiddleware.cs:120` — role parse → returns `false` on any error = **fail-closed** (deny).
  Correct security posture. **Keep.**
- `TeamsBotHandler.cs:1252` (delete), `:1647` (auto-refresh ref), `:420` (channel fetch), `:177`
  (nudge); `BotService.cs:296,419` — all best-effort with `LogWarning`/`LogDebug`. **Keep.**
- `TeamsBotHandler.cs:441` — bare `catch { /* best-effort */ }`. **FIXED** — narrowed to
  `catch (JsonException)` (the method is `static`, so there's no logger; narrowing is the right fix and
  clears the generic-catch finding).

### G2 — "Constant condition … always not null because of IsNullOrEmpty" (CodeQL, 5) — **WON'T FIX**
All five are the same pattern: a `channelData?.Team?.Id` null-conditional feeding
`string.IsNullOrEmpty(...)`. `IsNullOrEmpty` correctly handles null; this is a **CodeQL imprecision**
around null-conditional + `IsNullOrEmpty`, not a real always-true/false bug. False positives.

### G3 — "Missed opportunity to use Where" (CodeQL, 1) — **FIXED**
`TeamsBotHandler.cs:414` foreach+if → `foreach (… channels.Where(c => !IsNullOrEmpty(c.Name)))`.
Behavior-preserving; covered by existing channel-enumeration tests.

### G4 — AI findings on `TeamsBotHandlerWebhookCommandsTests.cs` (3) — **fold into F3/F6 work**
1. Unused mock fields — valid minor cleanup; do it when we edit this test file for F3/F6.
2. No positive test for explicit `updown` source — **valid, add it** (aligns with F3 changes).
3. Help-text test matches disconnected substrings (fragile) — **valid, strengthen** with an ordering
   assertion (already partially shown in the scan's suggested diff).

None of §G is a correctness bug. The AI test-quality items (G4) are worth doing but naturally ride
along with the F3/F5/F6 command work.

---

## §M — Module hardening (terraform-azurerm-teams-notification-bot-lz)

### M1 — Validate IpSecurityRestriction description/name length  **[greenlit — see PR]**
**Why:** the dev-wlzs apply failed at ARM (not at plan) because a caller-rule `description` exceeded
Azure's **64-char** `IpSecurityRestriction.Description` limit (`ExtendedCode 01033`). Plan/validate
didn't catch it.

**Assessment of other affected inputs:** both list-of-object inputs that feed
`ipSecurityRestrictions` carry `name` + `description` and hit the same ARM limits:
- `allowed_caller_rules` (`variables.tf:113`)
- `management_ip_rules` (`variables.tf:390`)

Azure limits: **Description ≤ 64 chars**; **Name ≤ 64 chars**. Adding validation for both fields on
**both** variables.

**Not adding integration tests** (per operator). Unit tests via `expect_failures` in
`tests/unit-tests.tftest.hcl` (matches existing `management_ip_rules_rejects_invalid_cidr` style).

Status: **FIXED** — PR #10 merged; released as **patch v1.1.1** (release PR #11). 86 unit tests pass
(5 new). This is the one item resolved outside this backlog's own commit series.

---

## Manual-verification progress (against manual-verification.md)

Run against **deployed dev** (`func-ikt-ops-teams-notifier-dev`), 2026-07-02, using a throwaway
personal-chat webhook (`5f30b126`) the operator created. **Essentially complete** — only §7 (updown's
own test-sender, operator side) and §8c live-flip (skipped — see below) remain.

| § | Check | Result |
|---|-------|--------|
| §3a | unknown token → 404 | ✅ PASS (logged rejected, hash prefix, no token) |
| §5 | `/api/v1/notify` + `/api/v1/send` no token → 401 | ✅ PASS |
| §0 | `/api/health` anonymous → 200 | ✅ PASS |
| §2a–f | card per event type → 200 + `Enqueued=1` | ✅ PASS (all 6 cards delivered) |
| §2g | performance_drop disabled by default | ✅ PASS (`Enqueued=0, Skipped=1`) |
| §3b | malformed JSON → 200, no enqueue | ✅ PASS (caught, logged exception) |
| §3c | non-array object → 200, no enqueue | ✅ PASS |
| §3d | empty array → 200 | ✅ PASS (`Enqueued=0, Skipped=0`) |
| §3e | unknown event → 200, skipped | ✅ PASS (`Skipped=1`) |
| §3f | dedupe (same token+event+time) | ✅ PASS (`Enqueued=0, Skipped=1`) |
| §3g | body > 28 KB → 413 | ✅ PASS (`too large (> 28672 bytes)`) |
| §3h | wrong `Content-Type: text/plain` | ⚠️ 200 — content-type **not enforced** (lenient; minor) |
| §4 | enqueue + render + no poison | ✅ PASS (6 cards, no delivery failures/poison) → but **F10** |
| §5 | rate-limit → 429 + Retry-After | ✅ PASS (100× 200 then 429, `Retry-After: 60`) — shared bucket not per-IP (**F8**) |
| §6 | token redacted in telemetry | ❌ **FAIL → F9** |
| §7/§8 | source IP captured correctly | ❌ **FAIL → F8** (`::1`/`127.0.0.1`) |
| §8a | allowlist populate/inspect | ✅ operator ran it (22 entries) |
| §8b | log-only never blocks | ✅ observed (source IP wrong — F8) |
| §8c | enforce blocks non-updown | ⏭️ not live-flipped (avoids app restart mid-session); **certain by F8** — every request is `::1`, so enforce would reject **all** traffic incl. real updown |
| §7 | updown's own test-sender | ⏳ operator-side (updown.io/recipients/test) |

The runbook surfaced **four** issues the automated tests didn't: F8, F9, F10, and the §3h content-type
leniency. The functional happy path (parse, filter, dedupe, enqueue, deliver, render, body-cap,
rate-limit mechanism) all **works**.

> Test webhook `5f30b126` should be **rotated/removed** — these tests wrote its token to App Insights
> in cleartext (F9).

## Prioritized action list

1. **F9** (HIGH) — stop logging webhook tokens in cleartext (telemetry redaction). Secret exposure.
2. **F8** (HIGH) — fix source-IP extraction; without it enforce-mode IP filtering and per-IP rate
   limiting don't work. Blocks meaningful F1 enforce.
3. **Finish manual verification** — unblock §2–§8 with a token; expect more findings.
4. **F2** — remove the unexplained `AppValidation-…` webhooks from dev + trace their origin.
5. **F1** — auto-populate allowlist (fix (b)).
6. **F7** — quoted-message delete + clearer guidance.
7. **F3 / F5 / F6 / F4** — webhook command UX (batch; carries the G4 test improvements).
8. **M1** — module length validation — ✅ done (PR #10 → v1.1.1).
9. **G1(441)/G3** — optional tidies if touching those methods.
