#!/usr/bin/env bats
# Unit tests for scripts/deploy/download-asset-by-digest.sh, against a gh stub for
# `gh release download` that serves a per-test file.
# shellcheck disable=SC2030,SC2031,SC2154 # bats runs each test in a subshell and sets output/status

load test_helper

setup() {
  setup_sandbox
  export APP_REPO="example-org/teams-notifier-function-app"
  export TAG="teams-notifier-function-app-v2.1.0"
  export ASSET="teams-notifier-function-app-v2.1.0.zip"
  export DEST_DIR="${BATS_TEST_TMPDIR}/release"
  # What the release serves now, and the digest preflight verified.
  export SERVED="${BATS_TEST_TMPDIR}/served"
  echo "the verified build" >"${SERVED}"
  EXPECTED_SHA256="$(sha256sum "${SERVED}" | cut -d' ' -f1)"
  export EXPECTED_SHA256
  stub gh <<'EOF'
[[ "$1 $2" == "release download" ]] || { echo "gh stub: unhandled: $*" >&2; exit 64; }
dir=""; pattern=""
shift 2
while (($#)); do
  case "$1" in
    --dir) dir="$2"; shift 2 ;;
    --pattern) pattern="$2"; shift 2 ;;
    *) shift ;;
  esac
done
cp "${SERVED}" "${dir}/${pattern}"
EOF
}

@test "the file preflight verified is downloaded and its path output" {
  run_script download-asset-by-digest.sh
  assert_success
  assert_equal "$(output_value path)" "${DEST_DIR}/${ASSET}"
  assert_calls_contain "gh release download ${TAG} --repo ${APP_REPO} --pattern ${ASSET} --dir ${DEST_DIR}"
  refute_calls_contain "gh attestation"
}

@test "an asset replaced since preflight is refused and removed" {
  echo "something else" >"${SERVED}"
  run_script download-asset-by-digest.sh
  assert_failure
  assert_output_contains "is not the file preflight verified"
  [[ ! -e "${DEST_DIR}/${ASSET}" ]]
  assert_equal "$(cat "${GITHUB_OUTPUT}")" ""
}

@test "a missing or malformed expected digest is refused before downloading" {
  for digest in "" "abc" "$(printf 'A%.0s' {1..64})"; do
    export EXPECTED_SHA256="${digest}"
    run_script download-asset-by-digest.sh
    assert_failure
  done
  refute_calls_contain "gh"
}
