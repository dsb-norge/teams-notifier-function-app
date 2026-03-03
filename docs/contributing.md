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

The project has 142 tests covering all functions, services, middleware, and models.

```bash
# Run all tests
dotnet test tests/TeamsNotificationBot.Tests/

# Run a specific test class
dotnet test tests/TeamsNotificationBot.Tests/ --filter "FullyQualifiedName~NotifyFunctionTests"
```

All tests must pass before submitting a pull request. Integration tests require Azurite to be running.

---

## 4. Contract Generation

The function app and Terraform module share an interface contract defined in `src/TeamsNotificationBot/app-contract.json`. This contract specifies queues, tables, routes, environment variables, and other infrastructure dependencies.

When you change queue names, table names, HTTP routes, function bindings, or required environment variables, regenerate and validate the contract:

```bash
cd scripts
./generate-contract.sh
./validate-contract.sh
```

Always commit the updated `app-contract.json` alongside your code changes.

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
3. **Contract is up to date**: Run `scripts/validate-contract.sh` if you changed infrastructure dependencies.
4. **No leaked secrets or identifiers**: Run `scripts/check-sanitization.sh` before publishing.
5. **Descriptive commit messages**: Use conventional commits where possible (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`).

### PR Description

Include:
- What changed and why.
- How to test the change (steps or commands).
- Whether Terraform changes are needed alongside the app change.

### Review Checklist

- Does the change maintain backward compatibility with the API contract?
- Are new endpoints documented in `openapi.yaml`?
- Are new app settings added to both `app-contract.json` and `local.settings.json.example`?
- Do new services have corresponding unit tests?

---

## See Also

- [Local Development](local-development.md) -- running and debugging locally
- [Architecture](architecture.md) -- system design and message flows
- [Troubleshooting](troubleshooting.md) -- debugging common issues
