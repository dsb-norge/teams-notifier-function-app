#!/usr/bin/env bash
#
# Builds an instance's Teams app package and names its release.
#
# The manifest comes from the release's create-teams-app-package.sh, fed with:
#   - the release's app-requirements.json (commands, scopes and the other app-owned fields)
#   - the caller's branding: app-metadata.json, color.png and outline.png in PACKAGE_DIR
#   - the instance's identity: BOT_APP_ID (manifest.bots[0].botId), TEAMS_APP_ID (manifest.id)
#
# The release is named teams-app-<instance>-v<version>-<shorthash>, where the shorthash covers
# the three files in the ZIP (see teams_package_shorthash). Identical content gives an identical
# name, which is how publish-teams-package.sh avoids publishing the same package twice.
#
# Environment:
#   GENERATOR                  create-teams-app-package.sh, checked out at the release's tag
#   RELEASE_REQUIREMENTS_FILE  the release's app-requirements.json, already verified
#   PACKAGE_DIR                the caller's teams_app_package_dir
#   OUTPUT_DIR                 where the manifest and the ZIP go
#   BOT_APP_ID, TEAMS_APP_ID   the instance's ids
#   INSTANCE_NAME              the caller's instance_name
#   VERSION                    the release version
#
# Outputs:
#   manifest, zip_path, shorthash, release_tag
set -euo pipefail
shopt -s inherit_errexit

# shellcheck source=lib.bash
source "$(dirname -- "${BASH_SOURCE[0]}")/lib.bash"

require_env GENERATOR RELEASE_REQUIREMENTS_FILE PACKAGE_DIR OUTPUT_DIR BOT_APP_ID TEAMS_APP_ID INSTANCE_NAME VERSION
[[ "${INSTANCE_NAME}" =~ ${INSTANCE_NAME_RE} ]] ||
  die "instance_name '${INSTANCE_NAME}' must be lower-case letters and digits, optionally separated by single hyphens."
[[ -f "${GENERATOR}" ]] || die "Teams package generator ${GENERATOR} does not exist."
for file in app-metadata.json color.png outline.png; do
  [[ -f "${PACKAGE_DIR}/${file}" ]] || die "teams_app_package_dir '${PACKAGE_DIR}' has no ${file}."
done

mkdir -p -- "${OUTPUT_DIR}"
bash "${GENERATOR}" \
  --bot-app-id "${BOT_APP_ID}" \
  --teams-app-id "${TEAMS_APP_ID}" \
  --requirements "${RELEASE_REQUIREMENTS_FILE}" \
  --metadata "${PACKAGE_DIR}/app-metadata.json" \
  --icons-dir "${PACKAGE_DIR}" \
  --output-dir "${OUTPUT_DIR}"

manifest="${OUTPUT_DIR}/manifest.json"
[[ -f "${manifest}" ]] || die "The generator did not write ${manifest}."
output_zip="$(json_string_field "${PACKAGE_DIR}/app-metadata.json" output_zip)" ||
  die "${PACKAGE_DIR}/app-metadata.json has no output_zip."
zip_path="${OUTPUT_DIR}/${output_zip}"
[[ -f "${zip_path}" ]] || die "The generator did not write ${zip_path}."

# The generator copies the icons into a temporary directory to zip them, so hash the caller's
# originals: the bytes are the same.
shorthash="$(teams_package_shorthash "${manifest}" "${PACKAGE_DIR}/color.png" "${PACKAGE_DIR}/outline.png")"
release_tag="teams-app-${INSTANCE_NAME}-v${VERSION}-${shorthash}"

set_output manifest "${manifest}"
set_output zip_path "${zip_path}"
set_output shorthash "${shorthash}"
set_output release_tag "${release_tag}"
notice "Teams app package" "built ${release_tag}"
