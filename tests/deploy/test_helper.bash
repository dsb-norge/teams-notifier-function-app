# shellcheck shell=bash
# shellcheck disable=SC2154 # output and status are set by bats' run
#
# Shared helper for the bats suites in tests/deploy/, loaded with `load test_helper`.
#
# The scripts under test run as processes, the way the workflow runs them, with their inputs in
# the environment and $GITHUB_OUTPUT / $GITHUB_STEP_SUMMARY pointed at files in the test's temp
# directory. External commands (gh, curl, sleep) are replaced by stubs on PATH, written per suite
# so each one answers only the calls that script makes. An unexpected call fails loudly (exit 64)
# rather than passing for a reason nobody wrote down.
#
# No helper libraries are vendored: the assertions below are all these suites need.

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
DEPLOY_SCRIPTS="${REPO_ROOT}/scripts/deploy"
export REPO_ROOT DEPLOY_SCRIPTS

# Per-test sandbox: outputs, summary, stub bin directory and a call log.
setup_sandbox() {
  export GITHUB_OUTPUT="${BATS_TEST_TMPDIR}/github-output"
  export GITHUB_STEP_SUMMARY="${BATS_TEST_TMPDIR}/step-summary.md"
  export GITHUB_PATH="${BATS_TEST_TMPDIR}/github-path"
  export STUB_CALLS="${BATS_TEST_TMPDIR}/calls.log"
  : >"${GITHUB_OUTPUT}"
  : >"${GITHUB_STEP_SUMMARY}"
  : >"${GITHUB_PATH}"
  : >"${STUB_CALLS}"
  mkdir -p "${BATS_TEST_TMPDIR}/bin"
  export PATH="${BATS_TEST_TMPDIR}/bin:${PATH}"
}

# Writes an executable stub: stub <name> <<'EOF' ... EOF. The body runs under bash with
# `set -euo pipefail`, and every call is logged to $STUB_CALLS first.
stub() {
  local name="${1}"
  {
    echo '#!/usr/bin/env bash'
    echo 'set -euo pipefail'
    # shellcheck disable=SC2016 # written into the stub unexpanded
    printf 'echo "%s $*" >>"${STUB_CALLS}"\n' "${name}"
    cat
  } >"${BATS_TEST_TMPDIR}/bin/${name}"
  chmod +x "${BATS_TEST_TMPDIR}/bin/${name}"
}

# A sleep that doesn't, so retry loops run instantly. The calls are still logged.
stub_sleep() {
  stub sleep <<<''
}

# Writes a ZIP with exactly the given entries, which `zip` itself won't create: make_zip <path>
# <name>:<file|link>... A link entry points at /etc/hostname. Names are stored as given, so
# "../x" and "/x" can be written too.
make_zip() {
  python3 - "${@}" <<'PY'
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1], "w") as archive:
    for spec in sys.argv[2:]:
        name, kind = spec.rsplit(":", 1)
        info = zipfile.ZipInfo(name)
        info.create_system = 3  # Unix, so unzip honours the mode bits
        if kind == "link":
            info.external_attr = 0o120777 << 16
            archive.writestr(info, "/etc/hostname")
        else:
            info.external_attr = 0o100644 << 16
            archive.writestr(info, "content")
PY
}

# Runs a script from scripts/deploy as a process.
run_script() {
  run bash "${DEPLOY_SCRIPTS}/${1}"
}

# The value of one output in $GITHUB_OUTPUT (the last one written, as the runner reads it).
output_value() {
  local name="${1}"
  sed -n "s/^${name}=//p" "${GITHUB_OUTPUT}" | tail -n 1
}

fail() {
  printf '%s\n' "${@}" >&2
  return 1
}

assert_success() {
  if ((status != 0)); then
    fail "expected success, got exit ${status}" "--- output ---" "${output}"
  fi
}

assert_failure() {
  if ((status == 0)); then
    fail "expected failure, got exit 0" "--- output ---" "${output}"
  fi
}

assert_equal() {
  if [[ "${1}" != "${2}" ]]; then
    fail "expected: ${2}" "  actual: ${1}"
  fi
}

assert_output_contains() {
  if [[ "${output}" != *"${1}"* ]]; then
    fail "output does not contain: ${1}" "--- output ---" "${output}"
  fi
}

refute_output_contains() {
  if [[ "${output}" == *"${1}"* ]]; then
    fail "output unexpectedly contains: ${1}" "--- output ---" "${output}"
  fi
}

assert_file_contains() {
  if ! grep -qF -- "${2}" "${1}"; then
    fail "${1} does not contain: ${2}" "--- ${1} ---" "$(cat -- "${1}")"
  fi
}

assert_calls_contain() {
  assert_file_contains "${STUB_CALLS}" "${1}"
}

refute_calls_contain() {
  if grep -qF -- "${1}" "${STUB_CALLS}"; then
    fail "unexpected call: ${1}" "--- calls ---" "$(cat -- "${STUB_CALLS}")"
  fi
}
