#!/usr/bin/env bash
#
# Downloads one release asset and verifies that it was built from the release tag's commit.
#
# release.yml and prerelease.yml attest the function app ZIP and app-requirements.json on a
# GitHub-hosted runner. Both build the commit the tag points at, and the attestation records
# that commit. Release assets can be replaced, so an attestation from this repository alone is
# not enough: an older release's legitimately attested file, uploaded under a newer tag, would
# pass. The attestation must therefore name this tag's commit (--source-digest). Tags in this
# repository can't be moved or deleted.
#
# Runs in preflight, on a GitHub-hosted runner. The later jobs download the same asset again and
# compare it with the SHA-256 output here (download-asset-by-digest.sh), so `gh attestation` is
# never needed on a self-hosted runner, and a file replaced between the jobs is caught.
#
# Environment:
#   APP_REPO  owner/repo of the release (the attestation must name the same repository)
#   TAG       the release tag
#   ASSET     the asset name
#   DEST_DIR  where to put it (created if missing)
#   GH_TOKEN  token for gh
#
# Outputs:
#   path      the downloaded file
#   sha256    its SHA-256
set -euo pipefail
shopt -s inherit_errexit

# shellcheck source=lib.bash
source "$(dirname -- "${BASH_SOURCE[0]}")/lib.bash"

require_env APP_REPO TAG ASSET DEST_DIR
[[ "${ASSET}" =~ ^[A-Za-z0-9._-]+$ ]] || die "Asset name '${ASSET}' has characters that gh would read as a pattern."

# refs/tags/ so a branch of the same name can't be picked instead.
commit="$(gh api "repos/${APP_REPO}/commits/refs/tags/${TAG}" --jq .sha)" ||
  die "Could not resolve the commit of tag ${TAG} in ${APP_REPO}."
[[ "${commit}" =~ ^[0-9a-f]{40}$ ]] || die "Tag ${TAG} resolved to '${commit}', not a commit SHA."

mkdir -p -- "${DEST_DIR}"
file="${DEST_DIR}/${ASSET}"
rm -f -- "${file}"

gh release download "${TAG}" --repo "${APP_REPO}" --pattern "${ASSET}" --dir "${DEST_DIR}"
[[ -f "${file}" ]] || die "Release ${TAG} has no asset ${ASSET}."

if ! gh attestation verify "${file}" --repo "${APP_REPO}" --source-digest "${commit}" --deny-self-hosted-runners; then
  die "${ASSET} from ${TAG} has no valid build provenance attestation from ${APP_REPO} for the tag's commit ${commit}. Releases before 1.3.1 were not attested and cannot be deployed with this workflow."
fi

digest="$(sha256sum -- "${file}")"
digest="${digest%% *}"
log "Downloaded ${ASSET} from ${TAG} and verified it was built from ${commit} (sha256 ${digest})."
set_output path "${file}"
set_output sha256 "${digest}"
