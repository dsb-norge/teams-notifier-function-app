# Teams Notification Bot - Function App

.NET 10 isolated-worker Azure Function App that sends proactive notifications to Microsoft Teams channels via the Bot Framework SDK.

## Endpoints

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| POST | `/api/notify/{alias}` | Bearer (Entra ID) | Queue a notification for a channel alias |
| POST | `/api/messages` | Bot Framework JWT | Teams webhook (conversationUpdate events) |
| POST | `/api/checkin` | Bearer (Entra ID) | Health check with optional diagnostic message |
| GET | `/api/aliases` | Bearer (Entra ID) | List configured channel aliases |

## Architecture

```
HTTP Request → NotifyFunction → Queue ("notifications") → QueueProcessorFunction → Bot Framework → Teams
```

Messages are enqueued by the HTTP trigger and processed asynchronously by the queue trigger, which looks up the conversation reference from Table Storage and sends via the Bot Framework SDK.

## Local Development

See **[local-development.md](local-development.md)** for detailed setup instructions covering:
- Offline mode (mock values, no Azure access)
- Online mode (real Teams delivery from localhost)
- Running tests
- VS Code debugging

Quick start:
```bash
./setup-local.sh offline
azurite --silent --location /tmp/azurite --skipApiVersionCheck &
func host start
```

## Deployment

Deployed to Azure via Terraform (Flex Consumption plan). Infrastructure is managed by the companion [terraform-azurerm-teams-notification-bot-lz](https://github.com/dsb-norge/terraform-azurerm-teams-notification-bot-lz) module.
