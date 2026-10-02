#!/usr/bin/env bats
# Unit tests for scripts/deploy/check-requirements.sh: the version check and the infrastructure
# hash gate.
# shellcheck disable=SC2030,SC2031,SC2154 # bats runs each test in a subshell and sets output/status

load test_helper

setup() {
  setup_sandbox
  export REQUIREMENTS_FILE="${BATS_TEST_TMPDIR}/caller/app-requirements.teams-notifier.json"
  export RELEASE_REQUIREMENTS_FILE="${BATS_TEST_TMPDIR}/release/app-requirements.json"
  export TAG="teams-notifier-function-app-v2.1.0"
  export WORKFLOW_VERSION="2.1.0"
  export VERSION_CHECK="true"
  export UPGRADE_DOC_URL="https://example.com/upgrade"
  mkdir -p "$(dirname "${REQUIREMENTS_FILE}")" "$(dirname "${RELEASE_REQUIREMENTS_FILE}")"
  requirements "${REQUIREMENTS_FILE}" 2.1.0 aaaaaaaaaaaa
  requirements "${RELEASE_REQUIREMENTS_FILE}" 2.1.0 aaaaaaaaaaaa
}

# requirements <file> <notifier_application_version> <infrastructure_requirements_unique_hash>
requirements() {
  jq -n --arg v "${2}" --arg h "${3}" \
    '{notifier_application_version: $v, infrastructure_requirements_unique_hash: $h, queues: []}' >"${1}"
}

@test "passes when the version and the hash both match" {
  run_script check-requirements.sh
  assert_success
  assert_output_contains "::notice title=Preflight gates::"
  assert_equal "$(cat "${GITHUB_STEP_SUMMARY}")" ""
}

@test "version check: a file from another release fails, and the summary says how to fix it" {
  requirements "${REQUIREMENTS_FILE}" 2.0.3 aaaaaaaaaaaa
  run_script check-requirements.sh
  assert_failure
  assert_output_contains "::error::Version mismatch: this workflow deploys 2.1.0, but ${REQUIREMENTS_FILE} is from 2.0.3."
  assert_file_contains "${GITHUB_STEP_SUMMARY}" "App version mismatch"
  assert_file_contains "${GITHUB_STEP_SUMMARY}" "(https://example.com/upgrade)"
}

@test "version check: skipped when the caller chose the tag" {
  export VERSION_CHECK="false"
  requirements "${REQUIREMENTS_FILE}" 1.9.0 aaaaaaaaaaaa
  run_script check-requirements.sh
  assert_success
  assert_output_contains "Version check: skipped"
}

@test "version check: a pre-release whose hash matches deploys by tag" {
  # prerelease.yml stamps its version into the release's file, but the hash excludes the version.
  export VERSION_CHECK="false"
  export TAG="teams-notifier-function-app-v2.1.1-pre.4"
  requirements "${RELEASE_REQUIREMENTS_FILE}" 2.1.1-pre.4 aaaaaaaaaaaa
  run_script check-requirements.sh
  assert_success
}

@test "hash gate: a different hash fails, also when the caller chose the tag" {
  for check in true false; do
    export VERSION_CHECK="${check}"
    requirements "${RELEASE_REQUIREMENTS_FILE}" 2.1.0 bbbbbbbbbbbb
    : >"${GITHUB_STEP_SUMMARY}"
    run_script check-requirements.sh
    assert_failure
    assert_output_contains "::error::Infrastructure hash mismatch: ${REQUIREMENTS_FILE} has aaaaaaaaaaaa, ${TAG} needs bbbbbbbbbbbb."
    assert_file_contains "${GITHUB_STEP_SUMMARY}" "Infrastructure hash mismatch"
    assert_file_contains "${GITHUB_STEP_SUMMARY}" "- Release \`${TAG}\`: \`bbbbbbbbbbbb\`"
  done
}

@test "both gates are reported in one run" {
  requirements "${REQUIREMENTS_FILE}" 2.0.3 aaaaaaaaaaaa
  requirements "${RELEASE_REQUIREMENTS_FILE}" 2.1.0 bbbbbbbbbbbb
  run_script check-requirements.sh
  assert_failure
  assert_output_contains "Version mismatch"
  assert_output_contains "Infrastructure hash mismatch"
}

@test "a missing checked-in file names the input" {
  rm "${REQUIREMENTS_FILE}"
  run_script check-requirements.sh
  assert_failure
  assert_output_contains "app_requirements_file '${REQUIREMENTS_FILE}' does not exist"
}

@test "a file without the gate fields is rejected, not compared as empty" {
  echo '{}' >"${REQUIREMENTS_FILE}"
  echo '{}' >"${RELEASE_REQUIREMENTS_FILE}"
  run_script check-requirements.sh
  assert_failure
  assert_output_contains "has no notifier_application_version"
}

@test "a release file without a hash is rejected" {
  echo '{"notifier_application_version": "2.1.0"}' >"${RELEASE_REQUIREMENTS_FILE}"
  run_script check-requirements.sh
  assert_failure
  assert_output_contains "The app-requirements.json of ${TAG} has no infrastructure_requirements_unique_hash."
}
