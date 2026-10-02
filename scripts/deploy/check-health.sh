#!/usr/bin/env bash
#
# Decides whether there is anything to deploy, and says why.
#
# With SKIP_IF_CURRENT "false" the answer is always yes. With "true", the app's anonymous
# /api/health endpoint is asked which version it runs:
#
#   - another version     -> deploy                 reason "deploy"
#   - the target version  -> nothing to deploy      reason "current"
#   - no answer           -> nothing to deploy,     reason "unreachable"
#                            with a warning. A deploy is never blind: if the running version
#                            can't be read, there is no basis for an automatic deploy. A first
#                            deploy (no code yet, so no /api/health) is therefore always a
#                            forced one.
#
# The reason decides the package-manifest job too: it builds the Teams package after a deploy,
# and also when the app already runs the target version, so a branding change, or a package
# build that failed after a good deploy, is picked up by the next run. It never builds for
# "unreachable": a package for a version nobody confirmed is running would be wrong.
#
# Environment:
#   SKIP_IF_CURRENT         "true" or "false"
#   FUNCTION_APP_NAME       the function app; the URL is https://<name>.azurewebsites.net
#   TARGET_VERSION          the version about to be deployed
#   HEALTH_ATTEMPTS         tries before giving up (default 3)
#   HEALTH_RETRY_SECONDS    pause between tries (default 5)
#   HEALTH_TIMEOUT_SECONDS  per-try timeout (default 10)
#
# Outputs:
#   deploy           "true" when reason is "deploy", else "false"
#   reason           "deploy", "current" or "unreachable"
#   current_version  what /api/health reported; empty when it wasn't asked or didn't answer
set -euo pipefail
shopt -s inherit_errexit

# shellcheck source=lib.bash
source "$(dirname -- "${BASH_SOURCE[0]}")/lib.bash"

require_env FUNCTION_APP_NAME TARGET_VERSION
require_bool SKIP_IF_CURRENT
# Function app names are 2-60 letters, digits and hyphens, and become a host name here.
[[ "${FUNCTION_APP_NAME}" =~ ^[A-Za-z0-9][A-Za-z0-9-]{0,58}[A-Za-z0-9]$ ]] ||
  die "function_app_name '${FUNCTION_APP_NAME}' is not a valid function app name."

attempts="${HEALTH_ATTEMPTS:-3}"
retry_seconds="${HEALTH_RETRY_SECONDS:-5}"
timeout_seconds="${HEALTH_TIMEOUT_SECONDS:-10}"
[[ "${attempts}" =~ ^[1-9][0-9]*$ ]] || die "HEALTH_ATTEMPTS must be a positive integer, got '${attempts}'."

if [[ "${SKIP_IF_CURRENT}" == "false" ]]; then
  log "skip_if_current is off: deploying ${TARGET_VERSION} whatever is running now."
  set_output deploy true
  set_output reason deploy
  set_output current_version ""
  exit 0
fi

url="https://${FUNCTION_APP_NAME}.azurewebsites.net/api/health"
err="$(mktemp)"
trap 'rm -f -- "${err}"' EXIT

current=""
for ((i = 1; i <= attempts; i++)); do
  if body="$(curl -fsS --max-time "${timeout_seconds}" "${url}" 2>"${err}")"; then
    current="$(jq -r '.version | strings' <<<"${body}" 2>/dev/null)" || current=""
    # Anything that isn't a version is treated as no answer: it can't be compared.
    if [[ "${current}" =~ ${RELEASE_VERSION_RE} ]]; then
      break
    fi
    current=""
    log "${url} answered without a version (attempt ${i}/${attempts})."
  else
    log "${url} did not answer (attempt ${i}/${attempts}): $(head -c 300 -- "${err}")"
  fi
  if ((i < attempts)); then
    sleep "${retry_seconds}"
  fi
done

set_output current_version "${current}"

if [[ -z "${current}" ]]; then
  set_output deploy false
  set_output reason unreachable
  warning "Health check" "${url} did not report a version after ${attempts} attempts, so nothing was deployed. Run the caller without skip_if_current to deploy anyway; a first deploy always needs that."
  summary "### :warning: Nothing deployed" "" "\`/api/health\` on \`${FUNCTION_APP_NAME}\` did not report a version, and \`skip_if_current\` never deploys blind."
elif [[ "${current}" == "${TARGET_VERSION}" ]]; then
  set_output deploy false
  set_output reason current
  notice "Health check" "${FUNCTION_APP_NAME} already runs ${current}: nothing to deploy. The Teams package is still built."
  summary "### Nothing to deploy" "" "\`${FUNCTION_APP_NAME}\` already runs \`${current}\`. The Teams package is still built, and published if its content changed."
else
  set_output deploy true
  set_output reason deploy
  notice "Health check" "${FUNCTION_APP_NAME} runs ${current}: deploying ${TARGET_VERSION}."
fi
