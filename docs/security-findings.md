# Security and Quality Findings

The repository's **Security and quality** tab collects findings from several scanners: CodeQL
code scanning (Default Setup), Code Quality, Trivy and BinSkim (via `msdo.yml`), Dependabot
(vulnerabilities and malware), and secret scanning (default and generic patterns). Copilot's PR
review catches some of the same problems, but not reliably, so these findings are checked
separately at fixed points.

## When to check

- **After merging a PR**, once the scans on the merge commit have finished.
- **Before merging a release-please PR**, so a release doesn't ship with an open finding nobody
  has looked at.

## How to check

```bash
bash scripts/security-findings.sh --wait   # waits for the scans on main's HEAD, then reports
bash scripts/security-findings.sh          # reports now; warns if scans are still running
bash scripts/security-findings.sh --all    # also lists the ignored findings below
bash scripts/security-findings.sh --json   # machine-readable, including URLs
```

The script is read-only. It lists every open finding except those in the
[permanently ignored](#permanently-ignored-findings) table, and it names any entry in that table
that is no longer open so the entry can be removed. It needs `gh` with the `repo` scope and `jq`.

**Code Quality AI findings are not covered.** GitHub has no API for them (checked 2026-09-25);
they exist only in the web UI and are not part of this routine.

## How to assess a finding

Treat each finding as a question, not an instruction. A scanner's suggested fix is often wrong
for this codebase: broad catches in side concerns are deliberate
([contributing.md §5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths)),
and some findings are rejected on sight
([§5](contributing.md#findings-that-are-rejected-on-sight)). For each one, decide:

1. **Fix it** in a follow-up PR. Before a release, decide whether the fix has to land first.
   GitHub closes a fixed finding by itself on the next scan of `main`.
2. **Ignore it permanently** when the code is deliberate or the finding is a false positive and
   that won't change. Add a row to the table below with the reason.
3. **Leave it open** when it is real but not worth acting on yet. It will show up again at the
   next check, which is intended.

**Findings are not dismissed or resolved in GitHub.** The table below is the record. Alerts that
were dismissed in GitHub before this routine existed stay dismissed and don't appear in the
report.

## Permanently ignored findings

Entries are per finding number, not per rule, so a new instance of a known rule is still
assessed: a new broad catch in an auth path is a defect even though the ones below are fine. GitHub
often opens the same finding under a new number when the code around it moves, even by a few
lines: assess it again, add the new ID, and remove the old one (the script lists it as no longer
open). A "Was" note in the reason keeps the trail.

The script reads the ID from the first column, so keep it in the form `` `<kind>#<number>` ``,
where `<kind>` is `code-scanning`, `code-quality`, `dependabot` or `secret-scanning`.

| ID | Rule | Location (when recorded) | Why it is ignored | Recorded |
|----|------|--------------------------|-------------------|----------|
| `code-scanning#52` | `cs/log-forging` | `src/TeamsNotificationBot/Middleware/AuthMiddleware.cs:82` | Values pass through `LogSanitizer.Sanitize()` into structured `ILogger` parameters. Default Setup can't apply the repo's `Sanitize` barrier to a source-analyzed method; the same line was dismissed as #32/#33 before it shifted. | 2026-10-08 |
| `code-scanning#53` | `cs/log-forging` | `src/TeamsNotificationBot/Middleware/AuthMiddleware.cs:82` | Same line and reason as `code-scanning#52`. | 2026-10-08 |
| `code-quality#191` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/TeamsBotHandler.cs:260` | Poison-alias nudge is a side concern. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#192` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/TeamsBotHandler.cs:522` | Team name lookup in `teamlookup` is a side concern; a failure must not stop the turn. Was `code-quality#180`. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#193` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/TeamsBotHandler.cs:593` | Channel list fetch for name backfill is a side concern. Was `code-quality#181`. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#194` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/TeamsBotHandler.cs:1510` | `delete-post` failure path: whatever makes `DeleteActivityAsync` fail, the user gets a reply instead of a 500. Was `code-quality#182`. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#195` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/TeamsBotHandler.cs:1851` | Conversation-reference auto-refresh is a side concern. Was `code-quality#183`. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#199` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/BotService.cs:335` | Channel- and team-name backfill is a side concern. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#205` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/BotService.cs:496` | Channel enumeration at install is a side concern. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#200` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/BotService.cs:651` | `LastUpdated` stamping runs after the send; a failure must not make the queue post again. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#201` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/DeliveryRecords.cs:105` | Releasing a send claim runs while the send failure propagates. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#202` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/DeliveryRecords.cs:175` | Recording a permanent failure; the failure is already reported. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#203` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/DeliveryRecords.cs:193` | Removing the record of a failed enqueue runs while that failure propagates. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#204` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/DeliveryRecords.cs:239` | Recording a delivery runs after Teams accepted the message; a throw would make the queue post it twice. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#186` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/DeliveryEvents.cs:95` | Delivery-event tracking runs right after a message was queued or delivered. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#187` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/IdempotencyService.cs:114` | Completing an idempotency claim runs after the message was queued. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#188` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/IdempotencyService.cs:136` | Releasing an idempotency claim runs while the request's own failure propagates. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#196` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Functions/PoisonQueueMonitorFunction.cs:51` | Marking a poisoned message failed must not stop the poison alert. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#198` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Functions/PoisonQueueMonitorFunction.cs:97` | Best-effort parse of `enqueuedAt` for the alert card, inside a function that must never throw. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#197` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Functions/PoisonQueueMonitorFunction.cs:109` | The poison monitor catches everything to avoid a `-poison-poison` cascade. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#184` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Functions/CheckInFunction.cs:63` | The check-in body is optional and only labels the source; any failure to read it means `unknown`. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-10-08 |
| `code-quality#166` | `cs/local-not-disposed` | `tests/TeamsNotificationBot.Tests/Services/TeamsBotHandlerChannelNameBackfillTests.cs:148` | `HttpResponseMessage` returned from a test `HttpMessageHandler` is disposed by the owning `HttpClient`. [§5](contributing.md#findings-that-are-rejected-on-sight) | 2026-09-25 |
| `code-quality#167` | `cs/local-not-disposed` | `tests/TeamsNotificationBot.Tests/Services/TeamsBotHandlerChannelNameBackfillTests.cs:149` | Same test handler as `code-quality#166`. [§5](contributing.md#findings-that-are-rejected-on-sight) | 2026-09-25 |
