#!/usr/bin/env bash
#
# Downloads one release asset and checks it is the exact file preflight verified.
#
# preflight verifies each asset's build provenance against the tag's commit
# (download-verified-asset.sh) and outputs its SHA-256. The deploy and package jobs download the
# asset again and compare it with that digest, so they run nothing they couldn't trust, need no
# `gh attestation` on a self-hosted runner, and notice an asset replaced in between.
#
# Environment:
#   APP_REPO         owner/repo of the release
#   TAG              the release tag
#   ASSET            the asset name
#   DEST_DIR         where to put it (created if missing)
#   EXPECTED_SHA256  the digest preflight verified
#   GH_TOKEN         token for gh
#
# Outputs:
#   path             the downloaded file
set -euo pipefail
shopt -s inherit_errexit

# shellcheck source=lib.bash
source "$(dirname -- "${BASH_SOURCE[0]}")/lib.bash"

require_env APP_REPO TAG ASSET DEST_DIR EXPECTED_SHA256
[[ "${ASSET}" =~ ^[A-Za-z0-9._-]+$ ]] || die "Asset name '${ASSET}' has characters that gh would read as a pattern."
[[ "${EXPECTED_SHA256}" =~ ^[0-9a-f]{64}$ ]] || die "EXPECTED_SHA256 is not a SHA-256 hex digest."

mkdir -p -- "${DEST_DIR}"
file="${DEST_DIR}/${ASSET}"
rm -f -- "${file}"

gh release download "${TAG}" --repo "${APP_REPO}" --pattern "${ASSET}" --dir "${DEST_DIR}"
[[ -f "${file}" ]] || die "Release ${TAG} has no asset ${ASSET}."

if ! printf '%s  %s\n' "${EXPECTED_SHA256}" "${file}" | sha256sum --check --strict --quiet; then
  rm -f -- "${file}"
  die "${ASSET} on ${TAG} is not the file preflight verified (sha256 ${EXPECTED_SHA256}): the release asset changed during the run."
fi

log "Downloaded ${ASSET} from ${TAG}; it is the file preflight verified."
set_output path "${file}"
