# Local Development Guide

## Prerequisites

| Tool | Install |
|------|---------|
| .NET 10 SDK | `dotnet --version` should show `10.x` |
| Azure Functions Core Tools v4 | `func --version` should show `4.x` |
| Azurite | `npm install -g azurite` |
| jq | `sudo apt install jq` (needed for online mode) |
| Azure CLI | Only needed for online mode |

## Quick Start

```bash
cd src/TeamsNotificationBot

# Generate local.settings.json (offline mode — no Azure access needed)
./setup-local.sh offline

# Start Azurite
azurite --silent --location /tmp/azurite --skipApiVersionCheck &

# Start the function app
func host start
```

Functions available at:

| Function | URL |
|----------|-----|
| Notify | `POST http://localhost:7071/api/notify/{alias}` |
| CheckIn | `POST http://localhost:7071/api/checkin` |
| GetAliases | `GET http://localhost:7071/api/aliases` |
| BotMessages | `POST http://localhost:7071/api/messages` |
| QueueProcessor | Queue trigger (automatic) |

---

## Modes

### Offline Mode (default)

Runs entirely locally with mock values. No Azure access, no real Teams delivery.

```bash
./setup-local.sh offline
```

This generates `local.settings.json` with:
- `TEAMS_INTEGRATION_DISABLED=true` — bot service calls are skipped
- Mock channel aliases (`test`, `diagnostics`)
- Azurite for storage (`UseDevelopmentStorage=true`)

Test the HTTP API (simulate EasyAuth headers for local dev):

```bash
curl -X POST http://localhost:7071/api/notify/test \
  -H "Content-Type: application/json" \
  -H "X-MS-CLIENT-PRINCIPAL-ID: local-test-user" \
  -H "X-MS-CLIENT-PRINCIPAL: $(echo '{"auth_typ":"aad","claims":[{"typ":"roles","val":"Notifications.Send"}],"name_typ":"name","role_typ":"roles"}' | base64 -w0)" \
  -d '{"message": "hello", "format": "text"}'
```

The message will be queued and processed by the QueueProcessor, but since Teams integration is disabled, no actual message is sent. You'll see the full processing flow in the function host logs.

### Online Mode

Sends real messages to Teams from a locally running function app. Uses Azurite for storage but with real conversation references seeded from Azure.

#### Prerequisites

1. **Environment variables** set for your deployment:
   ```bash
   export BOT_APP_ID="<your-bot-app-registration-client-id>"
   export TENANT_ID="<your-azure-ad-tenant-id>"
   export KEY_VAULT="<your-key-vault-name>"
   export STORAGE_ACCOUNT="<your-storage-account-name>"
   ```

2. **Azure CLI logged in** and subscription set:
   ```bash
   az login
   az account set --subscription "<your-subscription>"
   ```

3. **RBAC role**: Your user needs `Storage Table Data Reader` on the storage account. If missing:
   ```bash
   az role assignment create \
     --role "Storage Table Data Reader" \
     --assignee "$(az ad signed-in-user show --query id -o tsv)" \
     --scope "$(az storage account show --name $STORAGE_ACCOUNT --query id -o tsv)"
   ```
   RBAC propagation takes 1-2 minutes.

4. **Azurite running** with the `--skipApiVersionCheck` flag (required because .NET 10's Azure SDK uses an API version newer than what Azurite 3.35.0 supports):
   ```bash
   azurite --silent --location /tmp/azurite --skipApiVersionCheck
   ```

#### Setup

```bash
./setup-local.sh online
```

This will:
1. Verify prerequisites (az CLI, jq, Azurite connectivity, env vars)
2. Fetch `bot-client-secret` from Key Vault
3. Create the `conversationreferences` table and `notifications` queue in Azurite
4. Copy all conversation references from Azure Table Storage into Azurite
5. Generate `local.settings.json` with real secrets and real channel aliases

#### Test end-to-end delivery

```bash
# Start the function app
func host start

# In another terminal — send a test message with EasyAuth headers
curl -X POST http://localhost:7071/api/notify/my-alias \
  -H "Content-Type: application/json" \
  -H "X-MS-CLIENT-PRINCIPAL-ID: local-test-user" \
  -H "X-MS-CLIENT-PRINCIPAL: $(echo '{"auth_typ":"aad","claims":[{"typ":"roles","val":"Notifications.Send"}],"name_typ":"name","role_typ":"roles"}' | base64 -w0)" \
  -d '{"message": "Hello from local dev", "format": "text"}'
```

Expected: HTTP 202, message appears in the Teams channel within a few seconds.

#### Known Issues (online mode)

- **Azurite `--skipApiVersionCheck`**: Required because .NET 10 Azure SDK sends API version `2026-02-06` which Azurite 3.35.0 doesn't recognize. Start Azurite with this flag or upgrade Azurite when a newer version is available.
- **`LastUsed` update warning**: After message delivery you may see `Failed to update LastUsed ... DateTime has a Kind of Local`. This is a non-blocking issue caused by how Azurite returns DateTime values. Messages still deliver successfully.

---

## Running Tests

The test project is at `tests/TeamsNotificationBot.Tests/`.

```bash
# From the repository root
dotnet test tests/TeamsNotificationBot.Tests/

# Run with verbose output
dotnet test tests/TeamsNotificationBot.Tests/ --verbosity normal

# Run a specific test class
dotnet test tests/TeamsNotificationBot.Tests/ --filter "FullyQualifiedName~NotifyFunctionTests"
```

### Test Coverage

| Test File | What It Covers |
|-----------|----------------|
| `Functions/NotifyFunctionTests.cs` | HTTP endpoint: unknown alias (404), wrong content-type (415), valid request (202+enqueue), invalid JSON (400) |
| `Functions/QueueProcessorFunctionTests.cs` | Queue processing: text delivery, card delivery, diagnostic on failure, Teams-disabled skip |
| `Functions/CheckInFunctionTests.cs` | Health check: 200 response, diagnostic message queuing |
| `Models/AdaptiveCardValidatorTests.cs` | Security: rejects `Action.OpenUrl`, accepts valid cards, edge cases |
| `Models/NotificationRequestTests.cs` | Validation: text+string, text+object, card+object, unsupported format |
| `Services/AliasServiceTests.cs` | Alias operations: CRUD, case-insensitive lookup |

---

## VS Code Debugging

The `.vscode/` directory is pre-configured. Press **F5** to:
1. Build the project
2. Start `func host start` with debugger attach
3. Set breakpoints in any `.cs` file

Recommended extensions (prompted on first open):
- Azure Functions
- C# Dev Kit
- Azurite

---

## Project Structure

```
src/TeamsNotificationBot/
  Functions/           # Azure Function triggers (HTTP + Queue)
  Middleware/           # EasyAuth + role-based auth middleware
  Models/              # Request/response models, validation
  Services/            # Bot service, channel alias service
  Helpers/             # Shared utilities (API response, app info)
  .vscode/             # Shared VS Code config (launch, tasks, extensions)
  Program.cs           # DI setup, dual-path storage config
  host.json            # Functions host configuration
  setup-local.sh       # Local dev setup script

tests/TeamsNotificationBot.Tests/
  Functions/           # Function unit tests
  Models/              # Model/validation tests
  Services/            # Service tests
  Middleware/           # Auth middleware tests
  Integration/         # Integration tests (Azurite-backed)
  Helpers/             # Test utilities (HttpRequestHelper)
```

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `func host start` fails with storage error | Azurite not running | Start Azurite: `azurite --silent --location /tmp/azurite --skipApiVersionCheck` |
| `API version not supported by Azurite` | Missing `--skipApiVersionCheck` flag | Restart Azurite with the flag |
| `setup-local.sh online` fails with env var error | Missing environment variables | Export `BOT_APP_ID`, `TENANT_ID`, `KEY_VAULT`, `STORAGE_ACCOUNT` |
| `setup-local.sh online` fails with permissions error | Missing RBAC role or wrong subscription | Check `az account show` and assign `Storage Table Data Reader` |
| `setup-local.sh online` hangs at "Checking prerequisites" | Azurite not running (curl timeout) | Start Azurite first, then run the script |
| HTTP 401 on curl request | Missing EasyAuth headers | Include `X-MS-CLIENT-PRINCIPAL-ID` and `X-MS-CLIENT-PRINCIPAL` headers (see test examples above) |
| Message queued but not delivered (offline) | Expected behavior | `TEAMS_INTEGRATION_DISABLED=true` skips bot delivery. Use online mode for real delivery. |
| `QueueNotFound` error on notify | Queue not created in Azurite | Re-run `./setup-local.sh online` (or offline + manually start Azurite before `func host start`) |
