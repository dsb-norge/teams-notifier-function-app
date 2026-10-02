#!/usr/bin/env bash
#
# The two preflight gates on the caller's checked-in app-requirements.json.
#
# Version check (only when VERSION_CHECK is "true", i.e. the caller passed no tag): the file's
# notifier_application_version must equal WORKFLOW_VERSION, the release this workflow belongs
# to. A pinned caller moves its `uses:` ref and replaces the file in the same upgrade PR; this
# catches a PR that did only one of the two.
#
# Hash gate (always): the file's infrastructure_requirements_unique_hash must equal the target
# release's. The file feeds the caller's Terraform, so a different hash means the provisioned
# infrastructure doesn't match what this release needs.
#
# Both are checked before failing, so one run reports everything that is wrong.
#
# Environment:
#   REQUIREMENTS_FILE          the caller's checked-in file (path in the caller's checkout)
#   RELEASE_REQUIREMENTS_FILE  the target release's app-requirements.json, already verified
#   TAG                        the target release tag
#   WORKFLOW_VERSION           this workflow's own release version, from resolve-target.sh
#   VERSION_CHECK              "true" or "false", from resolve-target.sh
#   UPGRADE_DOC_URL            linked from the job summary when a gate fails
set -euo pipefail
shopt -s inherit_errexit

# shellcheck source=lib.bash
source "$(dirname -- "${BASH_SOURCE[0]}")/lib.bash"

require_env REQUIREMENTS_FILE RELEASE_REQUIREMENTS_FILE TAG WORKFLOW_VERSION UPGRADE_DOC_URL
require_bool VERSION_CHECK

[[ -f "${REQUIREMENTS_FILE}" ]] ||
  die "app_requirements_file '${REQUIREMENTS_FILE}' does not exist in the caller's checkout."

checked_version="$(json_string_field "${REQUIREMENTS_FILE}" notifier_application_version)" ||
  die "${REQUIREMENTS_FILE} has no notifier_application_version. Is it an app-requirements.json?"
checked_hash="$(json_string_field "${REQUIREMENTS_FILE}" infrastructure_requirements_unique_hash)" ||
  die "${REQUIREMENTS_FILE} has no infrastructure_requirements_unique_hash. Is it an app-requirements.json?"
release_hash="$(json_string_field "${RELEASE_REQUIREMENTS_FILE}" infrastructure_requirements_unique_hash)" ||
  die "The app-requirements.json of ${TAG} has no infrastructure_requirements_unique_hash."

failed=0

if [[ "${VERSION_CHECK}" == "true" ]]; then
  log "Version check: workflow release ${WORKFLOW_VERSION}, checked-in file ${checked_version}."
  if [[ "${checked_version}" != "${WORKFLOW_VERSION}" ]]; then
    failed=1
    error "Version mismatch: this workflow deploys ${WORKFLOW_VERSION}, but ${REQUIREMENTS_FILE} is from ${checked_version}. Move the uses: ref and replace the file in the same change."
    summary \
      "### :x: App version mismatch" \
      "" \
      "With no \`tag\` input this workflow deploys its own release, \`${TAG}\`. The checked-in \`${REQUIREMENTS_FILE}\` comes from release \`${checked_version}\`." \
      "" \
      "The workflow's \`uses:\` ref and the requirements file must come from the same release. Either the ref was moved without replacing the file, or the file was replaced without moving the ref." \
      "" \
      "**Fix:** point both at the same release, and replace the file **verbatim** with that release's \`app-requirements.json\`. See [the upgrade procedure](${UPGRADE_DOC_URL})." \
      ""
  fi
else
  log "Version check: skipped, the caller chose the tag."
fi

log "Hash gate: checked-in ${checked_hash}, release ${TAG} ${release_hash}."
if [[ "${checked_hash}" != "${release_hash}" ]]; then
  failed=1
  error "Infrastructure hash mismatch: ${REQUIREMENTS_FILE} has ${checked_hash}, ${TAG} needs ${release_hash}. Replace the file with the release's app-requirements.json and apply Terraform before deploying."
  summary \
    "### :x: Infrastructure hash mismatch" \
    "" \
    "The checked-in \`${REQUIREMENTS_FILE}\` has a different \`infrastructure_requirements_unique_hash\` than release \`${TAG}\`, so the provisioned infrastructure does not match what this release needs." \
    "" \
    "- Checked in: \`${checked_hash}\`" \
    "- Release \`${TAG}\`: \`${release_hash}\`" \
    "" \
    "**Fix:** replace the file with the release's \`app-requirements.json\` and apply the infrastructure change before deploying. See [the upgrade procedure](${UPGRADE_DOC_URL})." \
    ""
fi

if ((failed)); then
  exit 1
fi
notice "Preflight gates" "${REQUIREMENTS_FILE} matches ${TAG} (hash ${checked_hash})."
