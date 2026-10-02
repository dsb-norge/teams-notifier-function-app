#!/usr/bin/env bats
# Unit tests for scripts/deploy/validate-manifest-schema.sh and its Python pins.
#
# The validation tests install the hash-pinned requirements.txt from PyPI into one venv per file
# run, so they need network access. The schema is a small local one; CI's Validate Requirements
# job runs the same script against the real Teams schema.
# shellcheck disable=SC2016,SC2030,SC2031,SC2154 # literal $schema; bats subshells and output/status

load test_helper

setup() {
  setup_sandbox
  export VENV_DIR="${BATS_FILE_TMPDIR}/venv"
  export SCHEMA_FILE="${BATS_TEST_TMPDIR}/schema.json"
  cat >"${SCHEMA_FILE}" <<'JSON'
{
  "$schema": "http://json-schema.org/draft-04/schema#",
  "type": "object",
  "required": ["id", "bots"],
  "properties": {
    "id": {"type": "string"},
    "functions": {"type": "array", "items": {"type": "string", "pattern": "^[\\p{L}][\\p{L}0-9._]*$"}},
    "bots": {"type": "array", "items": {"type": "object", "properties": {"botId": {"type": "string"}}}}
  },
  "additionalProperties": false
}
JSON
  export MANIFEST="${BATS_TEST_TMPDIR}/manifest.json"
  echo '{"id": "11111111-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "bots": [{"botId": "x"}]}' >"${MANIFEST}"
}

@test "a valid manifest passes, though the schema has an ECMAScript-only pattern" {
  # The real Teams schema writes some patterns as \p{L}, which Python's re can't compile, on
  # properties the generated manifest doesn't use. check_schema must not compile them (it does by
  # default since jsonschema 4.17), or every real validation fails.
  run_script validate-manifest-schema.sh
  assert_success
  assert_output_contains "::notice title=Manifest validation::schema-valid"
}

@test "each schema violation is reported with its path" {
  echo '{"id": "x", "bots": [{"botId": 5}], "packageName": "x"}' >"${MANIFEST}"
  run_script validate-manifest-schema.sh
  assert_failure
  assert_output_contains "::error::Teams manifest schema validation failed (2 error(s)):"
  assert_output_contains "::error::  at bots.0.botId: 5 is not of type 'string'"
  assert_output_contains "::error::  at <root>: Additional properties are not allowed ('packageName' was unexpected)"
}

@test "without a local schema, a manifest with no \$schema fails before any download" {
  unset SCHEMA_FILE
  stub curl <<<'exit 64'
  run_script validate-manifest-schema.sh
  assert_failure
  assert_output_contains "has no \$schema field"
  refute_calls_contain "curl"
}

@test "a non-https \$schema is refused" {
  unset SCHEMA_FILE
  stub curl <<<'exit 64'
  echo '{"$schema": "http://example.com/schema.json"}' >"${MANIFEST}"
  run_script validate-manifest-schema.sh
  assert_failure
  assert_output_contains "is not an https URL"
  refute_calls_contain "curl"
}

@test "requirements.txt pins every package to one version and at least one hash" {
  local file="${DEPLOY_SCRIPTS}/requirements.txt" names pins
  names="$(grep -cE '^[A-Za-z0-9._-]+==' "${file}")"
  pins="$(grep -cE '^[A-Za-z0-9._-]+==[0-9][^ ]* \\$' "${file}")"
  ((names > 0)) || fail "no requirements in ${file}"
  assert_equal "${pins}" "${names}"
  # pip-compile writes the hashes on the lines after each requirement; --require-hashes would
  # refuse a requirement without one, but fail in a deploy rather than here.
  awk '/^[A-Za-z0-9._-]+==/ { if (name && !hashed) exit 1; name = $1; hashed = 0 }
       /--hash=sha256:[0-9a-f]{64}/ { hashed = 1 }
       END { if (name && !hashed) exit 1 }' "${file}" || fail "a requirement has no hash"
  # No ranges anywhere, and the header still names the command Dependabot re-runs.
  if grep -E '^[A-Za-z0-9._-]+(>|<|~|!)' "${file}"; then fail "unpinned requirement"; fi
  assert_file_contains "${file}" "pip-compile --allow-unsafe --generate-hashes --strip-extras requirements.in"
}

@test "requirements.in pins the validator exactly" {
  run grep -E '^[A-Za-z]' "${DEPLOY_SCRIPTS}/requirements.in"
  [[ "${output}" =~ ^jsonschema==[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "requirements.in: ${output}"
}

@test "requirements.txt was compiled from requirements.in's pins" {
  # A pin changed in requirements.in without re-running pip-compile would leave deploys
  # installing the old locked version. Every pin in the .in must be the same line in the .txt.
  local pin found=0
  while IFS= read -r pin; do
    found=$((found + 1))
    grep -qE "^${pin//./\\.} \\\\$" "${DEPLOY_SCRIPTS}/requirements.txt" ||
      fail "requirements.in pins ${pin}, but requirements.txt doesn't: re-run pip-compile (docs/contributing.md section 9)."
  done < <(grep -E '^[A-Za-z0-9._-]+==' "${DEPLOY_SCRIPTS}/requirements.in")
  ((found > 0)) || fail "requirements.in pins nothing"
}
