#!/usr/bin/env bash
#
# Publishes the Teams app package as a release in the caller's repository, unless a release with
# the same name, and therefore the same content, already exists.
#
# Runs in the publish-package job, which has contents: write on the caller's repository and so
# runs no code from the target release: the package was built by the release's generator in the
# read-only package-manifest job, and only the ZIP crosses over, as an artifact. Nothing that job
# says about the ZIP is trusted either. The release name is worked out here, from the ZIP's own
# manifest.json, color.png and outline.png, with the same formula build-teams-package.sh logs.
#
# A Teams Administrator uploads the attached ZIP in Teams Admin Center. A new release appears
# only when the package content changes, so no new release means no Teams action is needed.
#
# The job runs on every caller run that deploys or finds the target version already running,
# so two runs can race to create the same release. The loser's create fails; if the release
# exists by then, that is the same outcome as finding it, not an error.
#
# Environment:
#   REPO           the caller's owner/repo
#   PACKAGE_DIR    the downloaded artifact: exactly one ZIP
#   INSTANCE_NAME  the caller's instance_name
#   VERSION, TAG   the app release
#   BOT_APP_ID     the bot's Entra app id, quoted in the release notes
#   GH_TOKEN       a token that can create releases in REPO
#
# Outputs:
#   release_tag    teams-app-<instance_name>-v<version>-<shorthash>
#   published      "true" when a release was created, "false" when it already existed
set -euo pipefail
shopt -s inherit_errexit

# shellcheck source=lib.bash
source "$(dirname -- "${BASH_SOURCE[0]}")/lib.bash"

require_env REPO PACKAGE_DIR INSTANCE_NAME VERSION TAG BOT_APP_ID
[[ "${INSTANCE_NAME}" =~ ${INSTANCE_NAME_RE} ]] ||
  die "instance_name '${INSTANCE_NAME}' must be lower-case letters and digits, optionally separated by single hyphens."
[[ "${VERSION}" =~ ${RELEASE_VERSION_RE} ]] || die "VERSION '${VERSION}' is not a release version."

shopt -s nullglob
zips=("${PACKAGE_DIR}"/*.zip)
shopt -u nullglob
((${#zips[@]} == 1)) || die "${PACKAGE_DIR} must hold exactly one package ZIP; it holds ${#zips[@]}."
ZIP_PATH="${zips[0]}"

work="$(mktemp -d)"
err="${work}/gh-error"
trap 'rm -rf -- "${work}"' EXIT

# A Teams package is exactly these three regular files. A symlink would make the hash follow
# its target and get a non-file published, so check every entry's type, not just the names,
# then extract only those three, by name.
check_zip_entries "${ZIP_PATH}"
entries="$(unzip -Z1 -- "${ZIP_PATH}" | LC_ALL=C sort)" || die "${ZIP_PATH} is not a readable ZIP."
[[ "${entries}" == $'color.png\nmanifest.json\noutline.png' ]] ||
  die "${ZIP_PATH} must contain exactly manifest.json, color.png and outline.png; it contains: ${entries//$'\n'/, }"
unzip -q -- "${ZIP_PATH}" manifest.json color.png outline.png -d "${work}/content"
for file in manifest.json color.png outline.png; do
  [[ -f "${work}/content/${file}" && ! -L "${work}/content/${file}" ]] ||
    die "${file} from ${ZIP_PATH} did not unpack as a regular file."
done

SHORTHASH="$(teams_package_shorthash "${work}/content/manifest.json" "${work}/content/color.png" "${work}/content/outline.png")"
RELEASE_TAG="teams-app-${INSTANCE_NAME}-v${VERSION}-${SHORTHASH}"
set_output release_tag "${RELEASE_TAG}"
log "Package ${ZIP_PATH##*/} is ${RELEASE_TAG}."

if gh release view "${RELEASE_TAG}" --repo "${REPO}" --json tagName >/dev/null 2>"${err}"; then
  notice "Teams app package" "${RELEASE_TAG} already exists in ${REPO}: not published again."
  set_output published false
  summary "## Teams app package" "" "- **Release:** \`${RELEASE_TAG}\`" "- **Status:** already existed, nothing published"
  exit 0
elif ! grep -qi 'release not found' "${err}"; then
  # Only a definite "not found" may lead to a create; anything else is an error to look at.
  die "Could not check whether ${RELEASE_TAG} exists in ${REPO}: $(head -c 500 -- "${err}")"
fi

zip_name="$(basename -- "${ZIP_PATH}")"
notes="$(
  cat <<EOF
## Teams app manifest: ${INSTANCE_NAME}, app v${VERSION}

**Attached ZIP:** \`${zip_name}\`
**Content shorthash:** \`${SHORTHASH}\`

### To install or update in Teams

1. Open [Teams Admin Center, Manage apps](https://admin.teams.microsoft.com/policies/manage-apps).
2. Search for the app. If it exists, select **Update**; otherwise **Upload new app**.
3. Confirm its status is **Allowed**. If it is blocked, check the tenant-wide app permission policy.

### Catalog references

- **Teams app id:** \`id\` in the bundled \`manifest.json\`
- **Bot app registration:** \`${BOT_APP_ID}\`
- **App version:** \`${VERSION}\` (app release \`${TAG}\`)

### Why a new release?

A release is published only when the package content changes: a different app version, a
branding change, or a different instance. Installs of an earlier release with the same
shorthash need no update.
EOF
)"

if ! gh release create "${RELEASE_TAG}" "${ZIP_PATH}" \
  --repo "${REPO}" \
  --title "Teams app manifest — ${INSTANCE_NAME} v${VERSION}" \
  --notes "${notes}"; then
  if gh release view "${RELEASE_TAG}" --repo "${REPO}" --json tagName >/dev/null 2>&1; then
    notice "Teams app package" "${RELEASE_TAG} was published by a concurrent run: not published again."
    set_output published false
    summary "## Teams app package" "" "- **Release:** \`${RELEASE_TAG}\`" "- **Status:** published by a concurrent run"
    exit 0
  fi
  die "Could not create release ${RELEASE_TAG} in ${REPO}."
fi

notice "Teams app package" "published ${RELEASE_TAG}"
set_output published true
summary "## Teams app package" "" "- **Release:** \`${RELEASE_TAG}\`" "- **Status:** published" \
  "- **Link:** ${GITHUB_SERVER_URL:-https://github.com}/${REPO}/releases/tag/${RELEASE_TAG}"
