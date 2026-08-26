# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET 10 isolated-worker Azure Function App that routes notifications to Microsoft Teams via the Bot Framework / M365 Agents SDK. The companion Terraform module that provisions the infrastructure lives at [dsb-norge/terraform-azurerm-teams-notification-bot-lz](https://github.com/dsb-norge/terraform-azurerm-teams-notification-bot-lz) — this repo only contains the function app code, Teams manifest assets, and an `app-requirements.json` contract consumed by the Terraform module.

User-facing docs in `docs/` are the source of truth for architecture, deployment, API, bot commands, and troubleshooting — read them before answering questions about behaviour rather than re-deriving from code.

## Common commands

```bash
# Build (run from repo root — picks up TeamsNotificationBot.slnx)
dotnet build

# Tests (xUnit + Moq). Integration tests need Azurite running.
dotnet test --project tests/TeamsNotificationBot.Tests/
dotnet test --project tests/TeamsNotificationBot.Tests/ -- --filter-class "*NotifyFunctionTests"

# Local function host (offline = no Azure access, no real Teams delivery)
cd src/TeamsNotificationBot
./setup-local.sh offline                                       # or: ./setup-local.sh online
azurite --silent --location /tmp/azurite --skipApiVersionCheck &
func host start                                                # http://localhost:7071

# Regenerate app-requirements.json (required after queue/route/auth changes)
cd scripts && ./generate-requirements.sh && ./validate-requirements.sh
```

`--skipApiVersionCheck` is mandatory: .NET 10's Azure SDK uses a storage API version newer than Azurite recognises.

In offline mode HTTP calls to `/api/v1/*` must simulate EasyAuth by providing both `X-MS-CLIENT-PRINCIPAL-ID` and a base64-encoded `X-MS-CLIENT-PRINCIPAL` claims payload that includes the `Notifications.Send` role. See `docs/local-development.md` §3 for the exact curl pattern.

## Architecture in one paragraph

HTTP triggers (`NotifyFunction`, `AlertFunction`, `SendFunction`, `CheckInFunction`) validate, then enqueue onto the `notifications` queue. `QueueProcessorFunction` dequeues, resolves the alias to a conversation reference in Table Storage, and proactively sends via `CloudAdapter` (M365 Agents SDK) → Bot Framework → Teams. Inbound bot activity arrives at `BotMessagesFunction` (`POST /api/messages`), which is excluded from EasyAuth — the M365 Agents SDK validates the Bot Framework JWT itself. `BotOperationsFunction` and `PoisonQueueMonitorFunction` handle a parallel internal queue plus poison-queue alerting (which itself catches all exceptions to avoid `-poison-poison` cascades).

Storage: 5 tables (`aliases`, `conversationreferences`, `teamlookup`, `idempotencykeys`, `ThrottlingTrollCounters`) and 4 queues (`notifications`, `notifications-poison`, `botoperations`, `botoperations-poison`). Shared keys are disabled in production — `Program.cs` selects between an Azurite connection-string path (when `AzureWebJobsStorage` is set) and a Managed Identity path (`StorageAccountName` + `AzureWebJobsStorage__clientId`).

`AuthMiddleware` runs first on every HTTP request: it injects a correlation ID, skips auth for `/api/messages`, `/api/health`, and `/api/v1/openapi.yaml`, then for the remaining `/api/v1/*` routes it parses the EasyAuth claims, requires the `Notifications.Send` app role, and enforces a 28 KB body limit. Rate limiting is layered on top via `ThrottlingTroll` (60 req/60 s per `X-MS-CLIENT-PRINCIPAL-ID`, counters stored in the `ThrottlingTrollCounters` table).

`docs/architecture.md` has the full sequence diagrams, data model, and infrastructure topology.

## Things that bite

- **`app-requirements.json` is generated, not hand-edited.** It is the contract with the Terraform module and the Teams manifest builder — it declares queues, routes, runtime version, required app settings, auth settings, and command lists. After changing any of those, regenerate it (`scripts/generate-requirements.sh`) and commit. CI's **Validate Requirements** job will fail the PR otherwise.

- **`TeamsBotHandler` is an `AgentApplication` with routes, not an `ActivityHandler`.** Migrated 2026-08-24 (`docs/contributing.md` §9 has the record). Routes are registered in the constructor and the handler is a **singleton** (the base ctor reflection-scans the whole class per construction; the poison-nudge cache relies on the singleton lifetime and is volatile-safe). The behavior-critical option flags (mention handling in the handler not the SDK, no typing timer) live in `TeamsBotHandler.ApplyHandlerOptionInvariants`, called by both `Program.cs` and the test helper — change them there, not in either call site. Member added/removed events are deliberately unrouted, and a last-rank catch-all invoke route answers invoke surfaces this bot doesn't handle with an explicit 501 plus a warning log (the SDK would 501 silently; the log keeps a missing route observable). Handler tests construct it with `TestAgentOptions.Create()` and build mocked `ITurnContext`s via `TurnContextStub.Wrap` — the turn pipeline NREs on a context without real `Services`/`StackState` collections, and the stub is the one place to add the next required member.

- **One migration workaround is still load-bearing** (revisit condition in `docs/contributing.md` §9): `Helpers/TeamsChannelList.cs` wraps the team channel-list REST call because `Microsoft.Teams.Api` 2.0.9's `TeamClient.GetConversationsAsync` mis-deserializes the response (bare array vs the service's `{"conversations":[...]}`) and throws on every real call. Don't "simplify" it away — `Microsoft.Teams.Api` is still 2.0.9 in the MSTeams 1.8.50 graph. (The sibling `Microsoft.Kiota.Abstractions` pin was dropped 2026-08-26 once MSTeams 1.8.50 resolved Graph 6.5.0 → Kiota 2.0.0 on its own; don't reintroduce it.)

- **Proactive turns bypass the AgentApplication pipeline.** `BotService` callbacks from `ContinueConversationAsync` never see the `TeamsAgentExtension` before-turn hook, so no Teams `ApiClient` is in `turnContext.Services` there — `BotService.GetTeamChannelsProactiveAsync` builds its own from `IConnections` + `IHttpClientFactory`. Changes to these paths need the manual pass in `docs/manual-verification.md` on dev.

- **`Microsoft.ApplicationInsights.WorkerService` is held below 3.0.0 on purpose.** 3.0 removed `ITelemetryInitializer`, which `Helpers/TokenRedactingTelemetryInitializer.cs` implements — building against 3.x fails with `CS0246`. Dependabot ignores that major. Re-verified 2026-08-14; see `docs/contributing.md` §9 for the revisit condition, and the "Bumping dependencies" section below for the day-to-day rules.

- **The test project runs on Microsoft Testing Platform, not VSTest.** `global.json` carries the opt-in (`"test": { "runner": "Microsoft.Testing.Platform" }`); the test project references `xunit.v3.mtp-v2` and `GitHubActionsTestLogger` 3.x, with no `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio`. Three gotchas: dropping the `global.json` opt-in fails the build with *"Testing with VSTest target is no longer supported"*; a bare directory path is rejected (*"Specifying a directory for 'dotnet test' should be via '--project' or '--solution'"*), so pass `--project tests/TeamsNotificationBot.Tests/` or no path at all; and MTP forwards unrecognised arguments to the test app, so `dotnet test`'s own options (`--project`, `--output <Detailed|Normal>`, `--no-build`, `-c`) go **before** `--` while test-app options (`--filter-class`, `--report-github*`) go **after** it — a stray `--nologo` yields `Unknown option` and "Zero tests ran".

- **Conventional commits drive releases.** release-please reads the squash commit message (not the individual PR commits) to decide patch/minor/major bumps and to update `CHANGELOG.md`, `AppInfo.cs`, and `app-requirements.json`. Dependabot PRs should be squash-merged with a `fix(deps):` prefix.

- **Deploys are not run from this repo.** This repo builds and releases artifacts (ZIP + `app-requirements.json` + Teams manifest tarball). The deploy workflow that uploads the ZIP to an Azure Function App lives in the ops/infra repo (`dsb-infra/azure-terraform-ikt-operations`), runs on a VNet-integrated runner, and uses an SCM IP allow-list. Don't reintroduce a `deploy.yml` here.

- **Broad `catch (Exception)` in side-effect paths is deliberate — don't narrow it.** Channel-name backfill, conversation-reference auto-refresh, `LastUpdated` stamping, the poison-alias nudge, channel enumeration, the updown warm-up and test teardown all catch broadly, log, and continue, because a failure in a side concern must never break notification delivery. GitHub's **Code Quality** surface flags all of them (`cs/catch-of-all-exceptions`); `docs/contributing.md` §5 records the triage, including the findings that are rejected on sight (`ResponseFabric` is a real ThrottlingTroll member, em dashes are house style, the test-double `IDisposable` hits are false positives). Auth/validation decisions are the exception — those catch specific types only.

- **`LogSanitizer.Sanitize()` is a CodeQL taint barrier.** The custom model in `.github/codeql/extensions/` teaches Default Setup that values flowing through `Sanitize` are clean. When logging user-controlled values, prefer `Sanitize(value)` over string interpolation, and don't rename or remove the helper without updating the extension.

- **Use `DateTimeOffset.UtcNow`, never `DateTime.UtcNow`.** Mixed kinds round-trip badly through Azurite (you'll see `LastUsed ... DateTime has a Kind of Local` warnings) and through Table Storage in general.

- **Flex Consumption uses per-function scaling.** Each queue trigger gets its own instance group. Don't assume singleton-per-app behaviour for in-memory state across triggers.

- **WSL filesystem matters for performance.** Keep the working tree on the Linux side (`/home/...`), not on `/mnt/c/...` — npm/dotnet/git operations on `/mnt/c` are an order of magnitude slower.

## Bumping dependencies

The deps surface is: NuGet packages (csproj), the .NET SDK (`global.json`), GitHub Action versions (workflow files), the Dependabot config itself, and unpinned tooling (`azurite`, `azure-functions-core-tools`). Most of it is on Dependabot's weekly schedule (Mondays); `docs/contributing.md` §9 documents the groups and revisit conditions for the long-standing holds.

### Pin format rules

- **GitHub Action `uses:` lines are SHA-pinned with a trailing version + date comment.** Example: `uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6 2026-01-09`. Tag refs (`@v4`) are not accepted; resolve to a commit SHA before pinning (annotated tags need two `gh api` hops via the `.object` chain).
- **Dependabot updates the SHA but not the pin comment**, so Actions bumps almost always need a hand-edit (it leaves the old version/date, or mangles it — `# v6.0.0 2026-06-26.0.0`). Verify the SHA against the tag, then correct the comment. **Add that as a commit on top of Dependabot's — never force-push a branch rebuilt from `main`.** `dependabot/fetch-metadata` parses the *first* commit; replacing it discards the metadata and the auto-merge workflow can no longer arm (no flag recovers it). When a Dependabot PR falls behind `main`, comment `@dependabot rebase` rather than rebasing it yourself.
- **NuGet `<PackageReference>` always carries an exact `Version="X.Y.Z"`.** Wildcards or ranges are not used.
- **`global.json` keeps `rollForward: latestFeature`** so a newer feature-band SDK on the runner image is acceptable, but `major.minor` stays explicit.

### NuGet bump rules

- **`Microsoft.Agents.Hosting.AspNetCore` + `Microsoft.Agents.Authentication.Msal` move in lockstep**; a hand-bump must keep them on the same version or you'll get cryptic runtime SDK errors. `Microsoft.Agents.Extensions.MSTeams` is in the same Dependabot group but versions **separately** (its 1.8.50 depends on the pair's 1.8.77) — matching numbers are not expected. When any of the three moves, check the transitive `Microsoft.Graph`/`Microsoft.Kiota.*` and `Microsoft.Teams.Api` versions it drags in (`dotnet list package --include-transitive`): the Graph/Kiota stack is what forced — and then un-forced — the old top-level Kiota pin, and `Microsoft.Teams.Api` moving past 2.0.9 is the trigger to retire `Helpers/TeamsChannelList.cs` (`docs/contributing.md` §9).
- **The Azure Functions Worker family shares a major.** `Microsoft.Azure.Functions.Worker`, `Microsoft.Azure.Functions.Worker.Sdk`, `Microsoft.Azure.Functions.Worker.Extensions.*`, `Microsoft.Azure.Functions.Worker.ApplicationInsights` are all on `2.x` today. Don't bump one of them across the major boundary alone; the `.Sdk` analyzer that emits the worker entry-point won't be compatible with the rest.
- **`.github/dependabot.yml`'s `ignore:` list is authoritative for "do not bump."** Don't propose lifting an entry without reading the rationale (see "Things that bite" above + `docs/contributing.md` §9) and confirming every revisit condition is met.

### Squash-merge commit prefix

release-please reads the squash message:

- `fix(deps):` → patch bump (the default for Dependabot PRs).
- `feat(deps):` → minor bump (only when the bump enables a feature you're shipping in the same PR).
- `chore(deps):` or no conventional-commit prefix → no release.

Dependabot titles carry the `fix(deps)` prefix via `commit-message.prefix` in `dependabot.yml`, and `dependabot-auto-merge.yml` arms auto-merge with a guaranteed-conventional subject for patch/minor bumps (human approval still gates the merge). Only major bumps are merged by hand: use `gh pr merge --squash --subject "fix(deps): ..."` and never let a non-conventional subject through.

### After a bump

1. Local validate: `dotnet build && dotnet test` from repo root.
2. If the bump can change anything declared in `app-requirements.json` (queue/route/auth/version/SDK runtime band), re-run `scripts/generate-requirements.sh` and commit the result.
3. Push and let CI run. The **Validate Requirements** job will fail the PR if step 2 was skipped.

### Known pitfalls

- **The `≥3.0.0` Dependabot ignore for `Microsoft.ApplicationInsights.WorkerService` is load-bearing.** See "Things that bite" for the rationale; `docs/contributing.md` §9 has the revisit condition. The matching `GitHubActionsTestLogger` ignore was lifted on 2026-08-14 once `xunit.v3.mtp-v2` shipped — don't reintroduce it.
- **`actions/checkout` cleans the workspace by default.** If a job downloads a release artifact (or anything else) and *then* runs checkout, the artifact is deleted. Either reorder so checkout runs first, or pass `clean: false`.
- **`dorny/paths-filter@v4` uses `git diff` on `push` events**, self-deepening from its `initial-fetch-depth: 100` default. The default shallow `actions/checkout` is fine — no need to set `fetch-depth: 2` on the `changes` job.
- **Azurite is intentionally not version-pinned.** It floats on npm. If a release breaks compatibility (typically: Azurite hasn't caught up to a new storage API version used by the .NET SDK), pin in `ci.yml` *and* `docs/local-development.md` *and* the workflow cache key together.
