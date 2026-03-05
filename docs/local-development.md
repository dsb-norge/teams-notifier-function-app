# Local Development Guide

This guide covers running the Teams Notification Bot function app on your local machine for development and testing. For deployment to Azure, see the [Deployment Guide](deployment-guide.md). For troubleshooting deployed environments, see [Troubleshooting](troubleshooting.md).

---

## 1. Prerequisites

| Tool | Install |
|------|---------|
| .NET 10 SDK | `dotnet --version` should show `10.x` |
| Azure Functions Core Tools v4 | `func --version` should show `4.x` |
| Azurite | `npm install -g azurite` |
| jq | `sudo apt install jq` (needed for online mode) |
| Azure CLI | Only needed for online mode |

---

## 2. Quick Start

```bash
cd src/TeamsNotificationBot

# Generate local.settings.json (offline mode -- no Azure access needed)
./setup-local.sh offline

# Start Azurite
azurite --silent --location /tmp/azurite --skipApiVersionCheck &

# Start the function app
func host start
```

Functions available at:

| Function | URL |
|----------|-----|
| Notify | `POST /api/v1/notify/{alias}` |
| Alert | `POST /api/v1/alert/{alias}` |
| Send | `POST /api/v1/send` |
| GetAliases | `GET /api/v1/aliases` |
| Health | `GET /api/health` |
| CheckIn | `POST /api/v1/checkin/{alias}` |
| BotMessages | `POST /api/messages` |
| OpenApi | `GET /api/v1/openapi.yaml` |
| QueueProcessor | Queue trigger (automatic) |
| PoisonQueueMonitor | Queue trigger (automatic) |

All HTTP endpoints are served at `http://localhost:7071`.

---

## 3. Offline Mode

Runs entirely locally with mock values. No Azure access needed, no real Teams delivery.

```bash
./setup-local.sh offline
```

This generates `local.settings.json` with:
- `TEAMS_INTEGRATION_DISABLED=true` -- bot service calls are skipped
- Mock channel aliases (`test`, `diagnostics`)
- Azurite for storage (`UseDevelopmentStorage=true`)

### Test the HTTP API

In offline mode, you must simulate EasyAuth headers that would normally be injected by Azure App Service. The two required headers are `X-MS-CLIENT-PRINCIPAL-ID` (the caller's object ID) and `X-MS-CLIENT-PRINCIPAL` (a base64-encoded claims payload).

```bash
curl -X POST http://localhost:7071/api/v1/notify/test \
  -H "Content-Type: application/json" \
  -H "X-MS-CLIENT-PRINCIPAL-ID: local-test-user" \
  -H "X-MS-CLIENT-PRINCIPAL: $(echo '{"auth_typ":"aad","claims":[{"typ":"roles","val":"Notifications.Send"}],"name_typ":"name","role_typ":"roles"}' | base64 -w0)" \
  -d '{"message": "hello from local dev", "format": "text"}'
```

The message will be queued and processed by the QueueProcessor, but since Teams integration is disabled, no actual message is sent. You will see the full processing flow in the function host logs. The health endpoint (`GET /api/v1/health`) does not require auth headers.

---

## 4. Online Mode

Sends real messages to Teams from a locally running function app. Uses Azurite for local storage but with real conversation references seeded from Azure.

### Prerequisites

1. **Azure CLI logged in** and subscription set:
   ```bash
   az login
   az account set --subscription "<subscription-id>"
   ```

2. **RBAC role**: Your user needs `Storage Table Data Reader` on the `<storage-account-name>` storage account to copy conversation references. RBAC propagation takes 1-2 minutes after assignment.

3. **Azurite running** with `--skipApiVersionCheck` (required for .NET 10 Azure SDK compatibility):
   ```bash
   azurite --silent --location /tmp/azurite --skipApiVersionCheck
   ```

### Setup

```bash
./setup-local.sh online
```

This will:
1. Verify prerequisites (az CLI, jq, Azurite connectivity)
2. Create the `conversationreferences` table and `notifications` queue in Azurite
3. Copy all conversation references from Azure Table Storage into Azurite
4. Generate `local.settings.json` with real config and channel aliases

Start the function app with `func host start`, then send a test message using the same curl pattern shown in the Offline Mode section, replacing the alias with a real one. Expected: HTTP 202, message appears in the Teams channel within seconds.

### Known Issues (Online Mode)

- **Azurite `--skipApiVersionCheck`**: Required because .NET 10 Azure SDK sends a storage API version that Azurite does not yet recognize. Start Azurite with this flag or upgrade Azurite when a newer version adds support.
- **`LastUsed` update warning**: After message delivery you may see `Failed to update LastUsed ... DateTime has a Kind of Local`. This is a non-blocking issue caused by how Azurite returns DateTime values. Messages still deliver successfully.

---

## 5. Running Tests

The test project uses xUnit and Moq covering all functions, services, middleware, and models.

```bash
# Run all tests
dotnet test tests/TeamsNotificationBot.Tests/

# Run with verbose output
dotnet test tests/TeamsNotificationBot.Tests/ --verbosity normal

# Run a specific test class
dotnet test tests/TeamsNotificationBot.Tests/ --filter "FullyQualifiedName~NotifyFunctionTests"

# Run only integration tests
dotnet test tests/TeamsNotificationBot.Tests/ --filter "FullyQualifiedName~Integration"
```

### Test Coverage

| Directory | What It Covers |
|-----------|----------------|
| `Functions/` | All HTTP triggers (Notify, Alert, Send, Health, CheckIn, GetAliases), queue triggers (QueueProcessor, BotOperations), and timer trigger (PoisonQueueMonitor) |
| `Models/` | Request validation (NotificationRequest) and Adaptive Card security (AdaptiveCardValidator) |
| `Services/` | Alias CRUD, queue management, idempotency, bot handler command routing, and all card builders (Alert, Poison, SetupGuide, CreateAlias) |
| `Middleware/` | EasyAuth header parsing, role-based authorization, and rate limiting |
| `Integration/` | End-to-end notify flow, queue serialization round-trip, and service integration tests with Azurite |

---

## 6. VS Code Debugging

The `.vscode/` directory at the repository root is pre-configured. Press **F5** to build, start `func host start` with the debugger attached, and set breakpoints in any `.cs` file. Recommended extensions (prompted on first open): Azure Functions, C# Dev Kit, and Azurite.

---

## 7. Project Structure

```
.vscode/                 # VS Code config (launch, tasks, extensions)
global.json              # .NET SDK version pin

src/TeamsNotificationBot/
  Functions/           # Azure Function triggers (HTTP + Queue + Timer)
  Helpers/             # Shared utilities (API responses, app info)
  Middleware/          # EasyAuth + role-based auth middleware
  Models/              # Request/response models, validation, entities
  Services/            # Bot service, alias service, queue management,
                       #   card builders, idempotency service
  Program.cs           # DI setup, storage config
  host.json            # Functions host configuration
  appsettings.json     # M365 Agents SDK auth config
  openapi.yaml         # OpenAPI specification
  app-requirements.json # App requirements for Terraform module and Teams manifest
  setup-local.sh       # Local dev setup script (offline/online)

tests/TeamsNotificationBot.Tests/
  Functions/           # Function unit tests
  Models/              # Model/validation tests
  Services/            # Service tests (including card builders)
  Middleware/          # Auth and rate limiting tests
  Integration/         # Integration tests (storage, end-to-end flows)
  Helpers/             # Test utilities
```

---

## 8. Troubleshooting (Local Development)

| Symptom | Cause | Fix |
|---------|-------|-----|
| `func host start` fails with storage error | Azurite not running | Start Azurite: `azurite --silent --location /tmp/azurite --skipApiVersionCheck` |
| `API version not supported by Azurite` | Missing `--skipApiVersionCheck` flag | Restart Azurite with the flag. This is required because .NET 10 Azure SDK uses a newer storage API version. |
| `setup-local.sh online` fails with permissions error | Missing RBAC role or wrong subscription | Run `az account set --subscription "<subscription-id>"` and assign `Storage Table Data Reader` to your user. |
| `setup-local.sh online` hangs at "Checking prerequisites" | Azurite not running (curl timeout) | Start Azurite first, then run the script. |
| HTTP 401 on curl request | Missing EasyAuth simulation headers | Include both `X-MS-CLIENT-PRINCIPAL-ID` and `X-MS-CLIENT-PRINCIPAL` headers. See the curl examples in Offline Mode above. |
| HTTP 403 on curl request | Missing `Notifications.Send` role in claims | Ensure the base64-encoded `X-MS-CLIENT-PRINCIPAL` header includes `{"typ":"roles","val":"Notifications.Send"}` in the claims array. |
| Message queued but not delivered (offline) | Expected behavior | `TEAMS_INTEGRATION_DISABLED=true` skips bot delivery. Use online mode for real Teams delivery. |
| `QueueNotFound` error on notify | Queue not created in Azurite | Re-run `./setup-local.sh` (both modes create the required queues). Alternatively, ensure Azurite was running before `func host start`. |
| Port 7071 already in use | Another instance of `func host start` running | Kill the existing process: `lsof -ti:7071 \| xargs kill` or use `func host start --port 7072`. |
| `dotnet test` fails with build errors | Stale build artifacts after framework change | Delete `obj/` and `bin/` directories in both `src/TeamsNotificationBot/` and `tests/TeamsNotificationBot.Tests/`, then rebuild. |
| Online mode: messages delivered but alias not found | Alias not seeded into Azurite | Re-run `./setup-local.sh online` to re-copy aliases from Azure. Or use the `set-alias` bot command in Teams first. |

---

## See Also

- [Architecture](architecture.md) -- system design and message flow diagrams
- [Authentication](authentication.md) -- Entra ID configuration and token flow
- [Troubleshooting](troubleshooting.md) -- production troubleshooting and KQL queries
- [Contributing](contributing.md) -- build, test, and contribution guidelines
