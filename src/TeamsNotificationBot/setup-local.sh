#!/usr/bin/env bash
set -euo pipefail

###############################################################################
# setup-local.sh — Generate local.settings.json for local dev
#
# Usage: ./setup-local.sh [offline|online]
#
#   offline (default) — Mock values, no Azure access needed
#   online            — Real config, seeds Azurite from Azure
#
# Online mode requires these environment variables:
#   BOT_APP_ID         — Entra ID app registration client ID for Bot Framework auth
#   TENANT_ID          — Azure AD tenant ID
#   BOT_CLIENT_SECRET  — Bot app registration client secret (create via: az ad app credential reset)
#   STORAGE_ACCOUNT    — Storage account name to seed conversation references from
###############################################################################

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SETTINGS_FILE="${SCRIPT_DIR}/local.settings.json"

# Azure resource constants — override via environment variables for your deployment
BOT_APP_ID="${BOT_APP_ID:-00000000-0000-0000-0000-000000000000}"
TENANT_ID="${TENANT_ID:-00000000-0000-0000-0000-000000000000}"
BOT_CLIENT_SECRET="${BOT_CLIENT_SECRET:-}"
STORAGE_ACCOUNT="${STORAGE_ACCOUNT:-your-storage-account}"
TABLE_NAME="conversationreferences"
IDEMPOTENCY_TABLE_NAME="idempotencykeys"
QUEUE_NAME="notifications"

# Azurite connection string
AZURITE_CONN="DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1"

## Channel aliases are stored in Table Storage (aliases table)
## and managed via bot commands (set-alias, remove-alias, list-aliases).

usage() {
    echo "Usage: $0 [offline|online]"
    echo ""
    echo "  offline  Generate local.settings.json with mock values (default)"
    echo "  online   Seed Azurite from Azure, generate real config"
    echo ""
    echo "Prerequisites for online mode:"
    echo "  - Environment variables set: BOT_APP_ID, TENANT_ID, BOT_CLIENT_SECRET, STORAGE_ACCOUNT"
    echo "  - az CLI logged in (az login), correct subscription selected"
    echo "  - Azurite running on default ports: azurite --silent --location /tmp/azurite --skipApiVersionCheck"
    echo "  - jq installed"
    echo "  - Storage Table Data Reader role on the storage account"
    exit 1
}

write_settings() {
    local teams_disabled="$1"
    local bot_secret="$2"

    ## In Azure, the bot uses FIC (FederatedCredentials) via UAMI — no secret needed.
    ## Locally, UAMI is not available, so we override back to ClientSecret for online mode.
    ## Offline mode doesn't connect to Bot Framework, so the secret value doesn't matter.
    cat > "${SETTINGS_FILE}" <<EOF
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "TEAMS_INTEGRATION_DISABLED": "${teams_disabled}",
    "DEBUG_MODE": "true",
    "BotAppId": "${BOT_APP_ID}",
    "TenantId": "${TENANT_ID}",
    "Connections__ServiceConnection__Settings__AuthType": "ClientSecret",
    "Connections__ServiceConnection__Settings__ClientSecret": "${bot_secret}"
  }
}
EOF
    echo "Wrote ${SETTINGS_FILE}"
}

setup_offline() {
    echo "=== Setting up OFFLINE mode ==="
    echo ""
    write_settings "true" "not-needed-offline"
    echo ""
    echo "Ready! Run: func host start"
}

check_prerequisites() {
    echo "Checking prerequisites..."

    # Check az CLI
    if ! command -v az &>/dev/null; then
        echo "ERROR: az CLI not found. Install: https://learn.microsoft.com/en-us/cli/azure/install-azure-cli"
        exit 1
    fi
    if ! az account show &>/dev/null; then
        echo "ERROR: Not logged in to Azure. Run: az login"
        exit 1
    fi
    echo "  az CLI: OK ($(az account show --query user.name -o tsv))"

    # Check required env vars
    if [[ "${BOT_APP_ID}" == "00000000-0000-0000-0000-000000000000" ]]; then
        echo "ERROR: BOT_APP_ID not set. Export it before running online mode."
        exit 1
    fi
    if [[ "${TENANT_ID}" == "00000000-0000-0000-0000-000000000000" ]]; then
        echo "ERROR: TENANT_ID not set. Export it before running online mode."
        exit 1
    fi
    if [[ -z "${BOT_CLIENT_SECRET}" ]]; then
        echo "ERROR: BOT_CLIENT_SECRET not set. Export it before running online mode."
        echo "  Create one with: az ad app credential reset --id <bot-app-id> --display-name local-dev"
        exit 1
    fi
    if [[ "${STORAGE_ACCOUNT}" == "your-storage-account" ]]; then
        echo "ERROR: STORAGE_ACCOUNT not set. Export it before running online mode."
        exit 1
    fi
    echo "  Environment variables: OK"

    # Check jq
    if ! command -v jq &>/dev/null; then
        echo "ERROR: jq not found. Install: sudo apt install jq"
        exit 1
    fi
    echo "  jq: OK"

    # Check Azurite table service (Azurite returns 400 for bare GET, so accept any HTTP response)
    if ! curl -s -o /dev/null -w '' --connect-timeout 3 "http://127.0.0.1:10002/" 2>/dev/null; then
        echo "ERROR: Azurite table service not responding on port 10002"
        echo "Start Azurite: azurite --silent --location /tmp/azurite --skipApiVersionCheck"
        exit 1
    fi
    echo "  Azurite: OK"
    echo ""
}

seed_azurite() {
    echo "Seeding Azurite from Azure Table Storage..."

    # Create tables and queue (idempotent)
    az storage table create \
        --name "${TABLE_NAME}" \
        --connection-string "${AZURITE_CONN}" \
        --output none 2>/dev/null || true

    az storage table create \
        --name "${IDEMPOTENCY_TABLE_NAME}" \
        --connection-string "${AZURITE_CONN}" \
        --output none 2>/dev/null || true

    az storage queue create \
        --name "${QUEUE_NAME}" \
        --connection-string "${AZURITE_CONN}" \
        --output none 2>/dev/null || true

    az storage queue create \
        --name "botoperations" \
        --connection-string "${AZURITE_CONN}" \
        --output none 2>/dev/null || true

    echo "  Created tables '${TABLE_NAME}', '${IDEMPOTENCY_TABLE_NAME}' and queues '${QUEUE_NAME}', 'botoperations'"

    # Query all conversation references from Azure
    echo "  Fetching conversation references from Azure..."
    local entities
    entities=$(az storage entity query \
        --table-name "${TABLE_NAME}" \
        --account-name "${STORAGE_ACCOUNT}" \
        --auth-mode login \
        -o json)

    local count
    count=$(echo "${entities}" | jq '.items | length')

    if [[ "${count}" -eq 0 ]]; then
        echo "  WARNING: No conversation references found in Azure. Azurite table will be empty."
        echo ""
        return
    fi

    # Seed each entity into Azurite
    echo "  Seeding ${count} entities into Azurite..."
    echo "${entities}" | jq -c '.items[]' | while IFS= read -r entity; do
        local pk rk
        pk=$(echo "${entity}" | jq -r '.PartitionKey')
        rk=$(echo "${entity}" | jq -r '.RowKey')

        # Build entity arguments for az storage entity replace/insert
        # Extract the fields we care about
        local conv_ref installed_at last_used
        conv_ref=$(echo "${entity}" | jq -r '.ConversationReference // empty')
        installed_at=$(echo "${entity}" | jq -r '.InstalledAt // empty')
        last_used=$(echo "${entity}" | jq -r '.LastUsed // empty')

        # Build the entity args array
        local -a entity_args=(
            PartitionKey="${pk}"
            RowKey="${rk}"
        )
        [[ -n "${conv_ref}" ]] && entity_args+=("ConversationReference=${conv_ref}")
        [[ -n "${installed_at}" ]] && entity_args+=("InstalledAt=${installed_at}" "InstalledAt@odata.type=Edm.DateTime")
        [[ -n "${last_used}" ]] && entity_args+=("LastUsed=${last_used}" "LastUsed@odata.type=Edm.DateTime")

        # Try insert, fall back to replace for re-runs
        if ! az storage entity insert \
            --table-name "${TABLE_NAME}" \
            --connection-string "${AZURITE_CONN}" \
            --entity "${entity_args[@]}" \
            --output none 2>/dev/null; then
            az storage entity replace \
                --table-name "${TABLE_NAME}" \
                --connection-string "${AZURITE_CONN}" \
                --entity "${entity_args[@]}" \
                --output none 2>/dev/null
        fi

        echo "    Seeded: ${pk} / ${rk}"
    done

    echo "  Done seeding."
    echo ""
}

setup_online() {
    echo "=== Setting up ONLINE mode ==="
    echo ""

    check_prerequisites

    seed_azurite

    write_settings "false" "${BOT_CLIENT_SECRET}"
    echo ""
    echo "Ready! Ensure Azurite is running (with --skipApiVersionCheck), then: func host start"
    echo ""
    echo "Test with (Bearer token auth — requires EasyAuth headers in local dev):"
    echo "  curl -X POST http://localhost:7071/api/v1/notify/my-alias \\"
    echo "    -H 'Content-Type: application/json' \\"
    echo "    -H 'X-MS-CLIENT-PRINCIPAL-ID: <your-oid>' \\"
    echo "    -H 'X-MS-CLIENT-PRINCIPAL: <base64-encoded-principal>' \\"
    echo "    -d '{\"message\": \"Hello from local dev!\", \"format\": \"text\"}'"
}

# Main
MODE="${1:-offline}"

case "${MODE}" in
    offline)
        setup_offline
        ;;
    online)
        setup_online
        ;;
    -h|--help|help)
        usage
        ;;
    *)
        echo "ERROR: Unknown mode '${MODE}'"
        echo ""
        usage
        ;;
esac
