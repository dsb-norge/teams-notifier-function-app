#!/usr/bin/env bash
#
# Waits until a release carries every named asset.
#
# release-please publishes the release, and with it the tag, before release.yml has built and
# uploaded the assets. A caller that reacts to the new release (or pins the new tag right away)
# can therefore start before the assets exist.
#
# Environment:
#   APP_REPO       owner/repo of the release
#   TAG            the release tag
#   ASSETS         space-separated asset names that must all be present
#   GH_TOKEN       token for gh
#   WAIT_ATTEMPTS  how many times to look (default 30)
#   WAIT_SECONDS   pause between looks (default 10)
#
# A release that doesn't exist fails at once; any other gh failure is retried, since it is
# usually transient.
set -euo pipefail
shopt -s inherit_errexit

# shellcheck source=lib.bash
source "$(dirname -- "${BASH_SOURCE[0]}")/lib.bash"

require_env APP_REPO TAG ASSETS
attempts="${WAIT_ATTEMPTS:-30}"
interval="${WAIT_SECONDS:-10}"
[[ "${attempts}" =~ ^[1-9][0-9]*$ ]] || die "WAIT_ATTEMPTS must be a positive integer, got '${attempts}'."
[[ "${interval}" =~ ^[0-9]+$ ]] || die "WAIT_SECONDS must be a non-negative integer, got '${interval}'."

read -r -a wanted <<<"${ASSETS}"
((${#wanted[@]} > 0)) || die "ASSETS names no asset."

err="$(mktemp)"
trap 'rm -f -- "${err}"' EXIT

for ((i = 1; i <= attempts; i++)); do
  if names="$(gh release view "${TAG}" --repo "${APP_REPO}" --json assets --jq '.assets[].name' 2>"${err}")"; then
    missing=()
    for asset in "${wanted[@]}"; do
      grep -qxF -- "${asset}" <<<"${names}" || missing+=("${asset}")
    done
    if ((${#missing[@]} == 0)); then
      log "Release ${TAG} has every required asset: ${wanted[*]}."
      exit 0
    fi
    log "Release ${TAG} is still missing ${missing[*]} (attempt ${i}/${attempts})."
  elif grep -qi 'release not found' "${err}"; then
    die "Release ${TAG} does not exist in ${APP_REPO}."
  else
    log "Could not read release ${TAG} (attempt ${i}/${attempts}): $(head -c 500 -- "${err}")"
  fi
  if ((i < attempts)); then
    sleep "${interval}"
  fi
done

die "Release ${TAG} still lacks ${missing[*]:-its assets} after ${attempts} attempts."
