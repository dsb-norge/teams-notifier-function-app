# Troubleshooting

This guide covers common issues, diagnostic queries, and monitoring for the Teams Notification Bot. For architecture details, see [architecture.md](architecture.md). For authentication specifics, see [authentication.md](authentication.md).

---

## 1. Common Issues

### Bot Not Responding in Teams

| Symptom | Cause | Fix |
|---------|-------|-----|
| Bot silently drops all messages from Teams | `minimum_tls_version` set to `1.3` on the function app | Bot Framework Connector uses TLS 1.2. Setting `1.3` blocks the TLS handshake before the HTTP layer. Change to `1.2`. Direct Line and proactive messaging are unaffected. |
| Bot returns no response; no logs for `/api/messages` | EasyAuth is intercepting the bot endpoint | Add `/api/messages` to the `excludedPaths` list in the EasyAuth configuration. EasyAuth strips the Bot Framework `Authorization` header, preventing token validation. |
| Bot created but Teams never delivers messages | Missing service principal for the bot app registration | Run `az ad sp create --id <bot-app-id>`. Without a service principal in the tenant, MSAL token acquisition fails silently. |
| Bot was deleted and recreated; Teams still not working | Bot Framework directory sync stale after delete/recreate | In Azure Portal, navigate to Bot > Configuration and click "Manage" next to the App ID to force a re-sync with Bot Framework's internal routing directory. |
| Bot commands work in 1:1 chat but not in channels | User not @mentioning the bot | In channel conversations, the bot only receives messages where it is explicitly @mentioned. Thread replies to the bot's own messages also require @mention unless `ChannelMessage.Read.Group` RSC permission is granted. |
| Bot type change needed (e.g., MultiTenant to SingleTenant) | Bot Service `msaAppType` is immutable after creation | Delete and recreate the Bot Service resource. You cannot PATCH or PUT to change `msaAppType` or `msaAppId`. |

### API Issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| `401 Unauthorized` on `/api/v1/notify/{alias}` | Missing or expired Bearer token | Acquire a fresh token from Entra ID with the correct audience (`api://<api-app-id>`). Tokens expire after 1 hour by default. |
| `403 Forbidden` on `/api/v1/notify/{alias}` | Caller missing `Notifications.Send` app role | Assign the `Notifications.Send` app role to the calling service principal in the API's Entra ID app registration. See [access-and-roles.md](access-and-roles.md). |
| `403 Forbidden` despite having the role | Wrong audience (`aud`) claim in the token | The token must target `api://<api-app-id>`. If using a different resource URI, the EasyAuth middleware rejects the request. |
| `429 Too Many Requests` | Rate limit exceeded (60 requests per 60 seconds per principal) | Back off and retry after the `Retry-After` header value. Rate limits are per-principal, not per-IP. |
| `terraform apply` fails with `AadWebhookResourceNotOwnedByCaller` when `alert_target_alias` is set | The deploying identity is not an owner of the API app registration | Add the deploying identity as an owner: `az ad app owner add --id <api-app-id> --owner-object-id <deployer-oid>`. See [prerequisites section 3.2](prerequisites.md#32-api-app-registration). |

### Delivery Issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| `404 Not Found` on `/api/v1/notify/{alias}` | The alias does not exist | Create the alias first by using the `set-alias` bot command in a Teams channel where the bot is installed. Use `GET /api/v1/aliases` to list existing aliases. |
| Message returns `202 Accepted` but never appears in Teams | Conversation reference is stale (team restructured, bot reinstalled) | Uninstall and reinstall the bot in the target team. This triggers a `conversationUpdate` event that refreshes the stored conversation reference. |
| Messages appearing in the poison queue | Repeated delivery failures after 5 attempts | Use the `queue-status` bot command to view poison queue depth. Use `queue-peek` to inspect individual failed messages and their error details. Common causes: stale conversation references, expired bot credentials. |
| Duplicate messages delivered to a channel | Idempotency key not provided, or retry after transient failure | Include an `Idempotency-Key` header in the request. The system deduplicates requests with the same key and operation type. |

### updown.io Webhook Issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| updown reports the webhook failing / `404 Not Found` | The `{token}` is wrong, or the webhook was removed/rotated | Recreate or rotate the URL with `rotate-webhook <id>` (or `create-webhook`) and update the recipient URL in updown. The URL is shown only once. |
| updown delivers but no card appears in Teams | The event type is filtered out for this webhook (e.g. `check.performance_drop` is off by default), or the conversation reference is stale | Check the filter with `list-webhooks`; adjust with `configure-webhook <id> events …`. For stale references, reinstall the bot in the target conversation. |
| Cards never arrive but updown shows 200 | The message enqueued but delivery failed (poison queue) | Use `queue-status` / `queue-peek notifications-poison`. |
| updown shows the webhook succeeding (200) but you suspect a bad payload | Malformed/unparseable JSON is deliberately answered **200** (logged at Error, nothing enqueued) so updown doesn't retry a body that can't parse. Unknown/filtered event types are also skipped with 200. | Enable `UpdownWebhook__DebugLogPayload=true` to see the raw body; check App Insights `traces` for the parse-error log. Only transient enqueue failures return 5xx (so genuine retries happen). |
| Need to see exactly what updown sent | Payload troubleshooting | Set app setting `UpdownWebhook__DebugLogPayload=true` (off by default) to log the raw body (sanitized, no token) at Debug, then inspect App Insights `traces`. Turn it off afterwards. |
| Verifying delivery before go-live | — | Use updown's *recipients → test* page to send a sample payload, and confirm a card lands. Source IPs are logged (search `traces` for `SourceIp`) — this is the data needed before enabling any IP allowlist. |
| Worried a leaked URL could post fake cards | updown does not sign requests; the token is the only credential | Rotate immediately with `rotate-webhook <id>`. Also enable the source-IP allowlist in `enforce` mode (below) so a leaked token also needs an updown-owned source IP. Cards have no actionable buttons and are labelled *unverified sender*. |
| updown deliveries suddenly get `403` | Source-IP filter is in `enforce` mode and updown added/changed egress IPs not yet in the allowlist | Run `show-ip-allow-list updown` to inspect; `update-ip-allow-list updown` to refresh from `ips.updown.io`. The list also refreshes lazily, and updown retries webhooks over several days, so a brief 403 self-heals. If needed, set `UpdownWebhook__IpFilterMode=log-only` temporarily. |
| Your own curl to `/ingest` gets `403` during testing | The IP filter is enforcing and your IP isn't updown's | Set `UpdownWebhook__IpFilterMode=off` (or `log-only`) for manual verification, then restore `enforce`. See [local-development.md](local-development.md) for the local ingest test procedure. |
| Want to confirm what source IPs updown uses | Observing before enforcing | Deploy with `IpFilterMode=log-only` (default), send updown's test payload, and read the `SourceIp=` log lines (App Insights `traces`); confirm they appear via `show-ip-allow-list updown`, then flip to `enforce`. |

### Function App Issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| Storage access denied (403) at runtime | UAMI missing required RBAC roles | Assign `Storage Blob Data Owner`, `Storage Queue Data Contributor`, and `Storage Table Data Contributor` to the function app's user-assigned managed identity on the storage account. |
| Function app returning 503 | `AzureWebJobsStorage` misconfigured | Verify that identity-based storage settings are used (`__credential`, `__clientId`, `__blobServiceUri`, `__queueServiceUri`, `__tableServiceUri`). Do not use a connection string. |
| Alert / notify queue messages enqueued but never delivered to Teams; queue function never wakes from scale-to-zero | When the function app is VNet-integrated behind a firewall, the underlying Container Apps platform can't acquire managed-identity tokens via the regional endpoints, which silently breaks the platform's scale-controller signalling. Fingerprint: ~24 firewall denies per minute from the integration subnet to `control-<region>.identity.azure.net` and `<region>.login.microsoft.com`. | Allow the FQDNs in the Terraform module's `required_outbound_fqdns.flex_consumption_platform` category outbound (HTTPS/443) from the integration subnet. See the [module README](https://github.com/dsb-norge/terraform-azurerm-teams-notification-bot-lz#required-outbound-network-access-important-for-byon-consumers). |
| Deployment fails with `Failed (Active)` after a 100-second timeout in Deployment Center; SyncTriggers SSL/TLS handshake errors in `AppExceptions` | The function host hairpins out through the firewall to call its own public hostname for SyncTriggers and the deployment-sync probe. Missing firewall egress rule for the app's own FQDN. | Allow `<function-app>.azurewebsites.net` and `<function-app>.scm.azurewebsites.net` outbound (HTTPS/443) from the integration subnet. These are exposed by the Terraform module as `required_outbound_fqdns.self_hairpin`. |
| Deployment fails from local machine | SCM endpoint requires an allowed IP | Add your IP address to the `management_ip_rules` in the Terraform module variables. Ensure you are connected to the correct network or VPN. |
| `func azure functionapp publish` does not list all functions | Normal behavior for custom-route functions | Functions with custom route prefixes (e.g., `/api/messages`) may not appear in the CLI output. Verify with `az functionapp function list --name <function-app-name> --resource-group <resource-group>`. |

### Teams App Issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| "Something went wrong" when installing the Teams app | `webApplicationInfo` in the manifest points to an invalid app ID | Ensure `webApplicationInfo.id` references a real Entra ID app registration. If SSO is not in use, remove the `webApplicationInfo` section entirely from the manifest. |
| Users cannot install the custom app | Custom app upload policy is disabled | In Teams Admin Center, go to Manage Apps and verify the app status is not blocked. Check the org-wide app settings for custom app permissions. |
| App installs successfully but bot never receives events | The app's `botId` does not match the bot registration | Verify that the `botId` in `manifest.json` matches the Entra ID app registration client ID used by the Bot Service resource. |

---

## 2. Diagnostic Queries

These KQL queries run in **Application Insights** (Logs) or **Log Analytics**. Adjust `TimeGenerated` ranges as needed.

### HTTP Traffic Analysis

```kql
AppRequests
| where TimeGenerated > ago(30m)
| project TimeGenerated, Name, Url, ResultCode, ClientIP, DurationMs
| order by TimeGenerated desc
```

### Function Execution Errors

```kql
AppTraces
| where TimeGenerated > ago(24h)
| where SeverityLevel >= 2
| extend Category = tostring(Properties.CategoryName)
| project TimeGenerated, SeverityLevel, Category, Message = substring(Message, 0, 300)
| order by TimeGenerated desc
```

### Bot Message Delivery Timeline

Traces the full lifecycle of an inbound or outbound bot message through the function app.

```kql
AppTraces
| where TimeGenerated > ago(1h)
| where Properties.CategoryName in (
    "TeamsNotificationBot.Functions.BotMessagesFunction",
    "TeamsNotificationBot.Services.TeamsBotHandler",
    "Microsoft.Agents.Authentication.Msal.MsalAuth"
  )
| extend Category = tostring(Properties.CategoryName)
| project TimeGenerated, Category, Message = substring(Message, 0, 150), OperationId
| order by TimeGenerated asc
```

### Rate Limiting Hits

```kql
AppRequests
| where TimeGenerated > ago(1h)
| where ResultCode == 429
| summarize HitCount = count() by bin(TimeGenerated, 5m), ClientIP
| order by TimeGenerated desc
```

### Queue Processing Failures

```kql
AppTraces
| where TimeGenerated > ago(24h)
| where Message has "poison" or Message has "failed" or Message has "dequeue"
| project TimeGenerated, Message, SeverityLevel
| order by TimeGenerated desc
```

### Bot Channel Traffic (Bot Service Diagnostics)

Requires `ABSBotRequests` diagnostic setting enabled on the Bot Service resource.

```kql
ABSBotRequests
| where TimeGenerated > ago(24h)
| summarize RequestCount = count() by Channel, ResultCode
| order by RequestCount desc
```

### Alias Resolution Failures

```kql
AppTraces
| where TimeGenerated > ago(24h)
| where Message has "alias" and (Message has "not found" or Message has "404")
| project TimeGenerated, Message
| order by TimeGenerated desc
```

### End-to-End Latency by Operation

```kql
AppRequests
| where TimeGenerated > ago(1h)
| where Name has "notify" or Name has "alert" or Name has "send"
| summarize
    AvgDurationMs = avg(DurationMs),
    P95DurationMs = percentile(DurationMs, 95),
    RequestCount = count()
  by Name
| order by RequestCount desc
```

---

## 3. Monitoring

### Application Insights

The primary monitoring surface. All function app telemetry flows here.

- **AppRequests**: HTTP trigger invocations (notify, alert, send, health, aliases, bot messages). This is the only HTTP traffic log available on Flex Consumption plans.
- **AppTraces**: Structured log output from all function executions including queue triggers.
- **AppExceptions**: Unhandled exceptions with full stack traces.
- **Live Metrics**: Real-time view with zero ingestion delay. Access via Azure Portal > Application Insights > Live Metrics.

### Log Analytics

The Terraform module creates a Log Analytics workspace and connects it to both Application Insights and the Function App diagnostic settings.

- **FunctionAppLogs**: Platform-level function app logs (cold starts, scaling events, host lifecycle).
- **ABSBotRequests**: Bot Service request logs showing channel distribution and response codes.
- **AllMetrics**: Platform metrics for the function app (invocations, execution units, errors).

### Alerts

Metric alerts can be configured via the `alert_target_alias` module variable. Common alert configurations:

- **Queue depth spike**: Fires when the `notifications` queue exceeds a threshold.
- **Poison queue non-empty**: Fires when messages land in the poison queue.
- **Error rate increase**: Fires when `AppRequests` with `ResultCode >= 500` exceeds a threshold.
- **Function timeout**: Fires when function execution time approaches the 5-minute limit.

### Bot Commands for Operational Monitoring

The bot itself provides monitoring commands when @mentioned in a Teams channel:

| Command | Description |
|---------|-------------|
| `queue-status` | Shows notification queue and poison queue depth |
| `queue-peek` | Previews the next message in the poison queue |
| `list-aliases` | Lists all configured channel aliases |
| `help` | Shows all available commands |

---

## 4. Known Limitations

### Flex Consumption Platform

- **`AppServiceHTTPLogs` is NOT supported** on Flex Consumption plans. The `AppRequests` table in Application Insights is the only source for HTTP traffic analysis.
- **Per-function scaling**: Queue triggers and HTTP triggers run on separate instances. Each queue function gets its own instance group. This means a queue processing spike does not affect HTTP trigger latency, but also means you cannot share in-memory state between them.
- **Cold start latency**: Flex Consumption plans may exhibit cold starts of 1-5 seconds for the first request after an idle period.

### Telemetry

- **App Insights ingestion latency**: `AppRequests` and `AppTraces` have a 2-5 minute ingestion delay. Use Live Metrics for real-time debugging.
- **Sampling**: The default sampling configuration (`maxTelemetryItemsPerSecond: 20`) may drop telemetry under high load. Increase this value or disable sampling for debugging sessions.

### Bot Framework

- **JWT middleware does not fire in isolated worker**: `AddAgentAspNetAuthentication()` registers ASP.NET Core auth middleware, but Azure Functions isolated worker does not use the ASP.NET Core middleware pipeline for HTTP triggers. CloudAdapter validates inbound tokens internally. This means JWT event handlers (OnTokenValidated, OnForbidden, OnAuthenticationFailed) will not produce log output.
- **MSAL debug logging**: The `Microsoft.Agents.Authentication.Msal.MsalAuth` category at Debug level shows the full MSAL token acquisition flow (cache hits, authority discovery, token endpoint, scopes). Enable this in `host.json` for outbound auth troubleshooting.
- **IP-based allow-listing is not officially supported**: Microsoft states that IP-based restrictions for inbound bot traffic are unreliable. JWT token validation in the Bot Framework SDK / M365 Agents SDK is the recommended security mechanism.

### Teams Channel Behavior

- **Thread replies**: `Activity.ReplyToId` is not set for thread replies. The root message ID must be extracted from `Conversation.Id` (format: `19:xxx@thread.tacv2;messageid=<id>`).
- **`isNotificationOnly: true`**: When set in the Teams manifest, @mentions do not trigger the bot, but `conversationUpdate` events are still sent on install and uninstall.

---

## See Also

- [Architecture](architecture.md) -- system design and message flow diagrams
- [Authentication](authentication.md) -- Entra ID configuration and token flow
- [Access and Roles](access-and-roles.md) -- RBAC and app role assignments
- [Local Development](local-development.md) -- running the bot locally
- [Deployment Guide](deployment-guide.md) -- end-to-end deployment walkthrough
