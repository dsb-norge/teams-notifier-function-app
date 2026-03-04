#!/usr/bin/env bash
#
# validate-requirements.sh
#
# Cross-checks app-requirements.json against the actual app source code.
# Validates schema completeness, queue consistency, route/endpoint consistency,
# auth role, infrastructure requirements hash integrity, command handlers,
# Teams manifest limits, and version format.
#
# Usage:
#   bash validate-requirements.sh [--requirements <path>]
#
# Default:
#   --requirements ../src/TeamsNotificationBot/app-requirements.json

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="${SCRIPT_DIR}/../src/TeamsNotificationBot"
REQ_FILE="${APP_DIR}/app-requirements.json"

ERRORS=0
WARNINGS=0

# --- Parse arguments ---
while [[ $# -gt 0 ]]; do
  case "$1" in
    --requirements) REQ_FILE="$2"; shift 2 ;;
    -h|--help)
      echo "Usage: $(basename "$0") [--requirements <path>]"
      exit 0
      ;;
    *)
      echo "Error: Unknown option '$1'" >&2
      exit 1
      ;;
  esac
done

# --- Helpers ---
pass() { echo "  PASS: $1"; }
fail() { echo "  FAIL: $1" >&2; ((ERRORS++)); }
warn() { echo "  WARN: $1" >&2; ((WARNINGS++)); }

# --- Validate prerequisites ---
if ! command -v jq &>/dev/null; then
  echo "Error: 'jq' is required." >&2
  exit 1
fi

if [[ ! -f "${REQ_FILE}" ]]; then
  echo "Error: Requirements file not found: ${REQ_FILE}" >&2
  exit 1
fi

echo "Validating requirements: ${REQ_FILE}"
echo "  App directory: ${APP_DIR}"
echo ""

# === 1. Schema completeness ===
echo "[1/9] Schema completeness"
REQUIRED_KEYS=("notifier_application_version" "infrastructure_requirements_unique_hash" "function_app_runtime_version" "storage_account_required_queues" "well_known_routes" "function_app_required_app_settings" "bot_auth_settings" "bot_service" "teams_app_configuration" "teams_app_command_lists")
for key in "${REQUIRED_KEYS[@]}"; do
  if jq -e ".${key}" "${REQ_FILE}" &>/dev/null; then
    pass "Top-level key '${key}' present"
  else
    fail "Missing top-level key '${key}'"
  fi
done
echo ""

# === 2. Queue names match code ===
echo "[2/9] Queue names vs code"
REQ_QUEUES=$(jq -r '.storage_account_required_queues[]' "${REQ_FILE}" | sort)
CODE_QUEUES=$(grep -rohP 'QueueTrigger\("\K[^"]+' "${APP_DIR}/Functions/"*.cs \
  | sort -u)

while IFS= read -r queue; do
  if echo "${CODE_QUEUES}" | grep -qx "${queue}"; then
    pass "Queue '${queue}' found in code"
  else
    fail "Queue '${queue}' in requirements but NOT in code"
  fi
done <<< "${REQ_QUEUES}"

while IFS= read -r queue; do
  if echo "${REQ_QUEUES}" | grep -qx "${queue}"; then
    :
  else
    fail "Queue '${queue}' in code but NOT in requirements"
  fi
done <<< "${CODE_QUEUES}"
echo ""

# === 3. Alert route matches code ===
echo "[3/9] Alert route vs code"
REQ_ALERT_ROUTE=$(jq -r '.well_known_routes.azure_alert_webhook_receiver_endpoint' "${REQ_FILE}" | sed 's|^/api/||')
CODE_ALERT_ROUTE=$(grep -ohP 'Route\s*=\s*"\K[^"]+' "${APP_DIR}/Functions/AlertFunction.cs" | head -1)

if [[ "${REQ_ALERT_ROUTE}" == "${CODE_ALERT_ROUTE}" ]]; then
  pass "Alert route matches AlertFunction.cs (/api/${CODE_ALERT_ROUTE})"
else
  fail "Alert route mismatch: requirements='/api/${REQ_ALERT_ROUTE}' vs code='/api/${CODE_ALERT_ROUTE}'"
fi
echo ""

# === 4. Bot messaging endpoint matches code ===
echo "[4/9] Messaging endpoint vs code"
REQ_ENDPOINT=$(jq -r '.bot_service.messaging_endpoint' "${REQ_FILE}" | sed 's|^/api/||')
CODE_ENDPOINT=$(grep -ohP 'Route\s*=\s*"\K[^"]+' "${APP_DIR}/Functions/BotMessagesFunction.cs" | head -1)

if [[ "${REQ_ENDPOINT}" == "${CODE_ENDPOINT}" ]]; then
  pass "Messaging endpoint matches BotMessagesFunction.cs (/api/${CODE_ENDPOINT})"
else
  fail "Messaging endpoint mismatch: requirements='/api/${REQ_ENDPOINT}' vs code='/api/${CODE_ENDPOINT}'"
fi
echo ""

# === 5. Auth required_role matches code ===
echo "[5/9] Auth required_role vs code"
REQ_ROLE=$(jq -r '.bot_auth_settings.required_role' "${REQ_FILE}")
CODE_ROLE=$(grep -oP 'RequiredRole\s*=\s*"\K[^"]+' "${APP_DIR}/Middleware/AuthMiddleware.cs")

if [[ "${REQ_ROLE}" == "${CODE_ROLE}" ]]; then
  pass "bot_auth_settings.required_role matches AuthMiddleware.cs (${CODE_ROLE})"
else
  fail "bot_auth_settings.required_role mismatch: requirements='${REQ_ROLE}' vs code='${CODE_ROLE}'"
fi
echo ""

# === 6. Infra hash integrity ===
echo "[6/9] Infra hash integrity"
COMPUTED_HASH=$(jq -S '{function_app_runtime_version, storage_account_required_queues, well_known_routes, function_app_required_app_settings, bot_auth_settings, bot_service}' "${REQ_FILE}" \
  | sha256sum | cut -c1-12)
FILE_HASH=$(jq -r '.infrastructure_requirements_unique_hash' "${REQ_FILE}")

if [[ "${COMPUTED_HASH}" == "${FILE_HASH}" ]]; then
  pass "infrastructure_requirements_unique_hash matches computed value (${FILE_HASH})"
else
  fail "infrastructure_requirements_unique_hash mismatch: file='${FILE_HASH}' vs computed='${COMPUTED_HASH}'"
fi
echo ""

# === 7. Command titles match handlers in TeamsBotHandler.cs ===
echo "[7/9] Command titles vs handlers"
HANDLER_FILE="${APP_DIR}/Services/TeamsBotHandler.cs"
if [[ ! -f "${HANDLER_FILE}" ]]; then
  fail "TeamsBotHandler.cs not found"
else
  # Extract all unique command titles from requirements
  COMMAND_TITLES=$(jq -r '.teams_app_command_lists[].commands[].title' "${REQ_FILE}" | sort -u)

  while IFS= read -r cmd; do
    # Search for the command title in handler (case patterns, string comparisons)
    if grep -qP "(\"${cmd}\"|'${cmd}')" "${HANDLER_FILE}"; then
      pass "Command '${cmd}' has handler"
    else
      fail "Command '${cmd}' has no handler in TeamsBotHandler.cs"
    fi
  done <<< "${COMMAND_TITLES}"
fi
echo ""

# === 8. Teams manifest limits ===
echo "[8/9] Teams manifest limits"
NUM_COMMAND_LISTS=$(jq '.teams_app_command_lists | length' "${REQ_FILE}")
if [[ ${NUM_COMMAND_LISTS} -le 3 ]]; then
  pass "Command lists count: ${NUM_COMMAND_LISTS} (max 3)"
else
  fail "Too many command lists: ${NUM_COMMAND_LISTS} (max 3)"
fi

# Check each command list max 10 commands
for i in $(seq 0 $((NUM_COMMAND_LISTS - 1))); do
  SCOPE=$(jq -r ".teams_app_command_lists[${i}].scopes[0]" "${REQ_FILE}")
  NUM_COMMANDS=$(jq ".teams_app_command_lists[${i}].commands | length" "${REQ_FILE}")
  if [[ ${NUM_COMMANDS} -le 10 ]]; then
    pass "Scope '${SCOPE}': ${NUM_COMMANDS} commands (max 10)"
  else
    fail "Scope '${SCOPE}': ${NUM_COMMANDS} commands exceeds max 10"
  fi
done
echo ""

# === 9. Version format (semver) ===
echo "[9/9] Version format"
APP_VERSION=$(jq -r '.notifier_application_version' "${REQ_FILE}")
if [[ "${APP_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  pass "Version '${APP_VERSION}' is valid semver"
else
  fail "Version '${APP_VERSION}' is not valid semver (expected X.Y.Z)"
fi

# Cross-check version against AppInfo.cs
CODE_VERSION=$(grep -oP 'Version\s*=\s*"\K[^"]+' "${APP_DIR}/Helpers/AppInfo.cs")
if [[ "${APP_VERSION}" == "${CODE_VERSION}" ]]; then
  pass "Version matches AppInfo.cs (${CODE_VERSION})"
else
  fail "Version mismatch: requirements='${APP_VERSION}' vs AppInfo.cs='${CODE_VERSION}'"
fi
echo ""

# === Summary ===
echo "========================================="
if [[ ${ERRORS} -eq 0 ]]; then
  echo "RESULT: ALL CHECKS PASSED"
  [[ ${WARNINGS} -gt 0 ]] && echo "  (${WARNINGS} warning(s))"
  exit 0
else
  echo "RESULT: ${ERRORS} ERROR(S), ${WARNINGS} WARNING(S)"
  exit 1
fi
