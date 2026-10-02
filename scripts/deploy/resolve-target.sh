#!/usr/bin/env bash
#
# Resolves the app release to deploy.
#
# The workflow's own version is the one release-please records in .release-please-manifest.json,
# read at the commit the workflow was called at (preflight checks the file out at
# job.workflow_sha). At the tag teams-notifier-function-app-vX.Y.Z the manifest says X.Y.Z; on a
# branch, a commit SHA or a pre-release tag it names the last full release. The workflow file
# itself carries no version: release-please runs with GITHUB_TOKEN, which can't write files
# under .github/workflows/.
#
# Environment:
#   RELEASE_PLEASE_MANIFEST  .release-please-manifest.json at the workflow's commit
#   INPUT_TAG                the caller's `tag` input. Empty means this workflow's own release.
#
# Outputs:
#   workflow_version  the version in the manifest's "." package
#   tag               teams-notifier-function-app-v<version>
#   version           the version part of the tag
#   zip               the release's function app ZIP asset name
#   version_check     "true" when INPUT_TAG was empty. The caller then relies on its `uses:` pin
#                     to name the release, so check-requirements.sh holds the checked-in
#                     requirements file to that release too. A caller that passes a tag picks the
#                     version itself and only the hash gate applies.
set -euo pipefail
shopt -s inherit_errexit

# shellcheck source=lib.bash
source "$(dirname -- "${BASH_SOURCE[0]}")/lib.bash"

require_env RELEASE_PLEASE_MANIFEST
[[ -f "${RELEASE_PLEASE_MANIFEST}" ]] ||
  die "${RELEASE_PLEASE_MANIFEST} is missing, so this workflow can't tell which release it belongs to."

# This repository is a single release-please package at the root, so its version is under ".".
workflow_version="$(jq -er '."." | strings' "${RELEASE_PLEASE_MANIFEST}" 2>/dev/null)" || workflow_version=""
if [[ -z "${workflow_version}" ]]; then
  keys="$(jq -r 'if type == "object" then keys | join(", ") else "not a JSON object" end' "${RELEASE_PLEASE_MANIFEST}" 2>/dev/null)" ||
    keys="not JSON"
  die "${RELEASE_PLEASE_MANIFEST} has no version for the \".\" package (found: ${keys:-no keys}), so this workflow can't tell which release it belongs to."
fi
[[ "${workflow_version}" =~ ${SEMVER_RE} ]] ||
  die "${RELEASE_PLEASE_MANIFEST} gives version '${workflow_version}' for the \".\" package; expected X.Y.Z."

tag="$(trim "${INPUT_TAG:-}")"
if [[ -z "${tag}" ]]; then
  tag="${APP_TAG_PREFIX}${workflow_version}"
  version_check="true"
  log "No tag input: deploying this workflow's own release, ${tag}."
else
  version_check="false"
  log "Tag input: deploying ${tag}. Only the hash gate applies; the requirements file's version is not compared with this workflow's (${workflow_version})."
fi

version="${tag#"${APP_TAG_PREFIX}"}"
if [[ "${version}" == "${tag}" ]] || ! [[ "${version}" =~ ${RELEASE_VERSION_RE} ]]; then
  die "'${tag}' is not a release tag of this app. Expected ${APP_TAG_PREFIX}X.Y.Z, or ${APP_TAG_PREFIX}X.Y.Z-pre.N for a pre-release."
fi

set_output workflow_version "${workflow_version}"
set_output tag "${tag}"
set_output version "${version}"
set_output zip "teams-notifier-function-app-v${version}.zip"
set_output version_check "${version_check}"
notice "Deploy target" "tag=${tag} version=${version} (workflow release ${workflow_version})"
