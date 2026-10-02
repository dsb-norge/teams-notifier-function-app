#!/usr/bin/env bats
# Unit tests for scripts/deploy/install-func-cli.sh, against a gh stub that serves a small fake
# func CLI ZIP built in setup().
# shellcheck disable=SC2030,SC2031,SC2154 # bats runs each test in a subshell and sets output/status

load test_helper

setup() {
  setup_sandbox
  export FUNC_CLI_VERSION="4.15.2"
  export FUNC_CLI_CACHE_DIR="${BATS_TEST_TMPDIR}/cache"
  export FUNC_CLI_INSTALL_DIR="${BATS_TEST_TMPDIR}/func-cli"
  export RUNNER_ARCH="X64"

  mkdir -p "${BATS_TEST_TMPDIR}/fake-cli"
  printf '#!/usr/bin/env bash\necho 4.15.2\n' >"${BATS_TEST_TMPDIR}/fake-cli/func"
  printf '#!/usr/bin/env bash\n' >"${BATS_TEST_TMPDIR}/fake-cli/gozip"
  # Unzipped modes are not trusted: the script must chmod +x itself.
  chmod -x "${BATS_TEST_TMPDIR}/fake-cli/func" "${BATS_TEST_TMPDIR}/fake-cli/gozip"
  export FAKE_ZIP="${BATS_TEST_TMPDIR}/fake-cli.zip"
  (cd "${BATS_TEST_TMPDIR}/fake-cli" && zip -q -r "${FAKE_ZIP}" .)
  FUNC_CLI_SHA256="$(sha256sum "${FAKE_ZIP}" | cut -d' ' -f1)"
  export FUNC_CLI_SHA256

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
cp "${FAKE_ZIP}" "${dir}/${pattern}"
EOF
}

@test "downloads the pinned version, verifies it, installs it and puts it on PATH" {
  run_script install-func-cli.sh
  assert_success
  assert_calls_contain "gh release download 4.15.2 --repo Azure/azure-functions-core-tools --pattern Azure.Functions.Cli.linux-x64.4.15.2.zip --dir ${FUNC_CLI_CACHE_DIR}"
  [[ -x "${FUNC_CLI_INSTALL_DIR}/func" && -x "${FUNC_CLI_INSTALL_DIR}/gozip" ]]
  assert_equal "$(cat "${GITHUB_PATH}")" "${FUNC_CLI_INSTALL_DIR}"
  assert_output_contains "Azure Functions Core Tools 4.15.2 (reports 4.15.2)"
}

@test "a cached download is used, and still verified" {
  mkdir -p "${FUNC_CLI_CACHE_DIR}"
  cp "${FAKE_ZIP}" "${FUNC_CLI_CACHE_DIR}/Azure.Functions.Cli.linux-x64.4.15.2.zip"
  run_script install-func-cli.sh
  assert_success
  assert_output_contains "Using the cached"
  refute_calls_contain "gh release download"
}

@test "a poisoned cache entry is refused, not run" {
  mkdir -p "${FUNC_CLI_CACHE_DIR}"
  echo "not the pinned zip" >"${FUNC_CLI_CACHE_DIR}/Azure.Functions.Cli.linux-x64.4.15.2.zip"
  run_script install-func-cli.sh
  assert_failure
  assert_output_contains "does not match the pinned SHA-256"
  [[ ! -e "${FUNC_CLI_INSTALL_DIR}/func" ]]
  [[ ! -e "${FUNC_CLI_CACHE_DIR}/Azure.Functions.Cli.linux-x64.4.15.2.zip" ]]
  assert_equal "$(cat "${GITHUB_PATH}")" ""
}

@test "a download that doesn't match the pin is refused" {
  export FUNC_CLI_SHA256="0000000000000000000000000000000000000000000000000000000000000000"
  run_script install-func-cli.sh
  assert_failure
  assert_output_contains "does not match the pinned SHA-256"
}

@test "a runner that isn't x64 is refused before downloading" {
  export RUNNER_ARCH="ARM64"
  run_script install-func-cli.sh
  assert_failure
  assert_output_contains "linux-x64 build, but this runner is ARM64"
  refute_calls_contain "gh"
}

@test "malformed pins are refused" {
  export FUNC_CLI_VERSION="latest"
  run_script install-func-cli.sh
  assert_failure
  export FUNC_CLI_VERSION="4.15.2"
  export FUNC_CLI_SHA256="291F"
  run_script install-func-cli.sh
  assert_failure
  refute_calls_contain "gh"
}
