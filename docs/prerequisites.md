# Prerequisites

Everything you need before deploying the Teams Notification Bot infrastructure,
application code, and Teams app manifest.

## 1. Required Tools

Install the following tools on your deployment workstation:

| Tool | Version | Purpose |
|------|---------|---------|
| Azure CLI (`az`) | Latest | Azure resource management, RBAC, Entra ID |
| Terraform | >= 1.5 | Infrastructure-as-code deployment |
| .NET 10 SDK | 10.x | Build the Azure Function App |
| Azure Functions Core Tools | v4 | Publish the Function App to Azure |
| jq | Latest | JSON processing in helper scripts |

> **Tip:** Azure Functions Core Tools v4 is required for Flex Consumption plan
> support and .NET 10 isolated worker model.

## 2. Azure Access

The person or service principal running Terraform and deploying code needs the
following permissions on the target Azure subscription or resource group:

| Role | Scope | Reason |
|------|-------|--------|
| **Contributor** | Resource group | Create and manage all Azure resources |
| **User Access Administrator** | Resource group | Terraform assigns RBAC roles (managed identity, monitor, etc.) |

If deploying into a pre-existing resource group, ensure the resource group
already exists and that you have the roles listed above scoped to it.

Log in and select the correct subscription:

```bash
az login
az account set --subscription "<subscription-id>"
az account show
```

## 3. Entra ID Setup

Two app registrations are required. These are created **manually** in Entra ID
before running Terraform -- the Terraform module does not manage Entra ID
resources.

> **Required Entra ID role:** Application Administrator or Global Administrator
> to create app registrations and service principals.

### 3.1 Bot App Registration

Used by Bot Framework for inbound message authentication (Teams to bot) and
outbound token acquisition (bot to Teams).

| Setting | Value |
|---------|-------|
| Name | e.g. `bot-my-notification-bot` |
| Supported account types | **Accounts in any organizational directory** (`AzureADMultipleOrgs`) |
| Redirect URI | (none) |

**Why `AzureADMultipleOrgs`?** Even though the bot is SingleTenant, the Bot
Framework Connector service lives in Microsoft's `botframework.com` tenant. It
must be able to authenticate against your app registration. With
`AzureADMyOrg`, the Connector cannot obtain tokens and messages are silently
dropped.

After creating the app registration, create its service principal:

```bash
az ad sp create --id <bot-app-id>
```

Without the service principal, MSAL token acquisition fails with
"missing service principal in the tenant".

Generate a client secret (only needed for local Dev Tunnels testing):

```bash
az ad app credential reset --id <bot-app-id> --display-name "local-dev"
```

### 3.2 API App Registration

Used by EasyAuth to protect the notification API endpoints. Callers (Azure
Monitor, CI/CD pipelines, scripts) authenticate with this app.

| Setting | Value |
|---------|-------|
| Name | e.g. `api-my-notification-bot` |
| Supported account types | **Accounts in this organizational directory only** (`AzureADMyOrg`) |
| Application ID URI | `api://<api-app-id>` |

Define an application-type app role:

| Field | Value |
|-------|-------|
| Display name | `Notifications.Send` |
| Allowed member types | Applications |
| Value | `Notifications.Send` |
| Description | Allows sending notifications via the bot API |

Create the service principal and lock it down:

```bash
# Create service principal
az ad sp create --id <api-app-id>

# Require explicit role assignment for token issuance
az ad sp update --id <api-app-id> \
  --set appRoleAssignmentRequired=true
```

With `appRoleAssignmentRequired=true`, only service principals that have been
explicitly assigned the `Notifications.Send` role can obtain tokens. All other
callers are rejected at the Entra ID token endpoint.

Assign the role to authorized callers:

```bash
# Example: grant Azure Monitor's "Azns AAD Webhook" service principal
az ad app permission grant ...

# Example: grant a CI/CD service principal
az rest --method POST \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/<api-sp-object-id>/appRoleAssignments" \
  --body '{
    "principalId": "<caller-sp-object-id>",
    "resourceId": "<api-sp-object-id>",
    "appRoleId": "<notifications-send-role-id>"
  }'
```

See [access-and-roles.md](access-and-roles.md) for a complete reference on
identity architecture, role assignments, and least-privilege guidance.

### 3.3 Federated Identity Credential (FIC)

The FIC links the bot's User-Assigned Managed Identity (UAMI) to the Bot App
Registration. This enables passwordless Bot Framework authentication -- no
client secret rotation needed in production.

The FIC is created **after** Terraform deploys the UAMI. See
[Step 2 in the deployment guide](deployment-guide.md#step-2-create-federated-identity-credential)
for the exact commands.

## 4. Microsoft 365 Access

| Role | Reason |
|------|--------|
| **Teams Administrator** | Upload the Teams app package to the org-wide app catalog |
| **Team Owner** | Install the app in specific teams and channels |

The Teams Administrator uploads the app via the
[Teams Admin Center](https://admin.teams.microsoft.com/policies/manage-apps).
Individual Team Owners then install it in their teams.

> **Note:** Check the tenant's app permission policies. Custom apps may be
> blocked by default under **Manage Apps** in the Teams Admin Center.

## 5. Verification Checklist

Run these commands to confirm all prerequisites are in place:

| Prerequisite | Verification Command |
|-------------|----------------------|
| Azure CLI installed | `az version` |
| Terraform installed | `terraform version` |
| .NET 10 SDK | `dotnet --version` (expect `10.x.x`) |
| Functions Core Tools v4 | `func --version` (expect `4.x.x`) |
| jq installed | `jq --version` |
| Azure subscription access | `az account show --query '{name:name, id:id}'` |
| Correct subscription selected | `az account show --query name -o tsv` |
| Bot app registration exists | `az ad app show --id <bot-app-id> --query displayName` |
| Bot service principal exists | `az ad sp show --id <bot-app-id> --query displayName` |
| API app registration exists | `az ad app show --id <api-app-id> --query displayName` |
| API service principal exists | `az ad sp show --id <api-app-id> --query displayName` |
| Resource group exists | `az group show --name <resource-group> --query name` |

## 6. Next Steps

Once all prerequisites are satisfied, proceed to the
[Deployment Guide](deployment-guide.md) for step-by-step instructions.
