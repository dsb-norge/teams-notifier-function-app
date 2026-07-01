# Implementation plan: updown.io webhook ingress

Companion to [design.md](./design.md). File-by-file work, ordered so each step builds and tests
green before the next. Paths are relative to each repo's root.

Repos:
- **APP** — `dsb-norge/teams-notifier-function-app` (this repo)
- **MODULE** — `dsb-norge/terraform-azurerm-teams-notification-bot-lz`
- **CONSUMER** — `dsb-infra/azure-terraform-ikt-app-platform-common-config` (reference; changes
  documented here, applied in that repo's own PR)

## Status

| Phase | Repo | Status |
|-------|------|--------|
| 0 — models & fixtures | APP | ✅ Done |
| 1 — event → card | APP | ✅ Done |
| 2 — webhook token store | APP | ✅ Done |
| 3 — ingest function + middleware + telemetry | APP | ✅ Done |
| 4 — bot commands + help | APP | ✅ Done |
| 5 — rate limiting | APP | ✅ Done |
| 6 — contract + docs | APP | ✅ Done |
| 7 — Terraform module `excludedPaths` | MODULE | ⏳ Not started (deferred) |
| 8 — consumer: open site inbound + module bump | CONSUMER | ⏳ Not started (deferred) |

All APP phases landed on branch `feat/updown-io-webhook`; **406 tests green** (`dotnet test`,
Azurite for integration). The route is **not reachable on a real deploy until phases 7–8** are done
(EasyAuth excluded path + open site inbound). The generated `app-requirements.json` already advertises
`well_known_routes.updown_webhook_ingest_endpoint` and `bot_auth_settings.easy_auth_excluded_paths`
for the module to consume; infra hash is now `04eec911853e`.

Where the as-built differs from the original plan below, the plan text has been updated to match
(inline JSON fixtures instead of external files; single-file model; `RateLimitPolicy` helper;
negative-lookahead rule exclusion). Deltas are called out inline.

---

## Phase 0 — models & fixtures (APP) — ✅ Done

1. **`src/TeamsNotificationBot/Models/UpdownWebhookPayload.cs`** — one file with the class graph
   (`UpdownEvent` + `UpdownCheck`, `UpdownDowntime`, `UpdownSsl`, `UpdownCert`), matching the repo's
   `CommonAlertPayload.cs` convention (single file, not a subfolder).
   - `System.Text.Json`, all properties **nullable**, `[JsonPropertyName]` for snake_case.
   - Deserialize the **array** wrapper (`List<UpdownEvent>`).
   - Lenient: unknown JSON properties ignored by default; do **not** annotate `[JsonRequired]`.
   - `event` string kept raw (so unknown/future types round-trip and can be logged+skipped).
   - Plus **`Models/UpdownEventTypes.cs`** — the 7 event-type constants + `DefaultEnabled` (all
     except `check.performance_drop`) + `IsKnown`.
2. **`tests/.../UpdownPayloads.cs`** — *(as-built: inline `const string` fixtures, not external
   `.json` files, matching the repo's inline-JSON test convention)*. The 7 verbatim payloads plus
   negatives: malformed, empty-array, unknown-event, nulls-everywhere, not-an-array, and an
   evil-downtime-link case.
3. **Unit tests** `Models/UpdownWebhookPayloadTests` — each fixture deserializes; null-safe access;
   unknown event type parses without throwing; array-of-N parses; non-array/malformed throw.

## Phase 1 — event → card (APP) — ✅ Done

4. **`src/TeamsNotificationBot/Services/UpdownCardBuilder.cs`** — `static string Build(UpdownEvent e,
   string? updownAccountLabel)`. Mirrors `AlertCardBuilder` conventions (returns card JSON string).
   - Colour + emoji + title per event type (design §8).
   - FactSet, null-safe (omit missing facts).
   - Humanize `downtime.duration` (seconds → `"9 minutes"`).
   - Downtime link: only if `downtime.details_url` starts with `https://updown.io/` → render as a
     markdown link in a `TextBlock`; else plain text / omit. **Never** auto-link `check.url`.
   - Footer TextBlock: `source: updown.io (unverified sender)`.
   - No `Action.*`, no external `Image` urls (must pass `AdaptiveCardValidator.Validate` — assert
     this in tests even though we don't route it through the validator at runtime).
5. **Unit tests** `UpdownCardBuilderTests` — one per event type asserting colour + key facts
   present; a null/missing-field case per event; assert every built card **passes
   `AdaptiveCardValidator.Validate`**; assert a non-updown.io `details_url` is not linkified.

## Phase 2 — webhook token store (APP) — ✅ Done

6. **`src/TeamsNotificationBot/Models/WebhookTokenEntity.cs`** — `ITableEntity` with the fields in
   design §5.
7. **`src/TeamsNotificationBot/Services/IWebhookService.cs` + `WebhookService.cs`** — mirrors
   `AliasService`. Methods:
   - `Task<WebhookTokenEntity?> ResolveByTokenAsync(string token)` — `RowKey = Sha256Hex(token)`
     point-read; returns null on 404.
   - `Task<(string id, string token)> CreateAsync(target coords, createdBy, name)` — generate 32
     random bytes → base64url token; `Id` = 8 hex; store SHA-256; default `EnabledEvents`.
   - `Task<IReadOnlyList<WebhookTokenEntity>> ListAsync()` — query `"webhook"` partition.
   - `Task<bool> RemoveByIdAsync(string id)`; `Task<string?> RotateByIdAsync(string id)` (returns
     new token); `Task ConfigureAsync(id, description?, account?, enabledEvents?)`;
     `Task TouchLastReceivedAsync(WebhookTokenEntity)` (best-effort, ETag-tolerant).
   - `Sha256Hex` helper; token generation via `RandomNumberGenerator`.
8. **DI wiring — `src/TeamsNotificationBot/Program.cs`**: register a `webhooktokens` `TableClient`
   in **both** the Azurite branch (lines ~185-207) and the Managed-Identity branch (~223-255),
   `CreateIfNotExists()`, then `services.AddSingleton<IWebhookService>(new WebhookService(client))`.
9. **Unit tests** `WebhookServiceTests` (mock `TableClient` per existing test style): create →
   resolve round-trip; resolve miss → null; rotate invalidates old hash; remove; configure updates
   fields; token is never persisted in plaintext (assert stored `RowKey`/props contain only hash).

## Phase 3 — ingest function (APP) — ✅ Done

10. **`src/TeamsNotificationBot/Middleware/AuthMiddleware.cs`** — add the ingest prefix to the
    skip-list (line ~41 alongside `/messages`): skip when
    `path.Contains("/v1/ingest/", OrdinalIgnoreCase)`. (Correlation id + source-IP extraction above
    that line still run.)
11. **`src/TeamsNotificationBot/Functions/UpdownIngestFunction.cs`** — `[HttpTrigger(Anonymous,
    "post", Route = "v1/ingest/updown/{token}")]`. Flow = design §3:
    - size cap (read with a bounded stream / check `ContentLength`, reuse the 28 KB constant or a
      dedicated smaller `UpdownWebhook__MaxBodyBytes`);
    - `[debug] dump` (setting-gated, sanitized);
    - log source IP (`X-Forwarded-For` first hop);
    - `ResolveByTokenAsync(token)` → miss → `404` + log rejected token **id-less** (log a hash
      prefix, never the token);
    - parse `List<UpdownEvent>`; catch → `Error` log + `200`;
    - per event: unknown type → log+skip; filtered → skip; dedupe via `IIdempotencyService` → skip;
      else build card + enqueue `QueueMessage{ Target = fromEntity, Format="adaptive-card",
      MessageId=… }` on the injected `notifications` `QueueClient`;
    - `TouchLastReceivedAsync`;
    - `200` (or `5xx` only if enqueue threw).
    - Build `MessageTarget` from the entity's `TargetType`+ids (same shape `SendFunction` consumes).
12. **Telemetry redaction** — `src/TeamsNotificationBot/Helpers/TokenRedactingTelemetryInitializer.cs`
    (`ITelemetryInitializer`): rewrite `RequestTelemetry.Url`/`Name` replacing the `{token}` segment
    of `/api/v1/ingest/updown/<token>` with `.../***`. Register in `Program.cs` services.
13. **Integration tests** `UpdownIngestFunctionTests` (Azurite; mirror `NotifyFunctionTests`):
    - valid token + valid `check.down` → `202/200` and a message lands on `notifications` with
      `Format=adaptive-card` and the right `Target`;
    - unknown token → `404`, nothing enqueued;
    - malformed body → `200`, nothing enqueued, Error logged;
    - unknown event type → `200`, skipped;
    - filtered event (e.g. `check.up` when only ssl enabled) → skipped;
    - duplicate `(token,event,time)` → single enqueue;
    - array with mixed known/unknown/filtered → only eligible ones enqueued;
    - oversized body → `413`;
    - debug-dump setting on → body appears in logs (captured logger), sanitized.

## Phase 4 — bot commands + help (APP) — ✅ Done

14. **`src/TeamsNotificationBot/Services/TeamsBotHandler.cs`** — add dispatch branches in
    `OnMessageActivityAsync` (before the generic fallback; longest-prefix first):
    `create-webhook`, `configure-webhook`, `list-webhooks`, `remove-webhook`, `rotate-webhook`.
    Handlers use `ExtractConversationKeysAsync` to capture the target (create/rotate scope), and
    `IWebhookService`. Gate with the existing `IsAuthorizedForQueueCommandsAsync` pattern (valid
    Entra ID). Inject `IWebhookService` via the constructor.
    - `create-webhook`: capture target → `CreateAsync` → reply with URL (`GetHostname()`) + id +
      "shown once" warning + default event filter note.
    - `list-webhooks`: `ListAsync` → new `WebhookListCardBuilder` (mirror `AliasListCardBuilder`).
    - `configure-webhook <id> [--desc …] [--account …] [--events a,b,c]` → `ConfigureAsync`.
    - `remove-webhook <id>` / `rotate-webhook <id>` → confirm reply.
15. **`src/TeamsNotificationBot/Services/WebhookListCardBuilder.cs`** — list card (no secrets).
16. **Help** — `src/TeamsNotificationBot/Helpers/HelpTextBuilder.cs`: add `Webhooks()`; add
    `"webhooks"/"webhook"` case in `TeamsBotHandler.HandleHelpAsync`; add to `Overview()` and the
    unknown-topic hint list. Content per design §9 (comprehensive).
17. **Unit tests** `TeamsBotHandler` webhook-command tests (mirror alias-command tests): create
    returns URL+id; list renders; configure updates; remove/rotate; unauthorized (no Entra id)
    rejected; `help webhooks` returns the section.

## Phase 5 — rate limiting (APP) — ✅ Done

18. **`src/TeamsNotificationBot/Middleware/RateLimitPolicy.cs`** *(as-built: extracted a testable
    helper instead of inline lambdas)* — holds the two `UriPattern`s, identity-key helpers, and
    env-driven limits. The AAD rule uses a **negative-lookahead** pattern
    `"/api/v1/(?!ingest/).*"` so anonymous ingest is *never* bucketed under the principal rule
    (deterministic — does not rely on ThrottlingTroll's null-identity skip). The ingest rule is
    `"/api/v1/ingest/.*"` keyed by source IP, defaults `RateLimit__Ingest__PermitLimit`=100 /
    `__IntervalInSeconds`=60.
19. **`Program.cs`** — wire both rules via `RateLimitPolicy`; the existing `ResponseFabric` already
    emits RFC-7807 `429` + `Retry-After`.
20. **Test** `Middleware/RateLimitPolicyTests` — the two patterns are disjoint (ingest excluded from
    the AAD rule); principal key null/empty → null (rule skipped); source-IP key uses first
    X-Forwarded-For hop with fallbacks.

## Phase 6 — contract + docs (APP) — ✅ Done

21. **`scripts/generate-requirements.sh`** — extracts the ingest `Route` from
    `UpdownIngestFunction.cs`; adds `well_known_routes.updown_webhook_ingest_endpoint` and
    `bot_auth_settings.easy_auth_excluded_paths` = `[messaging_endpoint, "/api/v1/ingest/updown"]`
    (the static prefix, `{token}` stripped) for the module to consume.
22. **`scripts/requirements-static.json`** — *(as-built per the command-hint decision, given Teams'
    10-per-scope cap)*: added `create-webhook` + `list-webhooks` to the **team** scope (dropping the
    `queue-peek`/`queue-retry` hints to stay ≤10) and to the **personal** scope; groupChat unchanged.
    The other webhook commands still work by typing and are documented in `help webhooks`. Validation
    (`[8/9] Teams manifest limits`) confirms team 10, personal 10, groupChat 6.
23. Ran `generate-requirements.sh && validate-requirements.sh` (all checks pass); committed the
    regenerated `app-requirements.json` (infra hash → `04eec911853e`).
24. **User docs** (this repo `docs/`): updated `api-reference.md` (new endpoint, anonymous, event
    payloads, 200-semantics), `bot-commands.md` (new §5 + availability table), `architecture.md`
    (new route + `webhooktokens` table + trust zone), `troubleshooting.md` (updown didn't deliver /
    retries / debug dump / rotate on leak). Cross-linked this feature folder.

## Phase 7 — MODULE — ⏳ Deferred (no TF changes yet, per instruction)

24. **`main.compute.tf`** — change EasyAuth `excludedPaths` from `[messaging_endpoint]` to
    `concat([...messaging_endpoint], <additional excluded paths from app_requirements>)`.
25. **`variables.tf`** — extend the `app_requirements` object typing for the new field (optional,
    defaulted so older `app-requirements.json` still validate).
26. **`examples/` + `README` + `CHANGELOG`** — document the new excluded-path capability and that
    consumers exposing an anonymous ingest route must open site inbound themselves. Version-bump.

## Phase 8 — CONSUMER (documented; applied in that repo) — ⏳ Deferred

27. **`main/main.teams-notifier.tf`** — add `allowed_caller_rules` entry
    `{ name = "public-ingest", description = "Public updown.io webhook ingress", cidr = "0.0.0.0/0" }`
    (+ `::/0` if needed); bump module `version`. No KV, no app settings, no updown IP rules.
28. Apply Terraform (updates auth excludedPaths + inbound) **before** the app deploy — the infra
    hash gate in the deploy pipeline enforces this ordering.

---

## Test coverage summary (positive + negative)

| Area | Unit | Integration |
|---|---|---|
| Payload parsing | all 7 events, unknown, nulls, non-array, empty array | — |
| Card building | per-event facts+colour, null-safety, validator-safe, link domain-gating | — |
| Webhook store | create/resolve/rotate/remove/configure, hash-only persistence | round-trip vs Azurite |
| Ingest function | — | valid, unknown-token 404, malformed→200, unknown/filtered/dup skips, oversize 413, debug dump |
| Commands | create/list/configure/remove/rotate, authz, help webhooks | — |
| Rate limiting | — | ingest 429/Retry-After, /notify regression |
| Telemetry redaction | token stripped from RequestTelemetry.Url/Name | — |

Run `dotnet build && dotnet test` from repo root; integration tests need Azurite
(`azurite --silent --skipApiVersionCheck`). Target: high coverage, both paths.

## Suggested PR/commit sequencing

MODULE PR (excludedPaths capability, back-compatible) → merge/tag → APP PR (feature + regenerated
`app-requirements.json`, `feat:` conventional commit) → CONSUMER PR (open inbound + module bump) →
`terraform apply` → app deploy. The deploy pipeline's infra-hash gate guarantees the consumer
apply happens before the new app version rolls out.
