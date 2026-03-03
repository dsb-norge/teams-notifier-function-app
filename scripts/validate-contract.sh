#!/usr/bin/env bash
#
# validate-contract.sh
#
# Cross-checks app-contract.json against the actual app source code.
# Validates schema completeness, queue/table/route consistency,
# command handler existence, Teams manifest limits, and version format.
#
# Usage:
#   bash validate-contract.sh [--contract <path>]
#
# Default:
#   --contract ../src/TeamsNotificationBot/app-contract.json

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="${SCRIPT_DIR}/../src/TeamsNotificationBot"
CONTRACT_FILE="${APP_DIR}/app-contract.json"

ERRORS=0
WARNINGS=0

# --- Parse arguments ---
while [[ $# -gt 0 ]]; do
  case "$1" in
    --contract) CONTRACT_FILE="$2"; shift 2 ;;
    -h|--help)
      echo "Usage: $(basename "$0") [--contract <path>]"
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

if [[ ! -f "${CONTRACT_FILE}" ]]; then
  echo "Error: Contract file not found: ${CONTRACT_FILE}" >&2
  exit 1
fi

echo "Validating contract: ${CONTRACT_FILE}"
echo "  App directory: ${APP_DIR}"
echo ""

# === 1. Schema completeness ===
echo "[1/7] Schema completeness"
REQUIRED_KEYS=("version" "runtime" "queues" "tables" "routes" "env_vars" "easyauth" "bot" "command_lists")
for key in "${REQUIRED_KEYS[@]}"; do
  if jq -e ".${key}" "${CONTRACT_FILE}" &>/dev/null; then
    pass "Top-level key '${key}' present"
  else
    fail "Missing top-level key '${key}'"
  fi
done

# Check runtime sub-keys
for subkey in dotnet functions; do
  if jq -e ".runtime.${subkey}" "${CONTRACT_FILE}" &>/dev/null; then
    pass "runtime.${subkey} present"
  else
    fail "Missing runtime.${subkey}"
  fi
done
echo ""

# === 2. Queue names match code ===
echo "[2/7] Queue names vs code"
CONTRACT_QUEUES=$(jq -r '.queues[]' "${CONTRACT_FILE}" | sort)
CODE_QUEUES=$(grep -rohP 'QueueTrigger\("\K[^"]+' "${APP_DIR}/Functions/"*.cs \
  | sort -u)

while IFS= read -r queue; do
  if echo "${CODE_QUEUES}" | grep -qx "${queue}"; then
    pass "Queue '${queue}' found in code"
  else
    fail "Queue '${queue}' in contract but NOT in code"
  fi
done <<< "${CONTRACT_QUEUES}"

while IFS= read -r queue; do
  if echo "${CONTRACT_QUEUES}" | grep -qx "${queue}"; then
    :
  else
    fail "Queue '${queue}' in code but NOT in contract"
  fi
done <<< "${CODE_QUEUES}"
echo ""

# === 3. Table names match code ===
echo "[3/7] Table names vs code"
CONTRACT_TABLES=$(jq -r '.tables[]' "${CONTRACT_FILE}" | sort)

CODE_TABLES_PROGRAM=$(grep -ohP 'new TableClient\([^,]+,\s*"\K[^"]+' "${APP_DIR}/Program.cs" \
  | sort -u)
CODE_TABLES_COUNTER=$(grep -ohP 'AzureTableCounterStore\([^,]+,\s*"\K[^"]+' "${APP_DIR}/Program.cs" \
  | sort -u)
CODE_TABLES=$(printf '%s\n' ${CODE_TABLES_PROGRAM} ${CODE_TABLES_COUNTER} | sort -u)

while IFS= read -r table; do
  if echo "${CODE_TABLES}" | grep -qx "${table}"; then
    pass "Table '${table}' found in code"
  else
    fail "Table '${table}' in contract but NOT in code"
  fi
done <<< "${CONTRACT_TABLES}"

while IFS= read -r table; do
  if echo "${CONTRACT_TABLES}" | grep -qx "${table}"; then
    :
  else
    fail "Table '${table}' in code but NOT in contract"
  fi
done <<< "${CODE_TABLES}"
echo ""

# === 4. Route values match code ===
echo "[4/7] Route values vs code"
CONTRACT_ROUTES=$(jq -r '.routes | values[]' "${CONTRACT_FILE}" | sed 's|^/api/||' | sort)
CODE_ROUTES=$(grep -rohP 'Route\s*=\s*"\K[^"]+' "${APP_DIR}/Functions/"*.cs \
  | sort -u)

while IFS= read -r route; do
  if echo "${CODE_ROUTES}" | grep -qx "${route}"; then
    pass "Route '${route}' found in code"
  else
    fail "Route '${route}' in contract but NOT in code"
  fi
done <<< "${CONTRACT_ROUTES}"

while IFS= read -r route; do
  # Strip /api/ prefix for comparison
  if echo "${CONTRACT_ROUTES}" | grep -qx "${route}"; then
    :
  else
    fail "Route '${route}' in code but NOT in contract"
  fi
done <<< "${CODE_ROUTES}"
echo ""

# === 5. Command titles match handlers in TeamsBotHandler.cs ===
echo "[5/7] Command titles vs handlers"
HANDLER_FILE="${APP_DIR}/Services/TeamsBotHandler.cs"
if [[ ! -f "${HANDLER_FILE}" ]]; then
  fail "TeamsBotHandler.cs not found"
else
  # Extract all unique command titles from contract
  COMMAND_TITLES=$(jq -r '.command_lists[].commands[].title' "${CONTRACT_FILE}" | sort -u)

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

# === 6. Teams manifest limits ===
echo "[6/7] Teams manifest limits"
NUM_COMMAND_LISTS=$(jq '.command_lists | length' "${CONTRACT_FILE}")
if [[ ${NUM_COMMAND_LISTS} -le 3 ]]; then
  pass "Command lists count: ${NUM_COMMAND_LISTS} (max 3)"
else
  fail "Too many command lists: ${NUM_COMMAND_LISTS} (max 3)"
fi

# Check each command list max 10 commands
for i in $(seq 0 $((NUM_COMMAND_LISTS - 1))); do
  SCOPE=$(jq -r ".command_lists[${i}].scopes[0]" "${CONTRACT_FILE}")
  NUM_COMMANDS=$(jq ".command_lists[${i}].commands | length" "${CONTRACT_FILE}")
  if [[ ${NUM_COMMANDS} -le 10 ]]; then
    pass "Scope '${SCOPE}': ${NUM_COMMANDS} commands (max 10)"
  else
    fail "Scope '${SCOPE}': ${NUM_COMMANDS} commands exceeds max 10"
  fi
done
echo ""

# === 7. Version format (semver) ===
echo "[7/7] Version format"
APP_VERSION=$(jq -r '.version' "${CONTRACT_FILE}")
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
  fail "Version mismatch: contract='${APP_VERSION}' vs AppInfo.cs='${CODE_VERSION}'"
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
