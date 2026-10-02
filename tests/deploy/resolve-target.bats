#!/usr/bin/env bats
# Unit tests for scripts/deploy/resolve-target.sh: the workflow's own version, read from
# .release-please-manifest.json, and the target tag.
# shellcheck disable=SC2030,SC2031,SC2154 # bats runs each test in a subshell and sets output/status

load test_helper

setup() {
  setup_sandbox
  export RELEASE_PLEASE_MANIFEST="${BATS_TEST_TMPDIR}/tooling/.release-please-manifest.json"
  mkdir -p "$(dirname "${RELEASE_PLEASE_MANIFEST}")"
  manifest '{".": "2.1.0"}'
  export INPUT_TAG=""
}

manifest() {
  printf '%s\n' "${1}" >"${RELEASE_PLEASE_MANIFEST}"
}

# --- the workflow's own version ----------------------------------------------------------------

@test "no tag: deploys the release the manifest names, and turns the version check on" {
  run_script resolve-target.sh
  assert_success
  assert_equal "$(output_value workflow_version)" "2.1.0"
  assert_equal "$(output_value tag)" "teams-notifier-function-app-v2.1.0"
  assert_equal "$(output_value version)" "2.1.0"
  assert_equal "$(output_value zip)" "teams-notifier-function-app-v2.1.0.zip"
  assert_equal "$(output_value version_check)" "true"
}

@test "the repository's own manifest is one the script reads" {
  # The file preflight checks out at the workflow's commit, in the shape release-please writes.
  export RELEASE_PLEASE_MANIFEST="${REPO_ROOT}/.release-please-manifest.json"
  run_script resolve-target.sh
  assert_success
  assert_equal "$(output_value workflow_version)" "$(jq -r '."."' "${RELEASE_PLEASE_MANIFEST}")"
}

@test "a missing manifest fails before any output" {
  rm "${RELEASE_PLEASE_MANIFEST}"
  run_script resolve-target.sh
  assert_failure
  assert_output_contains "${RELEASE_PLEASE_MANIFEST} is missing"
  assert_equal "$(cat "${GITHUB_OUTPUT}")" ""
}

@test "an empty version fails" {
  for content in '{".": ""}' '{".": null}' '{}' ''; do
    manifest "${content}"
    run_script resolve-target.sh
    assert_failure
    assert_output_contains "has no version for the \".\" package"
    assert_equal "$(cat "${GITHUB_OUTPUT}")" ""
  done
}

@test "a malformed manifest or version fails" {
  for content in 'not json' '["2.1.0"]' '{".": 2.1}' '{".": "2.1"}' '{".": "v2.1.0"}' '{".": "2.1.0-pre.1"}' '{".": " 2.1.0"}'; do
    manifest "${content}"
    run_script resolve-target.sh
    assert_failure
    assert_equal "$(cat "${GITHUB_OUTPUT}")" ""
  done
  manifest '{".": "2.1"}'
  run_script resolve-target.sh
  assert_output_contains "gives version '2.1' for the \".\" package; expected X.Y.Z."
}

@test "a manifest whose package isn't at the root fails, naming the keys it found" {
  # This app is one release-please package at ".". A manifest keyed otherwise means the release
  # layout changed, and the tag and asset names this workflow derives may no longer hold.
  manifest '{"src/TeamsNotificationBot": "2.1.0"}'
  run_script resolve-target.sh
  assert_failure
  assert_output_contains "has no version for the \".\" package (found: src/TeamsNotificationBot)"
}

@test "a tag input doesn't make the manifest optional" {
  rm "${RELEASE_PLEASE_MANIFEST}"
  export INPUT_TAG="teams-notifier-function-app-v1.8.1"
  run_script resolve-target.sh
  assert_failure
}

# --- the target tag -----------------------------------------------------------------------------

@test "a tag: deploys that release and leaves only the hash gate" {
  export INPUT_TAG="teams-notifier-function-app-v1.8.1"
  run_script resolve-target.sh
  assert_success
  assert_equal "$(output_value workflow_version)" "2.1.0"
  assert_equal "$(output_value tag)" "teams-notifier-function-app-v1.8.1"
  assert_equal "$(output_value version)" "1.8.1"
  assert_equal "$(output_value zip)" "teams-notifier-function-app-v1.8.1.zip"
  assert_equal "$(output_value version_check)" "false"
}

@test "a pre-release tag resolves to its full version" {
  export INPUT_TAG="teams-notifier-function-app-v1.8.2-pre.7"
  run_script resolve-target.sh
  assert_success
  assert_equal "$(output_value version)" "1.8.2-pre.7"
  assert_equal "$(output_value zip)" "teams-notifier-function-app-v1.8.2-pre.7.zip"
}

@test "whitespace around a typed tag is ignored" {
  export INPUT_TAG=$' teams-notifier-function-app-v1.8.1\n'
  run_script resolve-target.sh
  assert_success
  assert_equal "$(output_value tag)" "teams-notifier-function-app-v1.8.1"
}

@test "anything that isn't this app's release tag fails before any output" {
  for tag in v1.8.1 1.8.1 teams-notifier-function-app-1.8.1 teams-notifier-function-app-v1.8 \
    teams-notifier-function-app-v1.8.1/../x other-app-v1.8.1 "teams-notifier-function-app-v1.8.1 extra"; do
    export INPUT_TAG="${tag}"
    : >"${GITHUB_OUTPUT}"
    run_script resolve-target.sh
    assert_failure
    assert_output_contains "is not a release tag of this app"
    assert_equal "$(cat "${GITHUB_OUTPUT}")" ""
  done
}
