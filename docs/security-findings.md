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
assessed: a new broad catch in an auth path is a defect even though the ones below are fine. If
code changes enough that GitHub opens the same finding under a new number, assess it again, add
the new ID, and remove the old one (the script lists it as no longer open).

The script reads the ID from the first column, so keep it in the form `` `<kind>#<number>` ``,
where `<kind>` is `code-scanning`, `code-quality`, `dependabot` or `secret-scanning`.

| ID | Rule | Location (when recorded) | Why it is ignored | Recorded |
|----|------|--------------------------|-------------------|----------|
| `code-quality#180` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/TeamsBotHandler.cs:524` | Team name lookup in `teamlookup` is a side concern; a failure must not stop the turn. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-09-25 |
| `code-quality#181` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/TeamsBotHandler.cs:595` | Channel list fetch for name backfill is a side concern. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-09-25 |
| `code-quality#182` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/TeamsBotHandler.cs:1514` | `delete-post` failure path: whatever makes `DeleteActivityAsync` fail, the user gets a reply instead of a 500. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-09-25 |
| `code-quality#183` | `cs/catch-of-all-exceptions` | `src/TeamsNotificationBot/Services/TeamsBotHandler.cs:1855` | Conversation-reference auto-refresh is a side concern. [§5](contributing.md#broad-catch-exception-is-deliberate-in-side-effect-paths) | 2026-09-25 |
| `code-quality#166` | `cs/local-not-disposed` | `tests/TeamsNotificationBot.Tests/Services/TeamsBotHandlerChannelNameBackfillTests.cs:148` | `HttpResponseMessage` returned from a test `HttpMessageHandler` is disposed by the owning `HttpClient`. [§5](contributing.md#findings-that-are-rejected-on-sight) | 2026-09-25 |
| `code-quality#167` | `cs/local-not-disposed` | `tests/TeamsNotificationBot.Tests/Services/TeamsBotHandlerChannelNameBackfillTests.cs:149` | Same test handler as `code-quality#166`. [§5](contributing.md#findings-that-are-rejected-on-sight) | 2026-09-25 |
