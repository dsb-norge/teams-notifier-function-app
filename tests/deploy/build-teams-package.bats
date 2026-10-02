#!/usr/bin/env bats
# Unit tests for scripts/deploy/build-teams-package.sh. They run the repository's real
# create-teams-app-package.sh, the generator a deploy takes from the release's tag, on the
# repository's own app-requirements.json and generic branding.
# shellcheck disable=SC2030,SC2031,SC2154 # bats runs each test in a subshell and sets output/status

load test_helper

setup() {
  setup_sandbox
  export GENERATOR="${REPO_ROOT}/teams-app-package/create-teams-app-package.sh"
  export RELEASE_REQUIREMENTS_FILE="${REPO_ROOT}/src/TeamsNotificationBot/app-requirements.json"
  # The caller's branding directory: a copy, so the test can't write into the repository.
  export PACKAGE_DIR="${BATS_TEST_TMPDIR}/caller/teams-app-package"
  mkdir -p "${PACKAGE_DIR}"
  cp "${REPO_ROOT}/teams-app-package/"{app-metadata.json,color.png,outline.png} "${PACKAGE_DIR}/"
  export OUTPUT_DIR="${BATS_TEST_TMPDIR}/out"
  export BOT_APP_ID="00000000-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
  export TEAMS_APP_ID="11111111-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
  export INSTANCE_NAME="platform"
  export VERSION="2.1.0"
}

@test "builds the package and names the release teams-app-<instance>-v<version>-<shorthash>" {
  run_script build-teams-package.sh
  assert_success
  manifest="$(output_value manifest)"
  zip_path="$(output_value zip_path)"
  shorthash="$(output_value shorthash)"
  [[ "${shorthash}" =~ ^[0-9a-f]{8}$ ]] || fail "shorthash '${shorthash}'"
  assert_equal "$(output_value release_tag)" "teams-app-platform-v2.1.0-${shorthash}"
  assert_equal "${zip_path}" "${OUTPUT_DIR}/$(jq -r .output_zip "${PACKAGE_DIR}/app-metadata.json")"
  # The instance's identity reached the manifest.
  assert_equal "$(jq -r .id "${manifest}")" "${TEAMS_APP_ID}"
  assert_equal "$(jq -r '.bots[0].botId' "${manifest}")" "${BOT_APP_ID}"
  # The shorthash covers exactly what the ZIP carries.
  unzip -q -d "${BATS_TEST_TMPDIR}/unzipped" "${zip_path}"
  expected="$(cat "${BATS_TEST_TMPDIR}/unzipped/"{manifest.json,color.png,outline.png} | sha256sum | cut -c1-8)"
  assert_equal "${shorthash}" "${expected}"
}

@test "the same input gives the same release name; branding changes it" {
  run_script build-teams-package.sh
  first="$(output_value release_tag)"
  : >"${GITHUB_OUTPUT}"
  run_script build-teams-package.sh
  assert_equal "$(output_value release_tag)" "${first}"

  jq '.accent_color = "#123456"' "${PACKAGE_DIR}/app-metadata.json" >"${BATS_TEST_TMPDIR}/m.json"
  mv "${BATS_TEST_TMPDIR}/m.json" "${PACKAGE_DIR}/app-metadata.json"
  : >"${GITHUB_OUTPUT}"
  run_script build-teams-package.sh
  assert_success
  [[ "$(output_value release_tag)" != "${first}" ]] || fail "branding change kept the name ${first}"
}

@test "typical instance names are accepted" {
  for name in dev prod teams-notifier platform; do
    export INSTANCE_NAME="${name}"
    : >"${GITHUB_OUTPUT}"
    run_script build-teams-package.sh
    assert_success
    [[ "$(output_value release_tag)" == "teams-app-${name}-v2.1.0-"* ]] || fail "${name}: $(output_value release_tag)"
  done
}

@test "an instance name that can't be part of a tag is refused before building" {
  for name in "" "Prod" "dev env" "dev/x" "-dev" "dev-" "dev--x"; do
    export INSTANCE_NAME="${name}"
    run_script build-teams-package.sh
    assert_failure
  done
  [[ ! -d "${OUTPUT_DIR}" ]]
}

@test "a branding directory without its icons is refused" {
  rm "${PACKAGE_DIR}/outline.png"
  run_script build-teams-package.sh
  assert_failure
  assert_output_contains "has no outline.png"
}
