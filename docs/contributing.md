# Contributing

Guidelines for building, testing, and contributing to the Teams Notification Bot. For local development setup, see [Local Development](local-development.md).

---

## 1. Getting Started

```bash
git clone <repository-url>
cd teams-notification-bot
```

Install the prerequisites listed in [Local Development - Prerequisites](local-development.md#1-prerequisites), then run:

```bash
cd src/TeamsNotificationBot
./setup-local.sh offline
```

This generates `local.settings.json` with mock values so you can build and test without Azure access.

---

## 2. Building

```bash
# Build (from repo root — dotnet finds the .slnx solution file)
dotnet build

# Publish a release build
dotnet publish src/TeamsNotificationBot -c Release -o ./publish
```

The publish output in `./publish` is what gets deployed to the Azure Function App.

---

## 3. Testing

The project has a comprehensive test suite covering all functions, services, middleware, and models.

```bash
# Run all tests
dotnet test --project tests/TeamsNotificationBot.Tests/

# Run a specific test class
dotnet test --project tests/TeamsNotificationBot.Tests/ -- --filter-class "*NotifyFunctionTests"
```

All tests must pass before submitting a pull request. Integration tests require Azurite to be **installed** (`npm install -g azurite`) but not running — `AzuriteFixture` starts its own instance on a dedicated port triple from 11000 and tears it down afterwards. It deliberately ignores any emulator on the default 10000-10002 ports, so a `func host start` session can keep running while you test, and two concurrent `dotnet test` runs won't fight over one emulator.

The updown webhook ingress is covered by: unit tests for payload parsing (inline fixtures in `UpdownPayloads.cs`), card building (colour/facts/null-safety/validator-clean/link domain-gating), the webhook token store (hash-only persistence, rotate), `IpMatcher` (IPv4/IPv6/CIDR + `ip:port` normalisation), and command routing; Azurite integration tests for the ingest function (token 404, malformed→200, filter modes → 403/allow, dedupe) and the allowlist service (resolve/diff/DNS-failure-keeps-list/lazy refresh).

---

## 4. App Requirements

The file `src/TeamsNotificationBot/app-requirements.json` declares the app's infrastructure and manifest dependencies. It is consumed by the Terraform module (queues, routes, auth settings, runtime version) and the manifest script (version, Teams app configuration, command lists).

When you change queue names, HTTP routes, function bindings, required app settings, or auth configuration, regenerate and validate the requirements:

```bash
cd scripts
./generate-requirements.sh
./validate-requirements.sh
```

Always commit the updated `app-requirements.json` alongside your code changes.

---

## 5. Code Style

- Follow .NET conventions: `PascalCase` for public members, `camelCase` for private fields and local variables.
- Use `DateTimeOffset.UtcNow` instead of `DateTime.UtcNow` for all timestamp operations to avoid mixed `DateTimeKind` values, which round-trip incorrectly through Azurite and Azure Table Storage.
- Use Azure Functions isolated worker patterns (not in-process).
- Use `async`/`await` throughout. Never use `.Result` or `.Wait()` on tasks.
- Keep functions thin: validate input, delegate to services, return responses.
- Use constructor injection for all dependencies.
- Log at appropriate levels: `Information` for business events, `Warning` for recoverable issues, `Error` for failures.
- Wrap user-controlled values in `LogSanitizer.Sanitize()` before logging (CWE-117 barrier — see the CI/CD section on CodeQL).
- Secret/webhook handling: never log a plaintext token (store/compare only its SHA-256); derive the source IP from the `X-Forwarded-For` first hop via `IpMatcher.ParseClientIp` (strips Azure's `ip:port`) with a fallback to `RemoteIpAddress`; wrap DNS resolution in try/catch so a transient failure degrades gracefully rather than throwing.

---

## 6. Pull Requests

### Before Submitting

1. **Branch from main** with a descriptive branch name.
2. **All tests pass**: `dotnet test --project tests/TeamsNotificationBot.Tests/` exits with code 0.
3. **Requirements are up to date**: Run `scripts/generate-requirements.sh` if you changed infrastructure dependencies. CI will catch staleness automatically.
4. **No leaked secrets or identifiers**: Wrap user-controlled values in `LogSanitizer.Sanitize()` when logging (see §5); secret scanning push protection blocks pushed credentials.
5. **Descriptive commit messages**: Use conventional commits where possible (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`).

### PR Description

Include:
- What changed and why.
- How to test the change (steps or commands).
- Whether Terraform changes are needed alongside the app change.

### Review Checklist

- Does the change maintain backward compatibility with the API?
- Are new endpoints documented in `openapi.yaml`?
- Are new app settings added to both `app-requirements.json` (via the seed file) and `local.settings.json.example`?
- Do new services have corresponding unit tests?

---

## 7. CI/CD Pipeline

Every pull request targeting `main` runs three CI jobs (in `ci.yml`). A **CI Conclusion** job aggregates their results into a single required status check for branch protection.

| Job | What it checks |
|-----|---------------|
| **Build and Test** | Restores, builds, and runs the full test suite (xUnit) with Azurite for storage emulation. Test failures appear as inline annotations and a Job Summary. |
| **Dependency Review** | Blocks PRs that introduce dependencies with known vulnerabilities. Only runs on pull requests. |
| **Validate Requirements** | Regenerates `app-requirements.json` from source and diffs against the committed file. Posts a PR comment with the diff if stale. Then runs `scripts/validate-requirements.sh` for structural validation. |

**CodeQL** runs separately via GitHub's Default Setup (configured in repo settings, not in a workflow file). It performs static analysis for common vulnerability patterns in C# code. A custom model extension in `.github/codeql/extensions/` marks `LogSanitizer.Sanitize()` as a taint barrier for advanced/custom CodeQL setups. **Note:** GitHub **Default Setup does not load repo-local model packs**, so it does not recognise this barrier — `cs/log-forging` alerts still fire on `Sanitize()`-wrapped values and are triaged as **false positives** (the sanitizer strips CR/LF/tab + U+2028/U+2029 and `ILogger` uses structured, non-interpolated logging). Dismiss such alerts with that rationale (see the dismissed alerts for the established wording). See [`CLAUDE.md`](../CLAUDE.md#things-that-bite) for why this helper must not be renamed or removed without updating the extension in lockstep.

A separate **Microsoft Security DevOps** workflow (`msdo.yml`) runs in parallel:

| Job | When | What it checks |
|-----|------|---------------|
| **Source & Dependency Scan** | PR + push to main | Runs Trivy via MSDO to scan NuGet dependencies for known CVEs. Uploads SARIF to the Security tab and Defender for Cloud. |
| **Binary Scan (BinSkim)** | Push to main only | Runs BinSkim on compiled binaries to check binary-level security properties (DEP, ASLR, stack protection). |

CodeQL and MSDO findings are intentionally non-blocking — they surface in the repository's **Security** tab but do not prevent merging. MSDO additionally feeds findings to Defender for Cloud via SARIF upload.

On push to `main`, the Build/Test, MSDO, and CodeQL jobs run again, plus the [release workflow](#8-versioning-and-releases) triggers.

CI uses concurrency groups — pushing a new commit to a PR cancels any in-progress CI run for that PR.

---

## 8. Versioning and Releases

This project uses [release-please](https://github.com/googleapis/release-please) for automated semantic versioning and release creation.

### How it works

1. **You merge a PR to `main`** with a conventional commit message.
2. **release-please** analyzes the commit and creates (or updates) a release PR that bumps the version, updates `CHANGELOG.md`, and patches version strings in `AppInfo.cs` and `app-requirements.json`.
3. **When the release PR is merged**, release-please creates a GitHub Release with a git tag.
4. **The release workflow** builds the function app, creates signed [build provenance attestations](https://docs.github.com/en/actions/security-for-github-actions/using-artifact-attestations) for the ZIP and `app-requirements.json`, and uploads three artifacts to the release.

### Commit messages and version bumps

release-please determines the version bump from your commit message prefix:

| Commit prefix | Version bump | Example |
|---------------|-------------|---------|
| `fix:` | Patch (1.2.3 → 1.2.4) | `fix: handle null alias in queue processor` |
| `feat:` | Minor (1.2.3 → 1.3.0) | `feat: add bulk notification endpoint` |
| `perf:` | Patch (1.2.3 → 1.2.4) | `perf: reduce queue processor memory allocation` |
| `feat!:` or `BREAKING CHANGE:` footer | Major (1.2.3 → 2.0.0) | `feat!: remove v1 API endpoints` |
| `docs:`, `chore:`, `refactor:`, `test:` | No release | `docs: update API reference` |

Scoped prefixes work too: `fix(deps):`, `feat(auth):`, etc. The scope appears in the changelog but doesn't affect the bump.

**Important:** When squash-merging a PR, the squash commit message determines the version bump — not the individual commits within the PR. Use the `--subject` flag with `gh pr merge --squash` to control this.

### Release artifacts

Each GitHub Release includes three downloadable artifacts:

| Artifact | Contents | Used by |
|----------|----------|---------|
| `teams-notifier-function-app-v{VERSION}.zip` | Pre-built function app (Release config, R2R compiled for linux-x64) | Deployed to Azure Function App |
| `app-requirements.json` | Infrastructure requirements: queues, routes, auth settings, runtime version | Fed into the Terraform module as `var.app_requirements` |
| `teams-app-package-v{VERSION}.tar.gz` | Teams manifest template, color icon, outline icon | Used by `create-teams-app-package.sh` to build the Teams app ZIP |

The tag format is `teams-notifier-function-app-v{VERSION}` (e.g., `teams-notifier-function-app-v1.2.0`).

See the [Deployment Guide](deployment-guide.md#step-3-deploy-function-app) for how to deploy from release artifacts.

---

## 9. Dependency Management

[Dependabot](https://docs.github.com/en/code-security/dependabot) is configured to scan for outdated NuGet packages and GitHub Actions versions weekly (Mondays). The day-to-day rules — pin formats, NuGet bump rules, squash-prefix conventions, post-bump validation, and known pitfalls — live in [`CLAUDE.md`](../CLAUDE.md#bumping-dependencies). This section documents the longer-lived context that doesn't belong in agent instructions.

### Dependency groups

Related packages are grouped so they update together in a single PR:

| Group | Packages |
|-------|----------|
| microsoft-agents | `Microsoft.Agents.*` |
| azure-functions | `Microsoft.Azure.Functions.*`, `Microsoft.Azure.Core.Extensions` |
| azure-sdk | `Azure.*` |
| testing | `xunit*` (includes `xunit.v3.mtp-v2`), `Microsoft.NET.Test.*`, `coverlet.*`, `Moq`, `GitHubActionsTestLogger` |

### Known version constraints — revisit checklist

These are pinned via `ignore:` entries in `.github/dependabot.yml`. Don't lift the ignore without confirming every condition below.

#### `Microsoft.ApplicationInsights.WorkerService` (held below 3.0.0)

Version 3.0 removed `ITelemetryInitializer` from the public API, which breaks `Microsoft.Azure.Functions.Worker.ApplicationInsights` 2.x at runtime (`TypeLoadException` on Flex Consumption).

**To revisit**: the Functions Worker ApplicationInsights package must ship a release that targets the AI 3.x API.

**Last checked (2026-08-14)**: still blocking. `Microsoft.Azure.Functions.Worker.ApplicationInsights` 2.51.0 still depends on `Microsoft.ApplicationInsights.PerfCounterCollector >= 2.23.0`, and building against `Microsoft.ApplicationInsights.WorkerService` 3.1.2 fails with `CS0246: ITelemetryInitializer could not be found` in `Helpers/TokenRedactingTelemetryInitializer.cs`. Keep the ignore.

### Deferred migrations

#### `TeamsActivityHandler` → `Microsoft.Agents.Extensions.MSTeams` (completed 2026-08-24)

Deferred on 2026-08-14, executed on 2026-08-24 (deliberate decision, ahead of the revisit
conditions — the old package was never deprecated). `TeamsBotHandler` now extends
`AgentApplication` and registers a `TeamsAgentExtension` for channel/team lifecycle events;
`AgentApplication` implements the same `IAgent` contract, so CloudAdapter hosting and
`BotMessagesFunction` were untouched. The command dispatcher stayed a single catch-all message
route to preserve behavior exactly. Before the rewrite, 30 dispatch-level tests
(`TeamsBotHandlerLifecycleTests`) were added to pin install/uninstall, channel/team events,
conversation-reference auto-refresh, mention stripping, and card invokes through the public
`IAgent.OnTurnAsync` seam — they ran unchanged (bar constructor options) against the new model.

Two workarounds shipped with it. The first has since been lifted (see
[`Microsoft.Kiota.Abstractions`](#microsoftkiotaabstractions-pin-dropped-2026-08-26) under Lifted
constraints); the second still stands, with its revisit condition:

1. ~~**`Microsoft.Kiota.Abstractions` pinned at top level.**~~ Dropped 2026-08-26 when MSTeams
   1.8.50 shipped stable — see Lifted constraints below.
2. **`Helpers/TeamsChannelList.cs` wraps the channel-list REST call.** `Microsoft.Teams.Api`
   2.0.9's `TeamClient.GetConversationsAsync` deserializes the response as a bare array, but the
   service returns `{"conversations":[...]}` — it throws `JsonException` on every real call
   (caught by the channel-name backfill tests, which replay captured wire JSON). The helper
   issues the same request through the same authenticated `IHttpClient` and deserializes the
   documented wrapper. **Remove it** when bumping `Microsoft.Teams.Api` past 2.0.9 after
   verifying the fix (teams.net main has since corrected the deserialization).

Also note: `Microsoft.Agents.Extensions.MSTeams` versions **separately** from the
`Hosting.AspNetCore`/`Authentication.Msal` pair (its 1.8.50 depends on their 1.8.77). Dependabot's
`microsoft-agents` group still covers all three; grouped bumps move each to its own latest, which
is correct — just don't expect the version numbers to match.

Changes touching the bot turn pipeline, the Agents packages, or `BotService`'s proactive paths
need the manual pass in [manual-verification.md](manual-verification.md) on dev — proactive send
and channel enumeration have no automated coverage by design.

### Dropped automation

#### Copilot review on Dependabot PRs (dropped 2026-08-24)

The "Automatic Copilot Code Review" ruleset only fires when the PR author holds a Copilot seat, so `dependabot[bot]` PRs were never reviewed. `dependabot-copilot-review.yml` requested Copilot explicitly via the GraphQL `requestReviews(botIds:)` mutation. It has been removed.

**It never worked from CI.** The mutation succeeds under `secrets.GITHUB_TOKEN` — it returns `{"data":{"requestReviews":{"clientMutationId":null}}}` — but adds no reviewer. The job went green having done nothing. The mechanism had originally been proven with a *user* token; `GITHUB_TOKEN` cannot request a bot reviewer and GraphQL does not complain.

**Fixing it wasn't worth the cost.** The only remaining route is a PAT: a long-lived, hand-rotated credential with write scope in repo secrets. Weighed against what the review delivers — across #123, #124, #125, #130 and #131, the one Copilot review that did land (requested by hand on #123) produced **zero** comments, just a restatement of the diff. That is the expected result: a dependency bump is a one-line version change, and its risk lives in the changelog, the transitive graph and the breaking-change notes, none of which the reviewer reads. The gates that do catch things here are Dependency Review (it blocked the vulnerable Kiota transitive during the MSTeams evaluation), Build and Test, and a human reading the release notes.

Adding a broadly-scoped standing credential to a repo that SHA-pins its actions specifically to shrink that surface is a poor trade for a reviewer that has so far said nothing.

**Unaffected:** Copilot review on human-authored PRs still runs via the ruleset, and has been genuinely useful there. So has the separate `github-code-quality` bot.

**To revisit**, either of:

1. Dependabot PRs in this repo start carrying more than a version string — a lockfile, a config migration, a vendored patch — so there is real diff surface to review.
2. GitHub makes `GITHUB_TOKEN` able to request Copilot as a reviewer, removing the PAT requirement entirely. Re-test with the mutation above; a `clientMutationId` response is not proof, so check `reviewRequests` afterwards.

### Lifted constraints

#### `Microsoft.Kiota.Abstractions` (pin dropped 2026-08-26, was pinned at 1.22.x)

The MSTeams migration shipped with a top-level `Microsoft.Kiota.Abstractions` pin because
`Microsoft.Agents.Extensions.MSTeams` 1.7.4 resolved `Microsoft.Graph` 5.99.0 → Kiota 1.17.1,
which carries [GHSA-7j59-v9qr-6fq9](https://github.com/advisories/GHSA-7j59-v9qr-6fq9) (HIGH).
The pin lifted Kiota past the advisory while keeping it on the 1.x line that Graph.Core 3.x
expected, so Dependency Review stayed green.

The recorded revisit condition — *"a stable `Microsoft.Agents.Extensions.MSTeams` resolves
`Microsoft.Graph` ≥ 6.x on its own"* — was met by **MSTeams 1.8.50** (stable, 2026-08-26), which
depends on `Microsoft.Graph` 6.5.0 → `Microsoft.Graph.Core` 4.0.1 → Kiota **2.0.0**. The pin then
became an active downgrade: restore failed with `NU1605` (*"Detected package downgrade:
Microsoft.Kiota.Abstractions from 2.0.0 to 1.22.1"*) because the repo builds with warnings as
errors. Both the `PackageReference` and the `ignore: [">=2.0.0"]` entry in
`.github/dependabot.yml` were removed together.

The whole Kiota stack now resolves coherently on 2.0.0 alongside Graph.Core 4.0.1, and
`dotnet list package --vulnerable --include-transitive` reports nothing — the split-across-majors
risk the ignore comment warned about resolved itself once Graph and Kiota moved as a set.

Note this did **not** clear the second migration workaround: `Microsoft.Teams.Api` is still
2.0.9 in the MSTeams 1.8.50 graph, so `Helpers/TeamsChannelList.cs` remains load-bearing.


#### `GitHubActionsTestLogger` (unpinned 2026-08-14, was held below 3.0.0)

Version 3.0 added Microsoft Testing Platform (MTP) support with a transitive dependency on `Microsoft.Testing.Platform` v2, which conflicted with `xunit.v3`'s MTP v1 dependency (`CS1705`, plus `CS0400` from `PrivateAssets="all"`). PRs #31 and #36 failed CI for this reason. See [GitHubActionsTestLogger#57](https://github.com/Tyrrrz/GitHubActionsTestLogger/issues/57).

All four revisit conditions are now satisfied, so the test project moved to MTP v2:

1. `xunit.v3.mtp-v2` 3.2.2 ships as the MTP v2 variant of `xunit.v3` — the package the original checklist anticipated.
2. `PrivateAssets="all"` is gone from the `GitHubActionsTestLogger` reference.
3. `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk` are removed — MTP replaces the VSTest runner, and the test project is already `OutputType=Exe`.
4. `ci.yml` runs `dotnet test --no-build -c Release -- --report-github --report-github-summary-include-passed false`. Under MTP, arguments after `--` go to the test app, and `--report-github` is the equivalent of the old `--logger GitHubActions`.

Two things make this work and are easy to break:

- **`global.json` carries the opt-in.** `"test": { "runner": "Microsoft.Testing.Platform" }` is what switches `dotnet test` off the VSTest path. Without it the build fails with *"Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later"*. `dotnet.config` is **not** the opt-in mechanism for this SDK band, despite what some docs suggest.
- **Unrecognised `dotnet test` flags are forwarded to the test app.** MTP passes anything it doesn't own straight through, so a stray `--nologo` fails the run with `Unknown option '--nologo'` and *"Zero tests ran"*.

- **Know which side of `--` an argument belongs on.** Options owned by `dotnet test` (`--project`, `--solution`, `--output <Detailed|Normal>`, `--configuration`, `--no-build`, `--list-tests`) go **before** `--`. Options owned by the test app (`--filter-class`, `--filter-method`, `--filter-namespace`, `--report-github*`, `--report-junit`) go **after** it. `dotnet test --help` and `<test-assembly> --help` list the two sets respectively.

- **A bare directory path is no longer accepted.** `dotnet test tests/TeamsNotificationBot.Tests/` now fails with *"Specifying a directory for 'dotnet test' should be via '--project' or '--solution'"*. Use `--project tests/TeamsNotificationBot.Tests/`, or run `dotnet test` with no path at all to use the solution.

### Handling Dependabot PRs

The rules live in [`CLAUDE.md`](../CLAUDE.md#bumping-dependencies). One workflow automates the mechanics:

- **`dependabot-auto-merge.yml`** arms GitHub auto-merge with a conventional `fix(deps):` squash subject for **patch/minor** bumps. It bypasses nothing — the merge still waits for the required human approval, CI Conclusion, CodeQL, and up-to-date-with-main. Major bumps are never armed and stay fully manual.

Dependabot PRs get **no Copilot review**, deliberately — see [Copilot review on Dependabot PRs](#copilot-review-on-dependabot-prs-dropped-2026-08-24).

For human reviewers, the short checklist:

1. Read the package changelog for the bumped major/minor — most regressions are advertised there.
2. Approve. For patch/minor the PR then merges itself once **CI Conclusion** passes (Build and Test, Source & Dependency Scan, Dependency Review, Validate Requirements) and the branch is up to date.
3. For major bumps, squash-merge manually with a `fix(deps):` prefix (or `feat(deps):` if the bump enables a shipped feature) using `gh pr merge --squash --subject "..."` — don't let a non-conventional subject through.

A failing Dependabot PR usually means a breaking API change in a major bump, a transitive conflict, or one of the pitfalls listed in `CLAUDE.md`. Investigate before merging; never bypass CI.

#### Amending a Dependabot PR — add a commit, don't replace one

GitHub Actions bumps routinely need a hand-edit, because Dependabot updates the SHA but leaves the trailing `# vX YYYY-MM-DD` pin comment stale or wrong. When you make that edit, **add a commit on top of Dependabot's** rather than force-pushing a branch rebuilt from `main`.

`dependabot/fetch-metadata` reads the *first* commit on the PR: it checks the author is `dependabot[bot]`, then parses the `Bumps ... from ... to ...` line and YAML trailer out of that commit message. Replace that commit and the metadata is gone — the action reports either *"PR is not from Dependabot"* or *"PR does not contain metadata"*. Neither `skip-verification` nor `skip-commit-verification` recovers it; the data genuinely isn't there any more. The auto-merge workflow now treats that as "nothing to do" instead of failing, but you also lose the automatic `fix(deps):` subject and have to merge by hand.

Keeping Dependabot's commit as the first one preserves the metadata and the auto-merge arming. The squash subject comes from the PR title regardless, so extra commits don't reach `main`.

When a Dependabot PR falls behind `main`, prefer commenting `@dependabot rebase` over rebasing it yourself — Dependabot re-authors its own commit and the metadata survives.

---

## See Also

- [Local Development](local-development.md) -- running and debugging locally
- [Architecture](architecture.md) -- system design and message flows
- [Troubleshooting](troubleshooting.md) -- debugging common issues
