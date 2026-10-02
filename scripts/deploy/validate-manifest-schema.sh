#!/usr/bin/env bash
#
# Validates a Teams app manifest against the schema it declares in its own $schema field.
#
# The schema URL is read from the manifest rather than pinned, so a schema bump in
# create-teams-app-package.sh is validated against whatever it now declares. The validator,
# Python's jsonschema, is pinned with its whole dependency tree, by version and hash, in
# requirements.txt next to this script, and installed wheels-only into a throwaway venv.
#
# Used by the package-manifest job of reusable-deploy.yml, and by CI's Validate Requirements job
# on a sample manifest, so CI runs the same pinned validator a deploy does.
#
# Environment:
#   MANIFEST     the manifest.json to validate
#   SCHEMA_FILE  a local schema to use instead of downloading $schema (tests)
#   VENV_DIR     where to create the venv (default: a temporary directory)
set -euo pipefail
shopt -s inherit_errexit

here="$(dirname -- "${BASH_SOURCE[0]}")"
# shellcheck source=lib.bash
source "${here}/lib.bash"

require_env MANIFEST
# shellcheck disable=SC2153 # MANIFEST comes from the environment
[[ -f "${MANIFEST}" ]] || die "Manifest ${MANIFEST} does not exist."

work="$(mktemp -d)"
trap 'rm -rf -- "${work}"' EXIT
venv="${VENV_DIR:-${work}/venv}"

schema="${SCHEMA_FILE:-}"
if [[ -z "${schema}" ]]; then
  # shellcheck disable=SC2016 # the field is literally named $schema
  schema_url="$(json_string_field "${MANIFEST}" '$schema')" ||
    die "${MANIFEST} has no \$schema field, so there is nothing to validate against."
  [[ "${schema_url}" == https://* ]] || die "The manifest's \$schema '${schema_url}' is not an https URL."
  schema="${work}/schema.json"
  curl -fsSL --max-time 15 --retry 3 --retry-delay 2 -o "${schema}" "${schema_url}"
fi

# --require-hashes also makes pip refuse any dependency that requirements.txt doesn't pin.
# --only-binary: a wheel installs without running code; an sdist would run its build backend.
python3 -m venv -- "${venv}"
"${venv}/bin/python" -m pip install --quiet --disable-pip-version-check --no-input \
  --require-hashes --only-binary :all: -r "${here}/requirements.txt"

"${venv}/bin/python" "${here}/validate-manifest-schema.py" "${schema}" "${MANIFEST}"
