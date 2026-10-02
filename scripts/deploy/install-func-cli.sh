#!/usr/bin/env bash
#
# Installs the pinned Azure Functions Core Tools (the func CLI) from its GitHub release.
#
# The prebuilt linux-x64 ZIP comes from github.com rather than npm: deploy runners behind an
# egress firewall commonly allow github.com, which they need for the release assets anyway, but
# not registry.npmjs.org. The ZIP's SHA-256 is pinned next to the version and checked on every
# run, including when the ZIP comes from the actions/cache entry, so neither a moved upstream
# asset nor a poisoned cache entry gets executed.
#
# Environment:
#   FUNC_CLI_VERSION      e.g. 4.15.2
#   FUNC_CLI_SHA256       SHA-256 of Azure.Functions.Cli.linux-x64.<version>.zip
#   FUNC_CLI_CACHE_DIR    holds the downloaded ZIP; the workflow caches this directory
#   FUNC_CLI_INSTALL_DIR  where the CLI is unpacked; added to GITHUB_PATH
#   GH_TOKEN              token for gh
#   RUNNER_ARCH           set by the runner; only X64 is supported
set -euo pipefail
shopt -s inherit_errexit

# shellcheck source=lib.bash
source "$(dirname -- "${BASH_SOURCE[0]}")/lib.bash"

readonly FUNC_CLI_REPO="Azure/azure-functions-core-tools"

require_env FUNC_CLI_VERSION FUNC_CLI_SHA256 FUNC_CLI_CACHE_DIR FUNC_CLI_INSTALL_DIR
[[ "${FUNC_CLI_VERSION}" =~ ${SEMVER_RE} ]] || die "FUNC_CLI_VERSION '${FUNC_CLI_VERSION}' is not X.Y.Z."
[[ "${FUNC_CLI_SHA256}" =~ ^[0-9a-f]{64}$ ]] || die "FUNC_CLI_SHA256 is not a SHA-256 hex digest."
if [[ -n "${RUNNER_ARCH:-}" && "${RUNNER_ARCH}" != "X64" ]]; then
  die "The pinned func CLI is the linux-x64 build, but this runner is ${RUNNER_ARCH}."
fi

asset="Azure.Functions.Cli.linux-x64.${FUNC_CLI_VERSION}.zip"
zip="${FUNC_CLI_CACHE_DIR}/${asset}"
mkdir -p -- "${FUNC_CLI_CACHE_DIR}"

if [[ -f "${zip}" ]]; then
  log "Using the cached ${asset}."
else
  log "Downloading ${asset} from ${FUNC_CLI_REPO}."
  gh release download "${FUNC_CLI_VERSION}" --repo "${FUNC_CLI_REPO}" --pattern "${asset}" --dir "${FUNC_CLI_CACHE_DIR}"
fi

if ! printf '%s  %s\n' "${FUNC_CLI_SHA256}" "${zip}" | sha256sum --check --strict --quiet; then
  # Remove it, so a corrupt download isn't saved to the cache. A restored cache entry that fails
  # keeps failing until its key changes: fail closed rather than run it.
  rm -f -- "${zip}"
  die "${asset} does not match the pinned SHA-256 ${FUNC_CLI_SHA256}."
fi

rm -rf -- "${FUNC_CLI_INSTALL_DIR}"
mkdir -p -- "${FUNC_CLI_INSTALL_DIR}"
unzip -q -- "${zip}" -d "${FUNC_CLI_INSTALL_DIR}"
chmod +x -- "${FUNC_CLI_INSTALL_DIR}/func"
# gozip builds the deployment ZIP on Linux; not every build ships it.
if [[ -f "${FUNC_CLI_INSTALL_DIR}/gozip" ]]; then
  chmod +x -- "${FUNC_CLI_INSTALL_DIR}/gozip"
fi

reported="$("${FUNC_CLI_INSTALL_DIR}/func" --version)"
notice "func CLI" "Azure Functions Core Tools ${FUNC_CLI_VERSION} (reports ${reported})"
printf '%s\n' "${FUNC_CLI_INSTALL_DIR}" >>"${GITHUB_PATH:?GITHUB_PATH is not set}"
