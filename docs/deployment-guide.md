# Deployment Guide

End-to-end instructions for deploying the Teams Notification Bot. This guide
covers infrastructure provisioning, application deployment, Teams app
installation, and end-to-end verification.

> **Before you begin:** Complete all items in the [Prerequisites](prerequisites.md)
> checklist.

## Overview

The deployment consists of three components:

1. **Infrastructure** -- Terraform module provisions all Azure resources (Function
   App, Storage, Bot Service, VNet, monitoring).
2. **Application code** -- .NET 10 Function App published via Azure Functions Core
   Tools.
3. **Teams app** -- Manifest package generated from app requirements and branding
   metadata, then uploaded to the Teams Admin Center.

Each step depends on the previous one. Follow the steps in order.

From GitHub Actions, steps 3 and 4 (and publishing the package for Step 5) are
one call to this repository's reusable workflow; see
[Deploy with the reusable workflow](#deploy-with-the-reusable-workflow).

---

## Step 1: Deploy Infrastructure

Create a Terraform root module that calls the Teams Notification Bot module:

```hcl
module "teams_notification_bot" {
  source = "path/to/terraform-module"

  name                = "my-notification-bot"
  resource_group_name = "<resource-group>"
  bot_app_id          = "<bot-app-id>"
  api_app_id          = "<api-app-id>"
  api_app_object_id   = "<api-app-object-id>"

  alert_target_alias = "ops-alerts"  # optional, empty string disables

  management_ip_rules = [
    {
      name        = "office"
      description = "Office network"
      cidr        = "203.0.113.0/24"
    }
  ]
}
```

See the [Module Reference](#module-reference) section below for all inputs and
outputs.

Initialize and apply:

```bash
terraform init && terraform plan -out=tfplan && terraform apply tfplan
```

The module creates approximately 30 resources (VNet, private endpoints, Flex
Consumption Function App, Storage, Bot Service, Log Analytics, Application
Insights). After a successful apply, note the key outputs:

The infrastructure team should provide these values from the Terraform outputs:

| Output | Used in |
|--------|---------|
| `function_app_name` | Code deployment (Step 3) |
| `function_app_hostname` | API base URL (Step 8) |
| `uami_principal_id` | FIC setup (Step 2) |

---

## Step 2: Create Federated Identity Credential

After Terraform creates the UAMI, establish the FIC trust between the UAMI and
the Bot App Registration. This enables passwordless Bot Framework
authentication.

```bash
UAMI_PRINCIPAL_ID="<uami-principal-id>"  # From Terraform output
TENANT_ID="$(az account show --query tenantId -o tsv)"

az ad app federated-credential create \
  --id "<bot-app-id>" \
  --parameters '{
    "name": "uami-fic",
    "issuer": "https://login.microsoftonline.com/'"$TENANT_ID"'/v2.0",
    "subject": "'"$UAMI_PRINCIPAL_ID"'",
    "audiences": ["api://AzureADTokenExchange"],
    "description": "UAMI federated credential for Bot Framework auth"
  }'
```

Verify the credential was created:

```bash
az ad app federated-credential list --id "<bot-app-id>" --query '[].name'
```

> **Why FIC?** The FIC allows the Function App's managed identity to
> authenticate as the Bot App Registration without a client secret. This
> eliminates secret rotation overhead in production. A client secret is only
> needed for local Dev Tunnels testing.
>
> **Note:** This credential uses the Entra ID issuer with the UAMI principal
> ID as its subject. It is unrelated to GitHub Actions OIDC and is **not**
> affected by GitHub's subject-claim format change -- that applies to the
> deploy UAMI FICs only, see
> [GitHub OIDC subject-claim formats](#github-oidc-subject-claim-formats).

---

## Step 3: Deploy Function App

There are two ways to deploy the function app by hand: from a published release
(recommended) or from source. Choose one.

> **Deploying from GitHub Actions?** Call this repository's reusable workflow
> instead. It deploys a release, gates it on your checked-in
> `app-requirements.json`, and publishes your Teams app package (Step 4) as well.
> See [Deploy with the reusable workflow](#deploy-with-the-reusable-workflow).

### Option A: Deploy from release artifacts (recommended)

Each GitHub Release includes a pre-built ZIP ready for deployment. These are the
steps the reusable workflow automates.

**1. Download the release artifacts:**

```bash
# Download all artifacts from a specific release
gh release download teams-notifier-function-app-v1.2.0 \
  --repo <org>/teams-notifier-function-app \
  -D ./release

# Or download the latest release
gh release download --repo <org>/teams-notifier-function-app -D ./release
```

This downloads three files:

| File | Purpose |
|------|---------|
| `teams-notifier-function-app-v{VERSION}.zip` | Pre-built function app (R2R compiled for linux-x64) |
| `app-requirements.json` | Infrastructure requirements — feed this to the Terraform module as `var.app_requirements` |
| `teams-app-package-v{VERSION}.tar.gz` | Teams manifest template and icons for Step 4 |

> **Tip:** If you haven't already, copy `app-requirements.json` to your
> Terraform configuration directory and reference it in your module call:
> `app_requirements = jsondecode(file("app-requirements.json"))`. Then run
> `terraform apply` to ensure infrastructure matches the app's requirements.

**2. Extract and deploy the ZIP:**

```bash
# Extract the function app ZIP
mkdir -p publish
unzip release/teams-notifier-function-app-v*.zip -d publish/

# Create a stub .csproj — the func CLI needs this to detect the .NET version
cat > publish/stub.csproj << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF

# Deploy to Azure (no build needed — the ZIP is pre-compiled)
cd publish
func azure functionapp publish "<function-app-name>" --no-build --dotnet-isolated
```

> **Why the stub `.csproj`?** The Azure Functions Core Tools CLI inspects
> `.csproj` files to determine the target framework. Since the release ZIP
> contains only compiled output (no project files), the CLI refuses to publish
> without it. The stub only needs the `<TargetFramework>` element.

### Option B: Deploy from source

If you need to build from source (e.g., for development or custom modifications):

```bash
cd src/TeamsNotificationBot
dotnet publish -c Release -o ./publish
func azure functionapp publish "<function-app-name>" --dotnet-isolated
```

### Verify the deployment

After deploying with either option, check the health endpoint (no auth required):

```bash
curl -s "https://<function-app-hostname>/api/health" | jq .
# Expected: { "status": "ok", "version": "1.2.0", "timestamp": "..." }
```

The `version` field should match the release you deployed. If this times out,
ensure your IP is in `management_ip_rules`.

> **Note:** The `func` CLI output may not list all functions (e.g., functions
> with custom routes). Use `az functionapp function list` to verify that all
> expected functions are loaded.

---

## Step 4: Package Teams App

The `teams-app-package/` directory contains the packaging script, branding
metadata, and icons. The script reads `app-requirements.json` (generated by
`scripts/generate-requirements.sh`) and `app-metadata.json` (branding) to
produce the Teams manifest and ZIP package.

For a single-env install (this guide's default path) the script can be run
with just `--bot-app-id` -- the Teams catalog id (`manifest.id`) falls back
to that value with a warning. For multi-env setups (e.g. dev + prod sharing
this guide), supply a distinct `--teams-app-id` per env so the two installs
don't collide in the Teams catalog.

```bash
cd teams-app-package

# Single-env (manifest.id falls back to the bot app id with a warning)
./create-teams-app-package.sh --bot-app-id <bot-app-id>

# Multi-env (generate one Teams catalog GUID per env and pin it)
./create-teams-app-package.sh --bot-app-id <bot-app-id> --teams-app-id <teams-app-id>
```

This produces a `.zip` file containing:

- `manifest.json` -- generated from app requirements, branding metadata, the bot app ID, and the Teams app ID
- `color.png` -- 192x192 color icon
- `outline.png` -- 32x32 outline icon

---

## Step 5: Install in Teams

### 5.1 Upload to Org Catalog

1. Open the [Teams Admin Center](https://admin.teams.microsoft.com/policies/manage-apps).
2. Navigate to **Teams apps** > **Manage apps**.
3. Click **Upload new app** and select the ZIP from Step 4.
4. Verify the app appears in the list with status **Allowed**.

> **If the app shows as Blocked:** Check the org-wide app permission policy
> under **Permission policies**. Custom apps may be blocked by default.

### 5.2 Install in a Team

1. Open Microsoft Teams.
2. Navigate to the target team and channel.
3. Click **+** (Add a tab) or go to **Apps** > search for the bot name.
4. Click **Add to a team** and select the target channel.

When the bot is installed, it sends a greeting message to the channel and
automatically stores the conversation reference for future proactive
notifications.

---

## Step 6: Create Aliases

Aliases map friendly names to Teams channels. They are used in API URLs for
routing notifications.

### Via Chat Command

In the channel where the bot is installed, send:

```
set-alias ops-alerts Operational alerts channel
```

This creates the alias `ops-alerts` pointing to the current channel.

### Via Interactive Form

Send the command:

```
create-alias
```

The bot responds with an Adaptive Card form where you can fill in the alias
name and description interactively.

### Verify Aliases

```
list-aliases
```

The bot responds with a card listing all registered aliases and their target
channels.

---

## Step 7: Configure Alert Webhook (Optional)

> **Prerequisite:** The identity running Terraform must be an owner of the API app
> registration (section 3.2 in [prerequisites](prerequisites.md)). If not, Action Group
> creation fails with `AadWebhookResourceNotOwnedByCaller`.

To route Azure Monitor alerts to a Teams channel, set `alert_target_alias` in
the module call (e.g., `alert_target_alias = "ops-alerts"`) and re-apply
Terraform. This creates an Action Group with an AAD-authenticated webhook
pointing to `/api/v1/alert/ops-alerts`. Reference this Action Group in your
alert rules.

---

## Step 7b: updown.io Webhook Ingress (Optional)

Lets [updown.io](https://updown.io) push uptime/SSL alerts into a Teams channel via an **anonymous,
token-authenticated** endpoint (`POST /api/v1/ingest/updown/{token}`). See
[api-reference.md](api-reference.md) for the endpoint and [authentication.md](authentication.md#5-webhook-token-authentication-updownio-ingress)
for the trust model.

**Infrastructure prerequisites:**

1. **Module `v1.1.0` or later.** Older versions lack `bot_auth_settings.easy_auth_excluded_paths`
   (which the app's `app-requirements.json` now declares). Pin the module accordingly:
   ```hcl
   module "teams_notification_bot" {
     source  = "dsb-norge/teams-notification-bot-lz/azurerm"
     version = "1.1.0"   # or later
     # ...
   }
   ```
2. **Open site inbound** so updown's (external, non-Azure) IPs can reach the endpoint. The module
   default-denies inbound; add a public rule:
   ```hcl
   allowed_caller_rules = [
     # ... existing rules ...
     { name = "public-ingest", description = "Public updown.io webhook ingress", cidr = "0.0.0.0/0" },
   ]
   ```
   The AAD `/api/v1/*` routes stay protected regardless (EasyAuth + `Notifications.Send`); the
   ingress is guarded by its per-webhook token plus the in-app source-IP allowlist.

**Per-channel setup (in Teams):**

1. In the target channel: **`create-webhook`** → the bot returns a secret URL (shown once).
2. Paste that URL into updown.io as a webhook recipient.
3. Use updown's *recipients → test* page to send a sample payload; confirm a card lands in Teams.
4. **IP filter is `enforce` by default** (secure by default). The empty-list fail-safe means a fresh
   deploy never blocks until the allowlist populates (startup warm-up + `ips.updown.io`), then
   non-updown IPs get `403`. Run **`show-ip-allow-list updown`** to confirm updown's IPs are captured.
   To observe first without blocking, temporarily set `UpdownWebhook__IpFilterMode=log-only` (or `off`)
   via `az` — a per-deployment override that reverts to `enforce` on the next infra apply. See
   [authentication.md §7](authentication.md#7-configuration-reference). These are operator-set app
   settings via `az`, not module inputs.

---

## Step 8: End-to-End Verification

Run through each endpoint to confirm the full deployment is working. First,
set up variables used by all subsequent commands. The token must belong to a
service principal or managed identity holding `Notifications.Send`, so sign
`az` in as that identity first (`az login --service-principal …` or
`az login --identity`). A user sign-in can't get a token with the role; see
[API Reference §2](api-reference.md#2-authentication).

```bash
HOST="https://<function-app-hostname>"
TOKEN=$(az account get-access-token \
  --resource "api://<api-app-id>" \
  --query accessToken -o tsv)
```

**Health check** and **OpenAPI spec** (no auth required):

```bash
curl -s "$HOST/api/health" | jq .
curl -s "$HOST/api/v1/openapi.yaml" | head -20
```

**Check-in** and **send notification** (with auth):

```bash
curl -s -X POST "$HOST/api/v1/checkin/ops-alerts" \
  -H "Authorization: Bearer $TOKEN" | jq .

curl -s -X POST "$HOST/api/v1/notify/ops-alerts" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"message": "Deployment complete. All systems operational.", "format": "text"}' | jq .
```

**Idempotency** -- send the same request twice with the same key:

```bash
IDEM_KEY=$(uuidgen)
for i in 1 2; do
  curl -s -X POST "$HOST/api/v1/notify/ops-alerts" \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    -H "Idempotency-Key: $IDEM_KEY" \
    -d '{"message": "Idempotency test", "format": "text"}' | jq .
done
```

The second request should return the cached response from the first: the same `messageId`.

**Error cases:**

```bash
# Auth rejection (expect 401)
curl -s -o /dev/null -w "%{http_code}\n" -X POST "$HOST/api/v1/notify/ops-alerts" \
  -H "Content-Type: application/json" -d '{"message": "No token", "format": "text"}'

# Unknown alias (expect 404)
curl -s -o /dev/null -w "%{http_code}\n" -X POST "$HOST/api/v1/notify/nonexistent" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"message": "Bad alias", "format": "text"}'
```

---

## Deploy with the reusable workflow

[`.github/workflows/reusable-deploy.yml`](../.github/workflows/reusable-deploy.yml)
deploys one app release to one Function App, from your repository's GitHub
Actions. It is `workflow_call` only: your workflow decides when to deploy and
which version. It runs four jobs:

1. **preflight** (GitHub-hosted runner, no Azure access) resolves the release,
   waits for its assets, verifies their build provenance, and checks your
   checked-in `app-requirements.json` against the release (see
   [Preflight checks](#preflight-checks)). When asked to, it reads
   `/api/health` and skips the deploy if there is nothing to deploy. A failure
   here stops the run before anything is published.
2. **deploy** (your runner, your GitHub environment) downloads the release ZIP,
   accepts it only if it is the file preflight verified, installs a pinned
   Azure Functions Core Tools, logs in to Azure with OIDC and runs
   `func azure functionapp publish --no-build --dotnet-isolated`.
3. **package-manifest** (GitHub-hosted, read-only token) builds your Teams app
   package (Step 4) with the release's `create-teams-app-package.sh` and your
   branding, and validates it against the Teams manifest schema.
4. **publish-package** (GitHub-hosted, the only job that can write to your
   repository) takes just the package ZIP and publishes it as a release **in your
   repository**, named `teams-app-<instance_name>-v<version>-<shorthash>` after
   the ZIP's own content. It runs no code from the release: the generator in
   job 3 is release code, and a pre-release may be built from an unreviewed
   branch. A new release appears only when the package content changes, and
   only then does a Teams Administrator need to upload it (Step 5).

The package is built and published after a successful deploy, and also when
`skip_if_current` found the target version already running, so a branding-only
change in your repository, or a package build that failed after a good deploy,
is picked up by your next run. Not when `/api/health` couldn't be read, since a
package for a version nobody confirmed is running would be wrong, nor after a
failed or cancelled deploy.

The workflow is released with the app. It reads its own version from this
repository's `.release-please-manifest.json` at the commit you call it at, so at
the tag `teams-notifier-function-app-vX.Y.Z` it deploys release `X.Y.Z` unless
you pass another `tag`: one `uses:` ref names both the deploy logic and the app
version. Pinning a commit SHA works the same way. Workflow changes ship as
ordinary app releases; you get them when you move your ref. Tags in this
repository can't be moved or deleted.

### Pinned caller

The version you run is the one your `uses:` ref names, so every upgrade or
rollback is a reviewed change in your repository. This caller deploys after each
successful Terraform run on `main`, but only when `/api/health` reports a
different version, and redeploys the pinned release on demand:

```yaml
name: Deploy Teams Notifier

on:
  workflow_run:
    workflows: ["Terraform CI/CD"] # the workflow that applies the module
    types: [completed]
    branches: [main]
  workflow_dispatch: # redeploys the pinned release, whatever is running

permissions:
  id-token: write # azure/login OIDC in the called workflow
  contents: write # gh release create in publish-package

jobs:
  deploy:
    # Infrastructure first: never deploy code whose infrastructure failed to apply.
    if: >-
      github.event_name == 'workflow_dispatch' ||
      github.event.workflow_run.conclusion == 'success'
    uses: dsb-norge/teams-notifier-function-app/.github/workflows/reusable-deploy.yml@teams-notifier-function-app-vX.Y.Z
    with:
      skip_if_current: ${{ github.event_name == 'workflow_run' }}
      app_requirements_file: infra/teams-notifier/app-requirements.json
      teams_app_package_dir: infra/teams-notifier/teams-app-package
      instance_name: prod
      function_app_name: func-example-teams-notifier
      azure_environment: teams-notifier-deploy
      azure_tenant_id: 00000000-0000-0000-0000-000000000000
      azure_subscription_id: 00000000-0000-0000-0000-000000000000
      azure_client_id: 00000000-0000-0000-0000-000000000000 # module output deploy_uami_client_id
      runner: my-vnet-runner
      concurrency_group: deploy-teams-notifier
      bot_app_id: 00000000-0000-0000-0000-000000000000
      teams_app_id: 00000000-0000-0000-0000-000000000000
```

`app_requirements_file` must be the `app-requirements.json` of the release the
ref names, the same file your Terraform module call reads. The two move together;
see [Upgrade a pinned caller](#upgrade-a-pinned-caller).

Passing the Azure ids as inputs is the recommended path: they are identifiers,
not secrets, and this path depends on no secret at all. The called workflow gets
its own `GITHUB_TOKEN`. The deploy job's `environment:` resolves in your
repository, so the OIDC subject is
`repo:<your-org>/<your-repo>:environment:<azure_environment>`, and GitHub
creates that environment on first use. That is the subject your module's
`deploy_github_actions_from` federated credential must match (see
[GitHub OIDC subject-claim formats](#github-oidc-subject-claim-formats)).

### Autopilot caller

A caller that picks the version itself passes it as `tag`. This one keeps a test
instance on the newest release. After each successful Terraform run on `main` it
deploys the latest release, but only when `/api/health` reports a different
version. A dispatch deploys the tag you give it, or the latest release, whatever
is running:

```yaml
name: Deploy the test instance

on:
  workflow_run:
    workflows: ["Terraform CI/CD"] # the workflow that applies the module
    types: [completed]
    branches: [main]
  workflow_dispatch:
    inputs:
      tag:
        description: Release or pre-release tag. Empty deploys the latest release.
        type: string
        required: false
        default: ""

permissions:
  id-token: write # azure/login OIDC in the called workflow
  contents: write # gh release create in publish-package

jobs:
  target:
    # Infrastructure first: never deploy code whose infrastructure failed to apply.
    if: >-
      github.event_name == 'workflow_dispatch' ||
      github.event.workflow_run.conclusion == 'success'
    runs-on: ubuntu-latest
    permissions:
      contents: read
    outputs:
      tag: ${{ steps.target.outputs.tag }}
    steps:
      - id: target
        env:
          GH_TOKEN: ${{ github.token }}
          INPUT_TAG: ${{ inputs.tag }}
        run: |
          set -euo pipefail
          tag="${INPUT_TAG:-}"
          if [ -z "$tag" ]; then
            tag="$(gh release view --repo dsb-norge/teams-notifier-function-app --json tagName --jq .tagName)"
          fi
          echo "tag=$tag" >>"$GITHUB_OUTPUT"

  deploy:
    needs: target
    uses: dsb-norge/teams-notifier-function-app/.github/workflows/reusable-deploy.yml@teams-notifier-function-app-vX.Y.Z
    with:
      tag: ${{ needs.target.outputs.tag }}
      skip_if_current: ${{ github.event_name == 'workflow_run' }}
      # ...the same inputs as the pinned caller
```

With an explicit `tag`, the [version check](#preflight-checks) is skipped and
only the hash gate applies: your `app-requirements.json` needs the same
infrastructure hash as the release, not the same version. Replace it, verbatim,
only when a release changes the hash; until then that release fails preflight
with an infrastructure hash mismatch. `gh release view` without a tag returns
the release marked Latest, which is never a pre-release.

Keep the `uses:` ref on a release tag and move it now and then to pick up
workflow fixes; it does not have to match the `tag` you deploy. A newer release
can't always be deployed by an older workflow, though: a release that changes
what the workflow reads from it (asset names, file paths, the Teams package
generator's options) is marked breaking, and its release notes say which ref to
move to. Until you do, runs that target it fail.

To deploy a **pre-release**, dispatch with its tag, e.g.
`teams-notifier-function-app-v2.1.1-pre.4`. The next chained run puts the latest
release back, because `/api/health` then reports a different version. A
pre-release doesn't bump `.release-please-manifest.json`, so calling the
workflow at a pre-release tag (or a branch) without `tag` deploys the last full
release.

### Inputs

| Input | Required | Default | Meaning |
|-------|----------|---------|---------|
| `tag` | no | `""` | The app release tag to deploy. Empty deploys the workflow's own release and turns on the version check. |
| `skip_if_current` | no | `false` | When `true`, preflight reads `https://<function_app_name>.azurewebsites.net/api/health` (three tries). If it reports the target version, nothing is deployed, but the Teams package is still built and published if its content changed. If it can't be read, nothing is deployed or packaged, with a warning: the workflow never deploys blind. A first deploy, with no code running yet, therefore needs `false`. |
| `app_requirements_file` | yes | | Path in your repository to the checked-in `app-requirements.json`. |
| `teams_app_package_dir` | yes | | Path in your repository to the directory with `app-metadata.json`, `color.png` and `outline.png` (Step 4). |
| `instance_name` | yes | | Names the Teams package release, `teams-app-<instance_name>-v<version>-<shorthash>`. Lower-case letters and digits, optionally separated by single hyphens. |
| `function_app_name` | yes | | The Function App to publish to. |
| `azure_environment` | yes | | GitHub environment of the deploy job. It scopes the OIDC subject. GitHub creates it on first use. |
| `azure_tenant_id`, `azure_subscription_id`, `azure_client_id` | no | `""` | The ids `azure/login` uses. Passing them is the recommended path. Each one, when empty, falls back to the secret of the same upper-case name (`AZURE_TENANT_ID` and so on) in the deploy job's environment, `azure_environment`: per GitHub's documentation, a called job that sets `environment:` uses that environment's secrets. If an id is still missing, the deploy job's first step fails with a message naming it. |
| `runner` | yes | | Label of the runner for the deploy job. It needs network access to the Function App's SCM endpoint. |
| `concurrency_group` | yes | | Serialises deploys to one instance. |
| `bot_app_id` | yes | | Client id of the bot's Entra app registration (`manifest.bots[0].botId`). |
| `teams_app_id` | yes | | Teams catalog id of the instance's app package (`manifest.id`). Fixed for the life of the instance: Teams treats a different id as a different app and every install has to be redone. |
| `upgrade_doc_url` | no | [Upgrade a pinned caller](#upgrade-a-pinned-caller) | Linked from the job summary when a preflight check fails. Point it at your own runbook if you have one. |

### Preflight checks

- **Version check**, only when `tag` is empty: `notifier_application_version`
  in `app_requirements_file` must equal the workflow's own release. It fails when
  the `uses:` ref was moved without replacing the file, or the file was replaced
  without moving the ref.
- **Infrastructure hash gate**, always: the file's
  `infrastructure_requirements_unique_hash` must equal the release's. A different
  hash means the release needs infrastructure your Terraform hasn't applied
  (changed EasyAuth excluded paths, app settings, queues and so on). The
  infrastructure hash leaves the version out, so a pre-release passes the gate
  of the release it came from.
- **Provenance**: the release's `app-requirements.json` and ZIP must carry a
  valid build provenance attestation from this repository, for the commit the
  tag points at, built on a GitHub-hosted runner. An older release's file
  uploaded under a newer tag is refused. The later jobs accept only these exact
  files, by SHA-256. Releases before 1.3.1 were not attested and can't be
  deployed with this workflow.

A failed check explains the fix in the job summary and links `upgrade_doc_url`.

### Upgrade a pinned caller

An upgrade moves the app and its deploy workflow together, in one pull request
with two changes for the same release tag:

1. Move the `uses:` ref to `@teams-notifier-function-app-vX.Y.Z`.
2. Download that release's `app-requirements.json` and replace your
   `app_requirements_file` with it **verbatim**:

   ```bash
   gh release download teams-notifier-function-app-vX.Y.Z \
     --repo dsb-norge/teams-notifier-function-app \
     --pattern app-requirements.json --output <app_requirements_file> --clobber
   ```

   Terraform reads the whole file, and preflight checks both its version and its
   hash, so changing only the version field leaves the infrastructure out of step
   with the app.

Then:

3. Let your Terraform plan run on the pull request. If the hash changed, the plan
   shows the infrastructure the release needs. If it didn't, the plan has no
   changes.
4. Merge. Terraform applies, then the deploy publishes the release the ref now
   names. If the ref and the file are from different releases, preflight fails
   before anything is published.
5. Check that `curl -fsS https://<function-app-hostname>/api/health` reports the
   new version.
6. If a new `teams-app-<instance_name>-…` release was published in your
   repository, a Teams Administrator uploads it as an update (Step 5.1).

To **roll back**, revert the upgrade pull request: the older ref, the older file
and, if the hash had changed, the older infrastructure come back together, and
the next deploy publishes the older release. A dispatch only ever redeploys what
is pinned.

### Switching from a copy of an older deploy workflow

If your repository runs its own copy of an earlier reusable deploy workflow
(inputs `env_dir`, `tag`, `function_app_name`, `azure_environment`, `runner`,
`concurrency_group`, `bot_app_id`, `teams_app_id`), only your caller changes:

- Point `uses:` at this workflow, at a release tag.
- Replace `env_dir` with `app_requirements_file` (the requirements file in that
  directory) and `teams_app_package_dir` (its `teams-app-package` directory).
- Set `instance_name` to the name your existing `teams-app-<name>-v…` releases
  use, so new package releases keep the same names and an unchanged package is
  not published again.
- Keep passing `tag` if your caller picks the version: only the hash gate
  applies then, as before.
- Pass the Azure ids as the `azure_*` inputs, the recommended path, or leave
  them empty to fall back to the `AZURE_*` secrets in your deploy environment.
  A caller that passes `secrets: inherit` today can keep it while it switches;
  its first run proves the fallback, and a missing id fails the deploy job's
  first step with a message naming it.
- Delete your copy of the workflow.

### What the deploy runner needs

- Linux on x64 (the pinned Azure Functions Core Tools build is linux-x64), with
  `bash`, `git`, `gh`, `az`, `unzip` and `sha256sum`. Provenance is verified in
  preflight, on a GitHub-hosted runner, so the deploy runner's `gh` doesn't need
  `gh attestation`.
- Egress to `github.com` and its release-asset downloads (the app ZIP and the
  Functions Core Tools ZIP come from GitHub releases, not npm), Microsoft Entra
  ID for the OIDC login, and the Function App's SCM endpoint.
- GitHub.com: the workflow checks its own scripts out at `job.workflow_sha`,
  which GitHub Enterprise Server does not provide.

Everything the workflow downloads is pinned: actions by commit SHA, Azure
Functions Core Tools by version and SHA-256 (checked on every run, including
from the cache), and the manifest validator's Python packages by version and
hash. The ~600 MB Functions Core Tools ZIP is cached in your repository's
Actions cache.

---

## Module Reference

### Inputs

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `name` | `string` | yes | -- | Base name for all resources. Max 22 characters excluding hyphens. |
| `resource_group_name` | `string` | yes | -- | Name of the pre-existing resource group. |
| `bot_app_id` | `string` | yes | -- | Client ID of the Bot App Registration (multi-tenant — set "Supported account types" to `AzureADMultipleOrgs`). |
| `api_app_id` | `string` | yes | -- | Client ID of the API App Registration (EasyAuth). |
| `api_app_object_id` | `string` | yes | -- | Object ID of the API App Registration. Used by Action Group AAD auth. |
| `alert_target_alias` | `string` | no | `""` | Channel alias for alert webhook delivery. Empty string disables alert resources. |
| `app_namespace` | `string` | no | `"TeamsNotificationBot"` | Root .NET namespace. Used in Log Analytics KQL queries for logger filtering. |
| `location` | `string` | no | `"norwayeast"` | Azure region for all resources. |
| `management_ip_rules` | `list(object)` | no | `[]` | IP addresses/CIDRs allowed for management access (deploy, test). Each object has `name`, `description`, `cidr`. |
| `vnet_address_space` | `list(string)` | no | `["10.0.0.0/16"]` | Address space for the virtual network. |
| `subnet_function_app_prefix` | `string` | no | `"10.0.0.0/24"` | CIDR for the Function App VNet integration subnet. Must be at least /24. |
| `subnet_private_endpoints_prefix` | `string` | no | `"10.0.1.0/24"` | CIDR for the private endpoints subnet. |
| `tags` | `map(string)` | no | `{}` | Additional tags merged with module-managed tags. Module tags take precedence on conflict. |
| `deploy_github_actions_from` | `map(object)` | no | `{}` | GitHub repos for CI/CD OIDC. Creates a deploy UAMI with FICs when non-empty. See [GitHub OIDC subject-claim formats](#github-oidc-subject-claim-formats). |
| `github_org` | `string` | no | `""` | GitHub organization name for OIDC subject claims in deploy UAMI FICs. |
| `github_org_id` | `string` | no | `""` | Permanent numeric GitHub ID of `github_org` (module `v1.2.0`+). Required when any repo in `deploy_github_actions_from` sets `repository_id`. |

### GitHub OIDC subject-claim formats

The FICs created by `deploy_github_actions_from` match the `sub` claim of the
GitHub Actions OIDC token **exactly**. Since 2026-07-15 GitHub issues two
subject-claim formats:

| Format | Subject | Issued by |
|--------|---------|-----------|
| Classic | `repo:<org>/<repo>:<trigger>` | Repos created before 2026-07-15 |
| Immutable | `repo:<org>@<org-id>/<repo>@<repo-id>:<trigger>` | Repos created, renamed, or transferred after 2026-07-15, and repos that opt in |

`<trigger>` is the same in both formats (e.g. `ref:refs/heads/main`,
`environment:prod`, `pull_request`). `<org-id>` and `<repo-id>` are permanent
numeric IDs that survive renames and transfers and are never reused. Look
them up with:

```bash
gh api /orgs/<org> --jq .id             # organization ID (dsb-norge = 29656362)
gh api /repos/<org>/<repo> --jq .id     # repository ID
```

A classic-subject FIC silently stops matching the moment its repo starts
issuing immutable subjects (rename, transfer, or opt-in), and for a repo
created after 2026-07-15 it never matches at all. Because the deploy identity
is a user-assigned managed identity, flexible FICs
(`claimsMatchingExpression`) are not an option -- Entra ID supports those on
app registrations only. The fix is to create **both** FICs per subject: the
classic one and an immutable-format twin.

Module `v1.2.0` and later create the twins natively: set `github_org_id` and
a `repository_id` per repository, and every FIC for that repo gets an
immutable-format twin alongside the classic one. Repos that omit
`repository_id` keep classic-only FICs (backwards compatible).

```hcl
module "teams_notification_bot" {
  source  = "dsb-norge/teams-notification-bot-lz/azurerm"
  version = "1.2.0"   # or later — required for github_org_id / repository_id
  # ...

  github_org    = "dsb-norge"
  github_org_id = "29656362"      # gh api /orgs/dsb-norge --jq .id

  deploy_github_actions_from = {
    "my-app-repo" = {
      environments  = ["prod"]
      repository_id = "123456789" # gh api /repos/dsb-norge/my-app-repo --jq .id
    }
  }
}
```

For repos created after 2026-07-15, the immutable-format FIC is the one that
actually matches -- the classic one is inert but harmless. See GitHub's
[changelog entry](https://github.blog/changelog/2026-04-23-immutable-subject-claims-for-github-actions-oidc-tokens/)
and the [OIDC reference](https://docs.github.com/en/actions/reference/security/oidc)
for details.

### Outputs

| Name | Description |
|------|-------------|
| `function_app_name` | The name of the Function App resource. |
| `function_app_hostname` | The default hostname of the Function App. |
| `bot_service_name` | The name of the Bot Service resource. |
| `storage_account_name` | The name of the Storage Account. |
| `uami_client_id` | Client ID of the bot's User-Assigned Managed Identity. |
| `uami_principal_id` | Principal ID of the bot's UAMI (needed for FIC setup). |
| `deploy_uami_client_id` | Client ID of the deploy UAMI. Null when `deploy_github_actions_from` is empty. |
| `resource_group_name` | The resource group name (passthrough from input). |
| `log_analytics_workspace_id` | The resource ID of the Log Analytics workspace. |
| `application_insights_connection_string` | App Insights connection string (sensitive). |
| `application_insights_instrumentation_key` | App Insights instrumentation key (sensitive). |
