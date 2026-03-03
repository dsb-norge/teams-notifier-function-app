# System Architecture

A Teams Notification Bot that routes notifications from external systems to Microsoft Teams channels via REST API. It supports Azure Monitor alerts (Common Alert Schema), interactive bot commands, alias-based routing, Adaptive Cards, and proactive messaging. See the [README](../README.md) for quick start and deployment instructions.

---

## Message Flow Diagrams

### M2: Notify Flow

External systems send notifications through the REST API. Messages are validated, enqueued, and delivered asynchronously to Teams.

```mermaid
sequenceDiagram
    participant Client
    participant FuncApp as Function App<br/>(HTTP trigger)
    participant EasyAuth as EasyAuth<br/>(Entra ID)
    participant RateLimit as ThrottlingTroll<br/>(Rate Limiter)
    participant Queue as notifications<br/>queue
    participant QueueProc as QueueProcessor<br/>(Queue trigger)
    participant Tables as Table Storage<br/>(aliases + conversationreferences)
    participant Bot as CloudAdapter<br/>(M365 Agents SDK)
    participant Teams as Bot Framework<br/>Connector → Teams

    Client->>FuncApp: POST /api/v1/notify/{alias}<br/>Authorization: Bearer token
    FuncApp->>EasyAuth: Validate token (Entra ID)
    EasyAuth-->>FuncApp: X-MS-CLIENT-PRINCIPAL-ID + claims
    FuncApp->>FuncApp: Check role: Notifications.Send
    FuncApp->>RateLimit: Rate limit check<br/>(60 req/60s per principal)
    RateLimit-->>FuncApp: OK / 429 Too Many Requests
    FuncApp->>FuncApp: Validate JSON schema + 28KB limit
    FuncApp->>FuncApp: Check Idempotency-Key header
    FuncApp->>Queue: Enqueue message
    FuncApp-->>Client: 202 Accepted { messageId, correlationId }

    Queue->>QueueProc: Dequeue message
    QueueProc->>Tables: Resolve alias → conversation reference
    QueueProc->>Bot: Send text or Adaptive Card
    Bot->>Teams: POST activity to Teams channel

    Note over Queue,QueueProc: maxDequeueCount=5<br/>Failed messages move to<br/>notifications-poison queue
```

### M3: Alert Flow

Azure Monitor alerts arrive via Action Groups using the Common Alert Schema. The alert payload is transformed into a color-coded Adaptive Card and routed through the same queue pipeline.

```mermaid
sequenceDiagram
    participant Monitor as Azure Monitor
    participant ActionGroup as Action Group<br/>(AAD auth)
    participant FuncApp as Function App<br/>(HTTP trigger)
    participant EasyAuth as EasyAuth<br/>(Entra ID)
    participant AlertFn as AlertFunction
    participant Queue as notifications<br/>queue
    participant QueueProc as QueueProcessor<br/>(Queue trigger)
    participant Tables as Table Storage
    participant Bot as CloudAdapter
    participant Teams as Teams

    Monitor->>ActionGroup: Alert fires
    ActionGroup->>FuncApp: POST /api/v1/alert/{alias}<br/>Entra ID token (AAD auth)
    FuncApp->>EasyAuth: Validate token
    EasyAuth-->>FuncApp: Authenticated principal
    FuncApp->>FuncApp: Check role: Notifications.Send
    FuncApp->>AlertFn: Parse Common Alert Schema
    AlertFn->>AlertFn: Validate schemaId = azureMonitorCommonAlertSchema
    AlertFn->>AlertFn: Build Adaptive Card<br/>(severity colors, alert details)
    AlertFn->>Queue: Enqueue as adaptive-card message
    AlertFn-->>ActionGroup: 202 Accepted

    Queue->>QueueProc: Dequeue message
    QueueProc->>Tables: Resolve alias → conversation reference
    QueueProc->>Bot: Send Adaptive Card
    Bot->>Teams: POST activity to Teams channel
```

### M4: Inbound Bot Messages

Users interact with the bot in Teams via @mentions and direct messages. The Bot Framework Connector delivers activities to the function app, where the M365 Agents SDK validates JWT tokens and dispatches commands.

```mermaid
sequenceDiagram
    participant User as Teams User
    participant Teams as Microsoft Teams
    participant Connector as Bot Framework<br/>Connector
    participant FuncApp as Function App<br/>POST /api/messages
    participant Adapter as CloudAdapter<br/>(JWT validation)
    participant Handler as TeamsBotHandler
    participant Tables as Table Storage

    User->>Teams: @mention bot + command
    Teams->>Connector: Route activity
    Connector->>FuncApp: POST /api/messages<br/>Authorization: Bearer JWT
    Note over FuncApp: /api/messages excluded<br/>from EasyAuth
    FuncApp->>Adapter: ProcessAsync(request, response, agent)
    Adapter->>Adapter: Validate Bot Framework JWT<br/>(M365 Agents SDK)
    Adapter->>Handler: OnMessageActivityAsync

    alt help
        Handler-->>User: Command reference card
    else set-alias <name>
        Handler->>Tables: Create alias for current channel
        Handler-->>User: Confirmation
    else create-alias
        Handler-->>User: Interactive Adaptive Card form
    else remove-alias <name>
        Handler->>Tables: Delete alias
        Handler-->>User: Confirmation
    else list-aliases
        Handler->>Tables: Query all aliases
        Handler-->>User: Alias list card
    else checkin
        Handler-->>User: Health status
    else queue-status / queue-peek / queue-retry
        Handler->>Tables: Query queue metrics
        Handler-->>User: Queue management card
    else setup-guide
        Handler-->>User: API auth setup instructions
    else delete-post
        Handler->>Teams: Delete referenced message
    end
```

---

## Components

### Function App

Azure Functions Flex Consumption plan (FC1 SKU), running .NET 10 on the isolated worker model. Per-function scaling means each trigger type gets its own instance group.

| Trigger Type | Function | Route / Queue | Purpose |
|---|---|---|---|
| HTTP | Health | `GET /api/health` | Liveness probe |
| HTTP | Notify | `POST /api/v1/notify/{alias}` | Send notification to alias |
| HTTP | Alert | `POST /api/v1/alert/{alias}` | Azure Monitor alert webhook |
| HTTP | CheckIn | `POST /api/v1/checkin/{alias}` | Deployment verification ping |
| HTTP | Send | `POST /api/v1/send` | Direct-target send (by team/channel/user ID) |
| HTTP | GetAliases | `GET /api/v1/aliases` | List aliases (debug mode only) |
| HTTP | OpenApi | `GET /api/v1/openapi.yaml` | OpenAPI specification |
| HTTP | BotMessages | `POST /api/messages` | Bot Framework messaging endpoint |
| Queue | QueueProcessor | `notifications` | Deliver queued messages to Teams |
| Queue | BotOperations | `botoperations` | Internal ops (channel enumeration on install) |
| Queue | NotificationsPoisonMonitor | `notifications-poison` | Alert on failed notifications |
| Queue | BotOperationsPoisonMonitor | `botoperations-poison` | Alert on failed bot operations |

**Authentication layers:**

- **API endpoints** (`/api/v1/*`): EasyAuth (Entra ID) validates Bearer tokens; AuthMiddleware checks the `Notifications.Send` app role; ThrottlingTroll enforces rate limits (60 req/60s per principal).
- **Bot endpoint** (`/api/messages`): Excluded from EasyAuth. CloudAdapter validates Bot Framework JWT tokens internally via the M365 Agents SDK.
- **Health and OpenAPI**: No authentication required.

### Storage Account

Azure Storage with shared access keys disabled. All access via RBAC (User-Assigned Managed Identity).

**Tables** (5):

| Table | Purpose |
|---|---|
| `aliases` | Maps alias names to conversation targets (channel, personal, groupChat) |
| `conversationreferences` | Stores Bot Framework conversation references (auto-populated on bot install) |
| `teamlookup` | Caches team metadata (team names) |
| `idempotencykeys` | Deduplication records with 24-hour TTL |
| `ThrottlingTrollCounters` | Rate limiter sliding window counters |

**Queues** (4):

| Queue | Purpose |
|---|---|
| `notifications` | Outbound notification messages pending delivery |
| `notifications-poison` | Notifications that failed delivery after 5 attempts |
| `botoperations` | Internal bot operations (channel enumeration, team rename) |
| `botoperations-poison` | Failed bot operations |

### Key Vault

Stores the bot app registration client secret (used for local dev tunnel scenarios). Function app accesses Key Vault references via the User-Assigned Managed Identity. Private endpoint access only.

### Bot Service

Azure Bot Service (F0 free tier, SingleTenant app type) with the Teams channel enabled. The messaging endpoint points to `https://<function-app-name>.azurewebsites.net/api/messages`. The bot app registration requires `signInAudience = AzureADMultipleOrgs` because the Bot Framework Connector authenticates from Microsoft's `botframework.com` tenant.

### Networking

Virtual network with two subnets, private endpoints, and IP restrictions. See the [Infrastructure diagram](#infrastructure--m9) below.

### Monitoring

Application Insights backed by a Log Analytics Workspace. Includes a pre-built KQL query pack with 14 saved queries covering bot traffic, function executions, MSAL token acquisition, JWT validation events, error tracking, and end-to-end request timelines.

---

## Data Model -- M8

```mermaid
erDiagram
    aliases {
        string PartitionKey "always 'alias'"
        string RowKey "alias name (lowercase)"
        string TargetType "channel | personal | groupChat"
        string TeamId "nullable"
        string ChannelId "nullable"
        string UserId "nullable"
        string ChatId "nullable"
        string Description ""
        string CreatedBy "Entra ID OID"
        string CreatedByName ""
        datetime CreatedAt ""
    }

    conversationreferences {
        string PartitionKey "team GUID | 'user' | 'chat'"
        string RowKey "channel thread ID | user OID | chat ID"
        string ConversationReference "JSON serialized"
        string ConversationType "channel | personal | groupChat"
        string TeamName "nullable"
        string ChannelName "nullable"
        string UserName "nullable"
        datetime InstalledAt ""
        datetime LastUpdated ""
    }

    teamlookup {
        string PartitionKey "team GUID"
        string RowKey "always empty"
        string TeamName ""
    }

    idempotencykeys {
        string PartitionKey "operation (e.g. 'notify')"
        string RowKey "idempotency key"
        string ResponseBody "cached JSON response"
        int StatusCode ""
        datetime CreatedAt "expires after 24h"
    }

    ThrottlingTrollCounters {
        string PartitionKey "counter key"
        string RowKey "window identifier"
        int Count "request count"
    }

    notifications_queue {
        string messageId "unique message ID"
        string alias "target alias name"
        string message "text or Adaptive Card JSON"
        string format "text | adaptive-card"
        datetime enqueuedAt ""
    }

    notifications_poison_queue {
        string content "original message after 5 failures"
    }

    aliases ||--o{ conversationreferences : "resolves via TargetType + keys"
    notifications_queue ||--o| notifications_poison_queue : "after maxDequeueCount failures"
```

---

## Infrastructure -- M9

```mermaid
flowchart TD
    subgraph Internet
        Client["API Clients<br/>(Bearer token)"]
        TeamsCloud["Microsoft Teams"]
        MonitorAG["Azure Monitor<br/>Action Groups"]
    end

    subgraph Azure["Azure Resource Group: &lt;resource-group&gt;"]

        subgraph VNet["Virtual Network (10.0.0.0/16)"]
            subgraph SubnetFunc["snet-function-app (10.0.0.0/24)<br/>Delegation: Microsoft.App/environments"]
                FuncApp["Function App<br/>&lt;function-app-name&gt;<br/>Flex Consumption FC1<br/>.NET 10 isolated worker"]
            end
            subgraph SubnetPE["snet-private-endpoints (10.0.1.0/24)"]
                PE_Blob["PE: blob"]
                PE_Queue["PE: queue"]
                PE_Table["PE: table"]
                PE_Vault["PE: vault"]
            end
        end

        Storage["Storage Account<br/>&lt;storage-account-name&gt;<br/>Tables + Queues<br/>Keys disabled, RBAC only"]
        KV["Key Vault<br/>&lt;key-vault-name&gt;"]
        UAMI["User-Assigned<br/>Managed Identity<br/>&lt;uami-client-id&gt;"]
        BotSvc["Bot Service (F0)<br/>SingleTenant<br/>Teams channel"]
        AppInsights["Application Insights"]
        LAW["Log Analytics<br/>Workspace"]

        subgraph DNS["Private DNS Zones"]
            DNS_Blob["privatelink.blob.core.windows.net"]
            DNS_Queue["privatelink.queue.core.windows.net"]
            DNS_Table["privatelink.table.core.windows.net"]
            DNS_Vault["privatelink.vaultcore.azure.net"]
        end
    end

    Client -->|"HTTPS + Bearer"| FuncApp
    TeamsCloud -->|"Bot Framework JWT"| BotSvc
    BotSvc -->|"/api/messages"| FuncApp
    MonitorAG -->|"Entra ID token"| FuncApp
    FuncApp -->|"VNet integration"| SubnetFunc

    PE_Blob --> Storage
    PE_Queue --> Storage
    PE_Table --> Storage
    PE_Vault --> KV

    FuncApp -.->|"private endpoint"| PE_Blob
    FuncApp -.->|"private endpoint"| PE_Queue
    FuncApp -.->|"private endpoint"| PE_Table
    FuncApp -.->|"private endpoint"| PE_Vault

    UAMI -.->|"RBAC"| Storage
    UAMI -.->|"RBAC"| KV
    FuncApp -.->|"identity"| UAMI

    FuncApp -->|"outbound"| BotSvc
    AppInsights --> LAW

    DNS_Blob -.-> PE_Blob
    DNS_Queue -.-> PE_Queue
    DNS_Table -.-> PE_Table
    DNS_Vault -.-> PE_Vault
```

**IP restrictions on the Function App:**

| Priority | Rule | Source |
|---|---|---|
| 100 | AllowAzureBotService | `AzureBotService` service tag |
| 101-102 | AllowTeamsService | `52.112.0.0/14`, `52.122.0.0/15` (M365 infrastructure) |
| 103 | AllowAzureMonitorActionGroup | `ActionGroup` service tag |
| 200+ | Management IPs | Configurable per deployment |
| Default | Deny | All other traffic |

**Storage network rules:** Default deny with `AzureServices` bypass. Function app accesses storage exclusively through private endpoints via VNet integration. Management IPs are allowed for Terraform operations.

---

## Queue Processing

Azure Functions Flex Consumption uses **per-function scaling**: each queue trigger function runs on its own instance group, independent of HTTP triggers and other queue triggers. This means `QueueProcessor` (notifications), `BotOperations`, `NotificationsPoisonMonitor`, and `BotOperationsPoisonMonitor` each scale independently.

**Queue configuration** (from `host.json`):

| Setting | Value | Description |
|---|---|---|
| `maxDequeueCount` | 5 | Attempts before moving to poison queue |
| `batchSize` | 16 | Messages fetched per poll |
| `maxPollingInterval` | 2 seconds | Polling frequency |
| `visibilityTimeout` | 30 seconds | Retry delay after failed processing |

**Poison queue monitoring:**

When a message exceeds `maxDequeueCount`, it moves to the corresponding `-poison` queue. The `PoisonQueueMonitorFunction` triggers on poison queue messages and sends an Adaptive Card alert to the channel configured by the `PoisonAlertAlias` environment variable. To prevent cascading failures (creating `-poison-poison` queues), the monitor function catches all exceptions internally.

**Bot commands for queue management:**

| Command | Description |
|---|---|
| `queue-status` | Show message counts for all queues |
| `queue-peek` | Preview messages in a poison queue |
| `queue-retry` | Move a specific poison message back for reprocessing |
| `queue-retry-all` | Move all poison messages back for reprocessing |

---

## Contract File

The file `app/app-contract.json` serves as the shared interface between the Function App code and the Terraform infrastructure module. It is generated by `scripts/generate-contract.sh` and declares:

- **Queues**: All queue names the app reads from or writes to
- **Tables**: All table names the app requires
- **Routes**: All HTTP trigger routes and their paths
- **Environment variables**: Required app settings grouped by category (identity/storage, telemetry, bot identity, app config)
- **EasyAuth configuration**: Platform auth settings including excluded paths
- **Bot configuration**: App type, scopes, notification-only flag, and command lists

The Terraform module reads this contract to create storage queues, configure app settings, and ensure infrastructure matches the app's expectations. This decouples infrastructure changes from application changes while maintaining a verifiable contract between the two.

---

## Related Documentation

| Document | Description |
|---|---|
| [README](../README.md) | Quick start, deployment, and usage |
| [deployment-guide.md](deployment-guide.md) | End-to-end deployment walkthrough |
| [access-and-roles.md](access-and-roles.md) | Permissions reference and RBAC guidance |
| [authentication.md](authentication.md) | Identity, auth flows, and credential management |
