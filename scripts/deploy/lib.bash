# shellcheck shell=bash
#
# Shared helpers for the scripts in scripts/deploy/. Each script sources this file.
#
# The scripts run in .github/workflows/reusable-deploy.yml, which checks them out at its own
# commit (job.workflow_sha), and under bats in tests/deploy/. Everything comes in through
# environment variables, so no `${{ }}` expression is ever expanded into shell code.
#
# Logging and annotations go to stdout, where the runner reads workflow commands. Machine output
# goes to $GITHUB_OUTPUT only. Keeping the two apart matters: a function that both logs and
# "returns" through stdout corrupts its own return value.

# shellcheck disable=SC2034 # the constants below are used by the scripts that source this file

# The release-please tag prefix of this app's releases. The deploy contract: tags, asset names
# and the version inside app-requirements.json all derive from it.
readonly APP_TAG_PREFIX="teams-notifier-function-app-v"

# X.Y.Z, as release-please records a release in .release-please-manifest.json.
readonly SEMVER_RE='^[0-9]+\.[0-9]+\.[0-9]+$'

# X.Y.Z or X.Y.Z-<pre-release>, e.g. 1.8.2-pre.7 from prerelease.yml.
readonly RELEASE_VERSION_RE='^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$'

# A caller's instance_name, which becomes part of a git tag in the caller's repository.
readonly INSTANCE_NAME_RE='^[a-z0-9]+(-[a-z0-9]+)*$'

# Escapes a workflow command's message the way @actions/core does, so a value with a newline
# can't end the command early and start another one (`::add-mask::`, `::stop-commands::`).
escape_data() {
  local s="${1}"
  s="${s//'%'/'%25'}"
  s="${s//$'\r'/'%0D'}"
  s="${s//$'\n'/'%0A'}"
  printf '%s' "${s}"
}

# Same, for a command property (title=...), which also can't contain ':' or ','.
escape_property() {
  local s
  s="$(escape_data "${1}")"
  s="${s//':'/'%3A'}"
  s="${s//','/'%2C'}"
  printf '%s' "${s}"
}

log() { printf '%s\n' "${*}"; }
notice() { printf '::notice title=%s::%s\n' "$(escape_property "${1}")" "$(escape_data "${2}")"; }
warning() { printf '::warning title=%s::%s\n' "$(escape_property "${1}")" "$(escape_data "${2}")"; }
error() { printf '::error::%s\n' "$(escape_data "${*}")"; }
die() {
  error "${*}"
  exit 1
}

# Fails unless every named variable is set and non-empty.
require_env() {
  local name
  for name in "${@}"; do
    [[ -n "${!name:-}" ]] || die "${name} is required but empty."
  done
}

# Fails unless the named variable is exactly "true" or "false", which is how a boolean input
# reaches a step's environment.
require_bool() {
  local name="${1}"
  [[ "${!name:-}" == "true" || "${!name:-}" == "false" ]] ||
    die "${name} must be 'true' or 'false', got '${!name:-}'."
}

set_output() {
  local name="${1}" value="${2}"
  [[ "${value}" != *$'\n'* ]] || die "Output ${name} must be a single line."
  printf '%s=%s\n' "${name}" "${value}" >>"${GITHUB_OUTPUT:?GITHUB_OUTPUT is not set}"
}

# Appends lines to the job summary. Outside Actions (local runs) the summary is discarded.
summary() {
  printf '%s\n' "${@}" >>"${GITHUB_STEP_SUMMARY:-/dev/null}"
}

# Strips leading and trailing whitespace.
trim() {
  local s="${1}"
  s="${s#"${s%%[![:space:]]*}"}"
  s="${s%"${s##*[![:space:]]}"}"
  printf '%s' "${s}"
}

# Prints a top-level string field of a JSON file; fails when the file isn't JSON or the field is
# missing, empty or not a string.
json_string_field() {
  local file="${1}" field="${2}"
  jq -er --arg field "${field}" '.[$field] | strings | select(. != "")' "${file}" 2>/dev/null
}

# Fails (exits) unless every entry of a ZIP is a regular file or a directory, under a plain
# relative path. Call it before unpacking a ZIP whose content isn't trusted: a release may be a
# pre-release built from an unreviewed branch, and attestation proves only where a file was
# built. Info-ZIP's unzip recreates symlinks, so a crafted archive could otherwise make a later
# write, or a hash, follow a link out of the directory it was unpacked into.
check_zip_entries() {
  local zip="${1}" listing count i name mode
  local -a entry_types=() entry_names=()
  listing="$(unzip -Z -s "${zip}" 2>/dev/null)" || die "${zip} is not a readable ZIP."
  # Line 2 is "Zip file size: ... number of entries: N"; the N entry lines follow.
  count="$(sed -n '2s/.*number of entries: \([0-9][0-9]*\)$/\1/p' <<<"${listing}")"
  [[ "${count}" =~ ^[0-9]+$ ]] || die "Could not read the number of entries in ${zip}."
  ((count > 0)) || die "${zip} has no entries."
  mapfile -t entry_types < <(sed -n "3,$((count + 2))p" <<<"${listing}" | cut -c1)
  mapfile -t entry_names < <(unzip -Z1 "${zip}")
  # A name with a line break in it would shift the pairing below; refuse rather than guess.
  ((${#entry_types[@]} == count && ${#entry_names[@]} == count)) || die "Could not list the entries of ${zip} one per line."
  for ((i = 0; i < count; i++)); do
    name="${entry_names[i]}"
    mode="${entry_types[i]}"
    case "${mode}" in
      - | d) ;;
      *) die "${zip} has entry '${name}' that is not a regular file or a directory (type ${mode}); refusing to unpack it." ;;
    esac
    if [[ -z "${name}" || "${name}" == /* || "${name}" == *\\* || "/${name}/" == */../* ]]; then
      die "${zip} has entry '${name}' with an absolute, parent or backslash path; refusing to unpack it."
    fi
  done
}

# The content hash in a Teams package release name: the first 8 hex digits of the SHA-256 of
# manifest.json, color.png and outline.png, concatenated in that order. Existing package releases
# were named with this exact formula, so changing it would re-publish every instance's package.
teams_package_shorthash() {
  local manifest="${1}" color="${2}" outline="${3}" digest
  digest="$(cat -- "${manifest}" "${color}" "${outline}" | sha256sum)"
  printf '%s' "${digest:0:8}"
}
