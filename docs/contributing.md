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
dotnet test tests/TeamsNotificationBot.Tests/

# Run a specific test class
dotnet test tests/TeamsNotificationBot.Tests/ --filter "FullyQualifiedName~NotifyFunctionTests"
```

All tests must pass before submitting a pull request. Integration tests require Azurite to be running.

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
- Use `DateTimeOffset.UtcNow` instead of `DateTime.UtcNow` for all timestamp operations.
- Use Azure Functions isolated worker patterns (not in-process).
- Use `async`/`await` throughout. Never use `.Result` or `.Wait()` on tasks.
- Keep functions thin: validate input, delegate to services, return responses.
- Use constructor injection for all dependencies.
- Log at appropriate levels: `Information` for business events, `Warning` for recoverable issues, `Error` for failures.

---

## 6. Pull Requests

### Before Submitting

1. **Branch from main** with a descriptive branch name.
2. **All tests pass**: `dotnet test tests/TeamsNotificationBot.Tests/` exits with code 0.
3. **Requirements are up to date**: Run `scripts/validate-requirements.sh` if you changed infrastructure dependencies.
4. **No leaked secrets or identifiers**: Run `scripts/check-sanitization.sh` before publishing.
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

Every pull request targeting `main` runs five CI jobs. All must pass before merging.

| Job | What it checks |
|-----|---------------|
| **Build and Test** | Restores, builds, and runs the full test suite (xUnit) with Azurite for storage emulation. Test results are posted as a PR check and comment. |
| **Security (CodeQL)** | Static analysis for common vulnerability patterns in C# code. Results appear in the repository's **Security** tab. |
| **Trivy Vulnerability Scan** | Scans NuGet dependencies for known CVEs (CRITICAL and HIGH severity). Posts a summary comment on the PR and uploads SARIF results to the Security tab. |
| **Dependency Review** | Blocks PRs that introduce dependencies with known vulnerabilities. Only runs on pull requests. |
| **Validate Requirements** | Runs `scripts/validate-requirements.sh` to ensure `app-requirements.json` is well-formed and consistent. |

On push to `main`, the same Build/Test, Security, and Trivy jobs run again, plus the [release workflow](#8-versioning-and-releases) triggers.

CI uses concurrency groups — pushing a new commit to a PR cancels any in-progress CI run for that PR.

---

## 8. Versioning and Releases

This project uses [release-please](https://github.com/googleapis/release-please) for automated semantic versioning and release creation.

### How it works

1. **You merge a PR to `main`** with a conventional commit message.
2. **release-please** analyzes the commit and creates (or updates) a release PR that bumps the version, updates `CHANGELOG.md`, and patches version strings in `AppInfo.cs` and `app-requirements.json`.
3. **When the release PR is merged**, release-please creates a GitHub Release with a git tag.
4. **The release workflow** builds the function app and uploads three artifacts to the release.

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

[Dependabot](https://docs.github.com/en/code-security/dependabot) is configured to scan for outdated NuGet packages and GitHub Actions versions weekly (Mondays).

### Dependency groups

Related packages are grouped so they update together in a single PR:

| Group | Packages |
|-------|----------|
| microsoft-agents | `Microsoft.Agents.*` |
| azure-functions | `Microsoft.Azure.Functions.*`, `Microsoft.Azure.Core.Extensions` |
| azure-sdk | `Azure.*` |
| testing | `xunit*`, `Microsoft.NET.Test.*`, `coverlet.*`, `Moq` |

### Known version constraints

- **`Microsoft.ApplicationInsights.WorkerService` is pinned below 3.0.0.** Version 3.0 removed `ITelemetryInitializer` from the public API, which breaks `Microsoft.Azure.Functions.Worker.ApplicationInsights` 2.x at runtime (`TypeLoadException`). Dependabot is configured to ignore `>=3.0.0` until the worker package supports it.

### Handling Dependabot PRs

1. **Review the PR** — check the package changelog for breaking changes.
2. **Wait for CI** — all five CI jobs must pass (build, test, security scans).
3. **Squash merge with `fix(deps):` prefix** — this triggers a patch version bump. Example: `fix(deps): bump Azure.Identity from 1.17.0 to 1.18.0`. Use `feat(deps):` only if the update enables a new feature you're shipping.
4. **For grouped updates**, the PR title usually works as-is after adding the `fix(deps):` prefix.

If a Dependabot PR fails CI, investigate before merging. Common causes: breaking API changes in a major version bump, or transitive dependency conflicts.

---

## See Also

- [Local Development](local-development.md) -- running and debugging locally
- [Architecture](architecture.md) -- system design and message flows
- [Troubleshooting](troubleshooting.md) -- debugging common issues
