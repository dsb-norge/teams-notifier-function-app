# Teams Notification Bot - Access & Roles Reference

**Document Status:** Reference Guide
**Created:** 2026-02-09
**Audience:** Administrators, Security teams, Implementation teams

---

## Purpose

This document specifies the exact access permissions and roles required in Azure, Entra ID, and Microsoft 365 for deploying and operating the Teams Notification Bot. Use this as a reference for:

- Requesting permissions from administrators
- Creating service principals with minimal required access
- Security compliance documentation
- Troubleshooting permission-related issues

---

## Role Requirements Summary

| Operation Phase | Azure Subscription | Entra ID | Microsoft 365 Teams |
|-----------------|-------------------|----------|---------------------|
| Infrastructure Deployment | Contributor or Owner | - | - |
| Entra ID Setup | - | App Registration Administrator or Global Administrator | - |
| Teams App Deployment | - | - | Teams Administrator (or equivalent) |
| Teams App Installation | - | - | Team Owner |
| Runtime Operations | - | - | - (uses Managed Identity) |

---

## 1. Azure Subscription Roles

### 1.1 Infrastructure Deployment (Terraform)

**When:** Running `terraform plan` and `terraform apply` to create Azure resources

**Required Role:** One of the following:
- **Contributor** (recommended for least privilege)
- **Owner** (only if RBAC assignments are needed)

**Scope:** Subscription or Resource Group level

**Permissions Required:**

| Operation | Required Permission | Reason |
|-----------|-------------------|--------|
| Create Resource Group | `Microsoft.Resources/subscriptions/resourceGroups/write` | To create `<resource-group>` |
| Create Function App | `Microsoft.Web/sites/write` | Deploy Function App |
| Create App Service Plan | `Microsoft.Web/serverfarms/write` | Flex Consumption plan for Function App |
| Create Storage Account | `Microsoft.Storage/storageAccounts/write` | Queue and Table storage |
| Create Key Vault | `Microsoft.KeyVault/vaults/write` | Secrets storage |
| Create Application Insights | `Microsoft.Insights/components/write` | Monitoring |
| Create Log Analytics | `Microsoft.OperationalInsights/workspaces/write` | App Insights backend |
| Create Azure Bot Service | `Microsoft.BotService/botServices/write` | Teams bot connector |
| Create User-assigned Identity | `Microsoft.ManagedIdentity/userAssignedIdentities/write` | Create user-assigned Managed Identity |
| Assign Identity to Function App | `Microsoft.ManagedIdentity/userAssignedIdentities/assign/action` | Associate identity with Function App |
| Assign RBAC Roles (if using Owner) | `Microsoft.Authorization/roleAssignments/write` | Grant user-assigned identity access to Storage/Key Vault |

**RBAC Assignment Notes:**
- If using **Owner** role: Terraform can assign RBAC roles directly (recommended for automation)
- If using **Contributor** role: Someone with **User Access Administrator** or **Owner** must manually assign:
  - User-assigned Managed Identity → `Storage Blob Data Owner` on Storage Account
  - User-assigned Managed Identity → `Storage Queue Data Contributor` on Storage Account
  - User-assigned Managed Identity → `Storage Table Data Contributor` on Storage Account
  - User-assigned Managed Identity → `Key Vault Secrets User` on Key Vault
  - Deployment principal → `Key Vault Secrets Officer` on Key Vault (for storing secrets)

**How to Assign Contributor Role:**

```bash
# Assign Contributor role to a user (for manual deployment)
az role assignment create \
  --assignee <user-email@example.com> \
  --role "Contributor" \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>"

# Assign Contributor role to a service principal (for CI/CD)
az role assignment create \
  --assignee <service-principal-app-id> \
  --role "Contributor" \
  --scope "/subscriptions/<SUBSCRIPTION_ID>"
```

---

### 1.2 Managing Secrets in Key Vault

**When:** Storing secrets in Key Vault after Terraform deployment

**Required Role:** One of the following:
- **Key Vault Secrets Officer** (recommended for automation scripts)
- **Key Vault Administrator** (if broader access is acceptable)
- **Owner** (on Key Vault resource)

**Scope:** Key Vault resource level

**Permissions Required:**

| Operation | Required Permission | Reason |
|-----------|-------------------|--------|
| Set secrets | `Microsoft.KeyVault/vaults/secrets/setSecret/action` | Store `bot-client-secret` for local Dev Tunnels testing. |
| Read secret metadata | `Microsoft.KeyVault/vaults/secrets/readMetadata/action` | List secrets (optional, for verification) |

**Note:** The `api-key` secret was removed in v1.4 § 1 — all callers now authenticate via Entra ID Bearer tokens. Only `bot-client-secret` remains (for local Dev Tunnels testing where UAMI is unavailable).

**How to Assign Key Vault Secrets Officer:**

```bash
# Assign to user
az role assignment create \
  --assignee <user-email@example.com> \
  --role "Key Vault Secrets Officer" \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>/providers/Microsoft.KeyVault/vaults/<key-vault>"

# Assign to service principal
az role assignment create \
  --assignee <service-principal-app-id> \
  --role "Key Vault Secrets Officer" \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>/providers/Microsoft.KeyVault/vaults/<key-vault>"
```

---

### 1.3 Runtime Access (User-assigned Managed Identity)

**When:** Function App accesses Azure Storage and Key Vault during runtime

**Identity Type:** User-assigned Managed Identity (created separately, associated with Function App)

**Required Roles (assigned to user-assigned Managed Identity):**

| Target Resource | Role | Scope | Reason |
|-----------------|------|-------|--------|
| Storage Account | **Storage Blob Data Owner** | Storage Account | Manage deployment blobs and AzureWebJobsStorage host data |
| Storage Account | **Storage Queue Data Contributor** | Storage Account | Read/write messages to `notifications` queue |
| Storage Account | **Storage Table Data Contributor** | Storage Account | Read/write conversation references in `conversationreferences` table |
| Key Vault | **Key Vault Secrets User** | Key Vault | Read secrets (Key Vault references in app settings) |

**How to Assign (if not done by Terraform):**

```bash
# Get user-assigned Managed Identity principal ID
PRINCIPAL_ID=$(az identity show \
  --resource-group <resource-group> \
  --name <managed-identity> \
  --query principalId -o tsv)

STORAGE_SCOPE="/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>/providers/Microsoft.Storage/storageAccounts/<storage-account>"
KV_SCOPE="/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>/providers/Microsoft.KeyVault/vaults/<key-vault>"

# Assign Storage Blob Data Owner
az role assignment create --assignee $PRINCIPAL_ID --role "Storage Blob Data Owner" --scope "$STORAGE_SCOPE"

# Assign Storage Queue Data Contributor
az role assignment create --assignee $PRINCIPAL_ID --role "Storage Queue Data Contributor" --scope "$STORAGE_SCOPE"

# Assign Storage Table Data Contributor
az role assignment create --assignee $PRINCIPAL_ID --role "Storage Table Data Contributor" --scope "$STORAGE_SCOPE"

# Assign Key Vault Secrets User
az role assignment create --assignee $PRINCIPAL_ID --role "Key Vault Secrets User" --scope "$KV_SCOPE"
```

---

### 1.4 Monitoring & Diagnostics

**When:** Viewing Application Insights telemetry, logs, and metrics

**Required Role:** One of the following:
- **Monitoring Reader** (read-only access, recommended)
- **Application Insights Component Contributor** (if modifying config)

**Scope:** Application Insights resource or Resource Group level

**Permissions Required:**

| Operation | Required Permission | Reason |
|-----------|-------------------|--------|
| View telemetry data | `Microsoft.Insights/components/read` | Read logs, metrics, traces |
| Query logs | `Microsoft.OperationalInsights/workspaces/query/read` | Run KQL queries in Log Analytics |
| View live metrics | `Microsoft.Insights/components/api/read` | Real-time monitoring |

**How to Assign Monitoring Reader:**

```bash
az role assignment create \
  --assignee <user-email@example.com> \
  --role "Monitoring Reader" \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>"
```

---

## 2. Entra ID (Azure Active Directory) Roles

### 2.1 App Registration Creation

**When:** Creating Entra ID app registrations for the solution. Two registrations are needed:
1. **API app registration** (required) — Used by EasyAuth (`auth_settings_v2`) to validate Bearer tokens on API endpoints.
2. **Bot app registration** (optional) — Only needed for local Dev Tunnels testing. In production, the bot authenticates via User-Assigned Managed Identity (UAMI), not an app registration.

**Required Role:** One of the following:
- **Application Administrator** (recommended for least privilege)
- **Cloud Application Administrator**
- **Global Administrator** (only if other roles not available)

**Tenant Scope:** Yes (applies to entire Entra ID tenant)

**Permissions Required:**

| Operation | Required Permission | Reason |
|-----------|-------------------|--------|
| Create App Registration | `microsoft.directory/applications/create` | Register API app (and optionally bot app for local dev) |
| Create client secret | `microsoft.directory/applications/credentials/update` | Generate authentication secret (bot app only, for local dev) |
| Read directory data | `microsoft.directory/applications/read` | Verify app registration |

**Note:** The role **Application Developer** is NOT sufficient as it only allows creating apps the user owns, not service-level applications.

**How to Assign Application Administrator Role:**

```bash
# Via Azure CLI
az role assignment create \
  --assignee <user-email@example.com> \
  --role "Application Administrator" \
  --scope /

# Note: Entra ID roles are tenant-wide and cannot be scoped to specific resources
```

**Alternative: Azure Portal**
1. Navigate to **Azure Active Directory** > **Roles and administrators**
2. Search for **Application Administrator**
3. Click **Add assignments**
4. Select user and confirm

---

### 2.2 App Registration Management (Ongoing)

**When:** Rotating client secrets, updating app configuration

**Required Role:** Same as 2.1 (Application Administrator or higher)

**Operations:**

| Operation | Required Permission | Reason |
|-----------|-------------------|--------|
| Rotate client secret | `microsoft.directory/applications/credentials/update` | Replace expiring secrets |
| Update app settings | `microsoft.directory/applications/update` | Modify redirect URIs, etc. |
| Delete app registration | `microsoft.directory/applications/delete` | Cleanup (rarely needed) |

**Secret Rotation Reminder:**
- Client secrets expire after 90 days (as configured in setup script)
- Only the bot app registration secret needs rotation (for local Dev Tunnels testing). The API app registration does not use a client secret (EasyAuth validates tokens server-side).
- In production, the bot uses UAMI — no client secrets to rotate.

---

### 2.3 Bot Runtime Identity

**When:** Bot authenticates to Azure Bot Service at runtime

**Identity:** User-Assigned Managed Identity (`<managed-identity>`), configured as `UserAssignedMSI` on the Bot Service resource. The bot does not use an Entra ID app registration for runtime authentication.

**Required API Permissions:** **NONE**

**Explanation:**
- The bot uses **Resource-Specific Consent (RSC)** permissions defined in the Teams App manifest
- RSC permissions are granted when the Teams App is installed in a team
- No Graph API permissions or delegated permissions are required
- The UAMI authenticates via the Azure Instance Metadata Service (IMDS) — no client secrets needed

**Teams App Manifest RSC Permissions:**

```json
"authorization": {
  "permissions": {
    "resourceSpecific": [
      {
        "name": "ChannelMessage.Send.Group",
        "type": "Application"
      }
    ]
  }
}
```

**What this means:**
- Installing the Teams App grants the bot permission to send messages to channels in that specific team only
- No tenant-wide permissions required
- No admin consent flow needed (consent is per-team)

---

## 3. Microsoft 365 / Teams Roles

### 3.1 Teams App Package Upload

**When:** Uploading the Teams App package to the organization app catalog

**Required Role:** One of the following:
- **Teams Administrator** (recommended for least privilege)
- **Global Administrator**

**Tenant Scope:** Yes (organization-wide app catalog)

**Permissions Required:**

| Operation | Required Permission | Reason |
|-----------|-------------------|--------|
| Upload custom app | Teams admin center access | Publish app to org catalog |
| Manage org apps | Teams apps policy management | Enable/disable app for org |
| Review app permissions | App permission review | Verify RSC permissions |

**How to Upload:**
1. Navigate to **Teams Admin Center** (https://admin.teams.microsoft.com)
2. Go to **Teams apps** > **Manage apps**
3. Click **Upload new app** or **Submit a new app**
4. Select the Teams App package ZIP file
5. Review and approve

**Alternative for Development/Testing:**
- Users can upload custom apps directly in Teams client if **org-wide custom app policy** allows it
- This does NOT require admin role but requires policy to be enabled
- Only installs in teams where the user is an owner

---

### 3.2 Teams App Installation (Per-Team)

**When:** Installing the bot in a specific team to enable notifications to that team's channels

**Required Role:** **Team Owner** (for that specific team)

**Scope:** Per-team (not tenant-wide)

**Permissions Required:**

| Operation | Required Permission | Reason |
|-----------|-------------------|--------|
| Install app in team | Team owner permissions | Add bot to team's apps |
| Grant RSC consent | Automatic (team owner action) | Grant `ChannelMessage.Send.Group` permission |
| View installed apps | Team member (any member) | See bot in team's app list |

**How to Install:**
1. Open Microsoft Teams client
2. Navigate to the target team
3. Click **...** (More options) next to team name
4. Select **Manage team** > **Apps**
5. Click **More apps**
6. Search for your bot's app name
7. Click **Add** or **Add to team**
8. Confirm installation

**Note:** Team owners do NOT need tenant-level admin roles. They only need to be designated as an owner of the specific team.

**How to Make Someone a Team Owner:**
1. Team owner navigates to **Manage team** > **Members**
2. Find the user in the members list
3. Click **...** next to their name > **Make owner**

---

### 3.3 Obtaining Team/Channel IDs

**When:** Configuring `ChannelAliases` for notification targets

**Required Role/Permission:** One of the following:

**Option 1: Graph Explorer (Recommended for Admins)**
- **Role Required:** None (uses delegated permissions)
- **Permission Scope:** User must be a member of the team
- **Access:** https://developer.microsoft.com/graph/graph-explorer

**Option 2: Teams Web Client URL**
- **Role Required:** None
- **Permission Scope:** User must be a member of the team and channel
- **Method:** Copy channel URL from browser address bar

**Option 3: PowerShell (Microsoft Teams Module)**
- **Role Required:** None (uses delegated permissions)
- **Permission Scope:** User must be a member of the team
- **Module:** `MicrosoftTeams`

**Detailed Methods (see specification-v1.0.md Section 10.4):**

**Graph Explorer:**
```http
GET https://graph.microsoft.com/v1.0/me/joinedTeams
GET https://graph.microsoft.com/v1.0/teams/{team-id}/channels
```

**PowerShell:**
```powershell
Connect-MicrosoftTeams
Get-Team
Get-TeamChannel -GroupId "<team-id>"
```

**Teams Web URL Parsing:**
- Navigate to channel in web browser
- URL format: `https://teams.microsoft.com/l/channel/19%3A<channel-id>%40thread.tacv2/<channel-name>?groupId=<team-id>&tenantId=<tenant-id>`
- Decode URL-encoded characters (`%3A` → `:`, `%40` → `@`)

**Required Microsoft Graph Permissions (if using Graph API directly):**
- `Team.ReadBasic.All` (delegated) - Read team information
- `Channel.ReadBasic.All` (delegated) - Read channel information

These permissions are automatically granted when using Graph Explorer with user consent.

---

### 3.4 Managing Teams App Policies (Optional)

**When:** Controlling which users can install custom apps, use specific apps, etc.

**Required Role:** **Teams Administrator** or **Global Administrator**

**Permissions Required:**

| Operation | Required Permission | Reason |
|-----------|-------------------|--------|
| Create app setup policy | Teams admin center access | Define which apps are pinned/allowed |
| Assign policy to users | Teams admin center access | Control user access to apps |
| Enable custom app uploads | Global app settings | Allow org-wide custom app installs |

**Common Policies:**
- **Allow custom apps:** Enable/disable users uploading custom apps
- **Allow interaction with custom apps:** Enable/disable using custom bots
- **App permission policies:** Control which apps users can use

**Note:**
- Default policy usually allows team owners to install custom apps from org catalog
- Only modify if you encounter permission errors during installation

---

## 4. Special Scenarios

### 4.1 Deployment via Service Principal (CI/CD)

**When:** Automating Terraform deployments using GitHub Actions, Azure DevOps, or similar

**Required Components:**

1. **Azure Service Principal** with:
   - **Contributor** role on subscription or resource group
   - **User Access Administrator** (if Terraform assigns RBAC roles)
   - **Key Vault Secrets Officer** on Key Vault (for storing secrets)

2. **Entra ID Setup:**
   - Service Principal **cannot** create App Registrations (requires user account)
   - App Registration must be created manually or via separate automation with user credentials
   - Use Azure CLI with service principal for infrastructure, user account for Entra ID setup

**How to Create Service Principal for CI/CD:**

```bash
# Create service principal with Contributor role
az ad sp create-for-rbac \
  --name "sp-teams-bot-terraform" \
  --role "Contributor" \
  --scopes "/subscriptions/<SUBSCRIPTION_ID>" \
  --sdk-auth

# Output will include appId, password, tenant - store securely in CI/CD secrets
```

**GitHub Actions Example:**
```yaml
- name: Azure Login
  uses: azure/login@v1
  with:
    creds: ${{ secrets.AZURE_CREDENTIALS }}

- name: Terraform Apply
  run: |
    cd .
    terraform init
    terraform apply -auto-approve
```

---

### 4.2 Separated Responsibilities (Different Persons)

**Scenario:** Different teams/persons handle Azure, Entra ID, and Teams

| Responsibility | Required Role | Person/Team |
|----------------|---------------|-------------|
| Infrastructure Deployment | Azure Contributor | DevOps / Cloud Team |
| Entra ID App Registration | Application Administrator | Identity Team |
| Key Vault Secret Storage | Key Vault Secrets Officer | Security Team or DevOps |
| Teams App Upload | Teams Administrator | Microsoft 365 Admin Team |
| Teams App Installation | Team Owner | End User / Team Owner |
| Monitoring | Monitoring Reader | Operations Team |

**Coordination Required:**
- Identity Team creates API App Registration (for EasyAuth) → provides `API_APP_ID` to DevOps
- DevOps deploys infrastructure (Terraform creates UAMI and Bot Service with `UserAssignedMSI`) → provides Key Vault name to Security Team
- Security Team stores `bot-client-secret` in Key Vault (only needed for local Dev Tunnels testing)
- DevOps generates Teams App manifest using `scripts/generate-contract.sh` → builds ZIP package
- Microsoft 365 Admin uploads Teams App package
- Team Owners install app in their teams and provide Team/Channel IDs to DevOps
- DevOps updates Terraform with Team/Channel IDs

---

### 4.3 Least Privilege Deployment (Minimal Access)

**For Organizations with Strict Security Requirements:**

**Principle:** Grant only the minimum permissions required at each stage

**Phase 1: Entra ID Setup**
- **Who:** Identity Administrator
- **Role:** Application Administrator (Entra ID)
- **Duration:** One-time setup (API app registration for EasyAuth). Bot app registration optional (local dev only).
- **Scope:** Entra ID tenant (cannot be scoped further)

**Phase 2: Infrastructure Deployment**
- **Who:** Platform Team
- **Role:** Contributor (Azure)
- **Duration:** Initial deployment + updates
- **Scope:** Subscription or Resource Group `<resource-group>`

**Phase 3: RBAC Assignment**
- **Who:** Security Team or Platform Team
- **Role:** User Access Administrator (Azure) or Owner
- **Duration:** One-time after infrastructure deployment
- **Scope:** Storage Account, Key Vault resources

**Phase 4: Secret Storage**
- **Who:** Security Team or Identity Administrator
- **Role:** Key Vault Secrets Officer
- **Duration:** One-time + secret rotation events
- **Scope:** Key Vault `<key-vault>`

**Phase 5: Teams App Deployment**
- **Who:** Microsoft 365 Administrator
- **Role:** Teams Administrator
- **Duration:** One-time upload + app updates
- **Scope:** Tenant-wide Teams app catalog

**Phase 6: App Installation**
- **Who:** Team Owners (end users)
- **Role:** Team Owner (per-team)
- **Duration:** One-time per team
- **Scope:** Individual team

**Runtime:**
- **Who:** User-assigned Managed Identity (associated with Function App)
- **Role:** Storage Queue Data Contributor, Storage Table Data Contributor, Key Vault Secrets User
- **Duration:** Continuous
- **Scope:** Storage Account, Key Vault

---

## 5. Troubleshooting Permission Issues

### 5.1 "Insufficient privileges to complete the operation" (Entra ID)

**Symptom:** Error when running Entra ID setup script

**Cause:** User lacks Application Administrator role

**Resolution:**
- Verify role assignment: Azure Portal → Entra ID → Users → [User] → Assigned roles
- Ensure role is **Application Administrator** or higher
- Contact Global Administrator to assign role

---

### 5.2 "Authorization failed" (Azure Deployment)

**Symptom:** Terraform apply fails with authorization error

**Cause:** User or Service Principal lacks Contributor role

**Resolution:**
```bash
# Check current role assignments
az role assignment list --assignee <user-or-sp-id> --all

# Verify Contributor role exists on subscription or resource group
az role assignment list \
  --scope "/subscriptions/<SUBSCRIPTION_ID>" \
  --query "[?roleDefinitionName=='Contributor']"
```

---

### 5.3 "Access denied" (Key Vault)

**Symptom:** Cannot set secrets in Key Vault

**Cause:** Missing Key Vault Secrets Officer role

**Resolution:**
```bash
# Check Key Vault access policies and RBAC
az keyvault show \
  --name <key-vault> \
  --query properties.enableRbacAuthorization

# If RBAC is enabled (true), check role assignments
az role assignment list \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>/providers/Microsoft.KeyVault/vaults/<key-vault>" \
  --assignee <user-or-sp-id>

# Assign Key Vault Secrets Officer if missing
az role assignment create \
  --assignee <user-or-sp-id> \
  --role "Key Vault Secrets Officer" \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>/providers/Microsoft.KeyVault/vaults/<key-vault>"
```

---

### 5.4 "Forbidden" (Storage Access)

**Symptom:** Function App cannot access queues or tables

**Cause:** User-assigned Managed Identity missing Storage roles

**Resolution:**
```bash
# Get user-assigned Managed Identity principal ID
PRINCIPAL_ID=$(az identity show \
  --resource-group <resource-group> \
  --name <managed-identity> \
  --query principalId -o tsv)

# Check role assignments
az role assignment list \
  --assignee $PRINCIPAL_ID \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>/providers/Microsoft.Storage/storageAccounts/<storage-account>"

# Assign missing roles (see Section 1.3)
```

---

### 5.5 "App installation failed" (Teams)

**Symptom:** Cannot install Teams App in team

**Cause:** User is not a team owner or app policy blocks installation

**Resolution:**
- Verify user is listed as **Owner** in team settings
- Check Teams Admin Center → Teams apps → Permission policies
- Ensure custom app policy allows installation
- Verify app was uploaded to org app catalog

---

## 6. Quick Reference: Role Assignment Commands

### Azure Subscription Roles

```bash
# Contributor (for infrastructure deployment)
az role assignment create \
  --assignee <email-or-sp-id> \
  --role "Contributor" \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>"

# User Access Administrator (for RBAC assignments)
az role assignment create \
  --assignee <email-or-sp-id> \
  --role "User Access Administrator" \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>"

# Key Vault Secrets Officer (for secret management)
az role assignment create \
  --assignee <email-or-sp-id> \
  --role "Key Vault Secrets Officer" \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>/providers/Microsoft.KeyVault/vaults/<key-vault>"

# Monitoring Reader (for viewing logs/metrics)
az role assignment create \
  --assignee <email-or-sp-id> \
  --role "Monitoring Reader" \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>"
```

### User-assigned Managed Identity Roles

```bash
# Get user-assigned Managed Identity principal ID
PRINCIPAL_ID=$(az identity show \
  --resource-group <resource-group> \
  --name <managed-identity> \
  --query principalId -o tsv)

STORAGE_SCOPE="/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>/providers/Microsoft.Storage/storageAccounts/<storage-account>"
KV_SCOPE="/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>/providers/Microsoft.KeyVault/vaults/<key-vault>"

# Storage Blob Data Owner
az role assignment create --assignee $PRINCIPAL_ID --role "Storage Blob Data Owner" --scope "$STORAGE_SCOPE"

# Storage Queue Data Contributor
az role assignment create --assignee $PRINCIPAL_ID --role "Storage Queue Data Contributor" --scope "$STORAGE_SCOPE"

# Storage Table Data Contributor
az role assignment create --assignee $PRINCIPAL_ID --role "Storage Table Data Contributor" --scope "$STORAGE_SCOPE"

# Key Vault Secrets User
az role assignment create --assignee $PRINCIPAL_ID --role "Key Vault Secrets User" --scope "$KV_SCOPE"
```

### Entra ID Roles

```bash
# Application Administrator (via Azure Portal)
# Navigate to: Entra ID → Roles and administrators → Application Administrator → Add assignments

# Or via Microsoft Graph PowerShell
Install-Module Microsoft.Graph -Scope CurrentUser
Connect-MgGraph -Scopes "RoleManagement.ReadWrite.Directory"

$user = Get-MgUser -Filter "userPrincipalName eq '<user-email@example.com>'"
$role = Get-MgDirectoryRole -Filter "displayName eq 'Application Administrator'"
New-MgDirectoryRoleMemberByRef -DirectoryRoleId $role.Id -OdataId "https://graph.microsoft.com/v1.0/users/$($user.Id)"
```

---

## 7. Compliance & Auditing

### 7.1 Required Logging

All role assignments and permission changes should be logged for compliance:

**Azure Activity Log:**
- Automatically records all role assignments
- Retention: 90 days (default), configure longer retention in Log Analytics
- Access: Azure Portal → Monitor → Activity log

**Entra ID Audit Logs:**
- Records App Registration creation, secret generation, role assignments
- Retention: 30 days (free tier), 90 days (P1/P2)
- Access: Azure Portal → Entra ID → Audit logs

**Key Vault Audit Logs:**
- Enable diagnostic settings to log all secret access
- Stream to Log Analytics Workspace for analysis
- Track who accessed which secrets when

**How to Enable Key Vault Logging:**
```bash
az monitor diagnostic-settings create \
  --resource "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>/providers/Microsoft.KeyVault/vaults/<key-vault>" \
  --name "KeyVaultAuditLogs" \
  --logs '[{"category": "AuditEvent", "enabled": true}]' \
  --workspace "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<resource-group>/providers/Microsoft.OperationalInsights/workspaces/<log-analytics>"
```

---

### 7.2 Periodic Access Review

**Recommended Schedule:**

| Review Type | Frequency | Actions |
|-------------|-----------|---------|
| Azure role assignments | Quarterly | Remove inactive users, review service principals |
| Entra ID App Registrations | Quarterly | Verify app still in use, rotate secrets |
| Key Vault access | Monthly | Review who accessed secrets |
| Teams App installations | Quarterly | Verify bot still needed in each team |

**Query Examples:**

```bash
# List all role assignments in resource group
az role assignment list \
  --resource-group <resource-group> \
  --output table

# List App Registrations (filter by name)
az ad app list --display-name "<your-bot-name>" --output table

# Check Key Vault secret expiration
az keyvault secret list --vault-name <key-vault> \
  --query "[].{name:name, expires:attributes.expires}" --output table
```

---

## 8. Summary Cheat Sheet

| **I need to...** | **Required Role** | **Scope** |
|------------------|-------------------|-----------|
| Deploy Azure infrastructure | Contributor | Azure Subscription/RG |
| Assign RBAC roles | User Access Administrator or Owner | Azure Subscription/RG |
| Create Entra ID App Registration (EasyAuth API) | Application Administrator | Entra ID Tenant |
| Store secrets in Key Vault | Key Vault Secrets Officer | Key Vault |
| Upload Teams App to org catalog | Teams Administrator | Microsoft 365 Tenant |
| Install Teams App in a team | Team Owner | Specific Team |
| View logs and metrics | Monitoring Reader | Azure RG or App Insights |
| Obtain Team/Channel IDs | Team Member | Specific Team |

---

**End of Access & Roles Reference**
