#!/usr/bin/env bats
# Unit tests for scripts/deploy/publish-teams-package.sh, against a gh stub for
# `gh release view` and `gh release create`.
# shellcheck disable=SC2030,SC2031,SC2154 # bats runs each test in a subshell and sets output/status

load test_helper

setup() {
  setup_sandbox
  export REPO="example-org/caller-repo"
  export INSTANCE_NAME="platform"
  export VERSION="2.1.0"
  export TAG="teams-notifier-function-app-v2.1.0"
  export BOT_APP_ID="00000000-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
  # The downloaded artifact: one package ZIP, as create-teams-app-package.sh builds it.
  export PACKAGE_DIR="${BATS_TEST_TMPDIR}/artifact"
  mkdir -p "${PACKAGE_DIR}" "${BATS_TEST_TMPDIR}/content"
  printf '{"id":"x"}' >"${BATS_TEST_TMPDIR}/content/manifest.json"
  printf 'color' >"${BATS_TEST_TMPDIR}/content/color.png"
  printf 'outline' >"${BATS_TEST_TMPDIR}/content/outline.png"
  (cd "${BATS_TEST_TMPDIR}/content" && zip -q "${PACKAGE_DIR}/teams-app-package.zip" manifest.json color.png outline.png)
  export ZIP_PATH="${PACKAGE_DIR}/teams-app-package.zip"
  SHORTHASH="$(printf '{"id":"x"}coloroutline' | sha256sum | cut -c1-8)"
  export SHORTHASH
  export RELEASE_TAG="teams-app-platform-v2.1.0-${SHORTHASH}"
  # GH_VIEW: "exists", "missing" (gh's own message) or "error". GH_VIEW_AFTER_CREATE, when
  # set, is what a view answers once a create has been attempted. GH_CREATE: "ok" or "fail".
  export GH_VIEW="missing"
  export GH_VIEW_AFTER_CREATE=""
  export GH_CREATE="ok"
  export GH_NOTES="${BATS_TEST_TMPDIR}/notes.md"
  stub gh <<'EOF'
case "$1 $2" in
  "release view")
    view="${GH_VIEW}"
    if [[ -n "${GH_VIEW_AFTER_CREATE}" ]] && grep -q '^gh release create' "${STUB_CALLS}"; then
      view="${GH_VIEW_AFTER_CREATE}"
    fi
    case "${view}" in
      exists) echo '{"tagName":"x"}' ;;
      missing) echo "release not found" >&2; exit 1 ;;
      *) echo "HTTP 502: Bad Gateway" >&2; exit 1 ;;
    esac
    ;;
  "release create")
    [[ "${GH_CREATE}" == "ok" ]] || { echo "HTTP 422: Validation Failed (tag_name already_exists)" >&2; exit 1; }
    while (($#)); do
      if [[ "$1" == "--notes" ]]; then printf '%s\n' "$2" >"${GH_NOTES}"; fi
      shift
    done
    ;;
  *) echo "gh stub: unhandled: $*" >&2; exit 64 ;;
esac
EOF
}

@test "a new package is released in the caller's repository" {
  run_script publish-teams-package.sh
  assert_success
  assert_equal "$(output_value published)" "true"
  # The name comes from the ZIP's own content, not from anything the build job said.
  assert_equal "$(output_value release_tag)" "${RELEASE_TAG}"
  assert_calls_contain "gh release view ${RELEASE_TAG} --repo example-org/caller-repo"
  assert_calls_contain "gh release create ${RELEASE_TAG} ${ZIP_PATH} --repo example-org/caller-repo --title Teams app manifest — platform v2.1.0"
  assert_file_contains "${GH_NOTES}" "**Content shorthash:** \`${SHORTHASH}\`"
  assert_file_contains "${GH_NOTES}" "(app release \`teams-notifier-function-app-v2.1.0\`)"
  assert_file_contains "${GITHUB_STEP_SUMMARY}" "**Status:** published"
}

@test "the same content is never released twice" {
  export GH_VIEW="exists"
  run_script publish-teams-package.sh
  assert_success
  assert_equal "$(output_value published)" "false"
  refute_calls_contain "gh release create"
  assert_file_contains "${GITHUB_STEP_SUMMARY}" "already existed"
}

@test "a failed lookup fails, instead of trying to create" {
  export GH_VIEW="error"
  run_script publish-teams-package.sh
  assert_failure
  assert_output_contains "Could not check whether ${RELEASE_TAG} exists"
  refute_calls_contain "gh release create"
}

@test "losing a race to a concurrent run counts as already published" {
  export GH_CREATE="fail"
  export GH_VIEW_AFTER_CREATE="exists"
  run_script publish-teams-package.sh
  assert_success
  assert_equal "$(output_value published)" "false"
  assert_output_contains "was published by a concurrent run"
}

@test "a failed create with no release behind it fails" {
  export GH_CREATE="fail"
  run_script publish-teams-package.sh
  assert_failure
  assert_output_contains "Could not create release ${RELEASE_TAG}"
  assert_equal "$(output_value published)" ""
}

@test "the artifact must hold exactly one ZIP" {
  rm "${ZIP_PATH}"
  run_script publish-teams-package.sh
  assert_failure
  assert_output_contains "must hold exactly one package ZIP; it holds 0"
  cp "${BATS_TEST_TMPDIR}/content/color.png" "${PACKAGE_DIR}/a.zip"
  cp "${BATS_TEST_TMPDIR}/content/color.png" "${PACKAGE_DIR}/b.zip"
  run_script publish-teams-package.sh
  assert_failure
  assert_output_contains "it holds 2"
  refute_calls_contain "gh"
}

@test "a ZIP with anything but the three package files is refused" {
  (cd "${BATS_TEST_TMPDIR}/content" && echo x >extra.sh && zip -q "${ZIP_PATH}" extra.sh)
  run_script publish-teams-package.sh
  assert_failure
  assert_output_contains "must contain exactly manifest.json, color.png and outline.png; it contains: color.png, extra.sh, manifest.json, outline.png"
  refute_calls_contain "gh"
}

@test "a ZIP that tries to write outside its directory is refused" {
  rm "${ZIP_PATH}"
  mkdir -p "${BATS_TEST_TMPDIR}/deep/a"
  (cd "${BATS_TEST_TMPDIR}/deep/a" && cp "${BATS_TEST_TMPDIR}/content/"* . && zip -q "${ZIP_PATH}" ../../content/manifest.json color.png outline.png)
  run_script publish-teams-package.sh
  assert_failure
  assert_output_contains "has entry '../../content/manifest.json' with an absolute, parent or backslash path"
  refute_calls_contain "gh"
}

@test "a ZIP whose package files are symlinks is refused" {
  # The right three names, but links: the hash would follow their targets, and a non-file
  # package would be published.
  rm "${ZIP_PATH}"
  make_zip "${ZIP_PATH}" manifest.json:link color.png:link outline.png:link
  run_script publish-teams-package.sh
  assert_failure
  assert_output_contains "that is not a regular file or a directory (type l)"
  refute_calls_contain "gh"
}

@test "an instance name that can't be part of a tag is refused" {
  export INSTANCE_NAME="Prod/x"
  run_script publish-teams-package.sh
  assert_failure
  refute_calls_contain "gh"
}
