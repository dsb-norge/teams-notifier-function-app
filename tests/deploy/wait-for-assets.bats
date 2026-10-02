#!/usr/bin/env bats
# Unit tests for scripts/deploy/wait-for-assets.sh, against a gh stub that answers
# `gh release view --json assets --jq '.assets[].name'` from a per-test script of responses.
# shellcheck disable=SC2030,SC2031,SC2154 # bats runs each test in a subshell and sets output/status

load test_helper

setup() {
  setup_sandbox
  stub_sleep
  export APP_REPO="example-org/teams-notifier-function-app"
  export TAG="teams-notifier-function-app-v2.1.0"
  export ASSETS="teams-notifier-function-app-v2.1.0.zip app-requirements.json"
  export WAIT_ATTEMPTS=3
  export WAIT_SECONDS=10
  # Response N is read from gh-response.N: first line "ok" or "fail", the rest is stdout
  # (ok) or stderr (fail). The last response repeats once they run out.
  export GH_RESPONSES="${BATS_TEST_TMPDIR}/responses"
  mkdir -p "${GH_RESPONSES}"
  stub gh <<'EOF'
[[ "$1 $2" == "release view" ]] || { echo "gh stub: unhandled: $*" >&2; exit 64; }
n=$(grep -c '^gh release view' "${STUB_CALLS}")
file="${GH_RESPONSES}/${n}"
if [[ ! -f "${file}" ]]; then
  file="${GH_RESPONSES}/$(find "${GH_RESPONSES}" -type f -printf '%f\n' | sort -n | tail -n 1)"
fi
kind="$(head -n 1 "${file}")"
if [[ "${kind}" == "ok" ]]; then tail -n +2 "${file}"; else tail -n +2 "${file}" >&2; exit 1; fi
EOF
}

respond() {
  local n="${1}"
  shift
  printf '%s\n' "${@}" >"${GH_RESPONSES}/${n}"
}

@test "returns at once when every asset is there" {
  respond 1 ok teams-app-package-v2.1.0.tar.gz app-requirements.json teams-notifier-function-app-v2.1.0.zip
  run_script wait-for-assets.sh
  assert_success
  assert_output_contains "has every required asset"
  refute_calls_contain "sleep"
  assert_calls_contain "gh release view teams-notifier-function-app-v2.1.0 --repo example-org/teams-notifier-function-app --json assets"
}

@test "waits while assets are still being uploaded" {
  respond 1 ok
  respond 2 ok app-requirements.json
  respond 3 ok app-requirements.json teams-notifier-function-app-v2.1.0.zip
  run_script wait-for-assets.sh
  assert_success
  assert_output_contains "still missing teams-notifier-function-app-v2.1.0.zip app-requirements.json (attempt 1/3)"
  assert_output_contains "still missing teams-notifier-function-app-v2.1.0.zip (attempt 2/3)"
  assert_equal "$(grep -c '^sleep 10$' "${STUB_CALLS}")" "2"
}

@test "an asset name must match exactly, not as a substring" {
  respond 1 ok app-requirements.json.bak teams-notifier-function-app-v2.1.0.zip
  run_script wait-for-assets.sh
  assert_failure
  assert_output_contains "still lacks app-requirements.json after 3 attempts"
}

@test "gives up after the last attempt, without a final sleep" {
  respond 1 ok app-requirements.json
  run_script wait-for-assets.sh
  assert_failure
  assert_output_contains "::error::Release teams-notifier-function-app-v2.1.0 still lacks teams-notifier-function-app-v2.1.0.zip after 3 attempts."
  assert_equal "$(grep -c '^sleep' "${STUB_CALLS}")" "2"
}

@test "a release that doesn't exist fails at once" {
  respond 1 fail "release not found"
  run_script wait-for-assets.sh
  assert_failure
  assert_output_contains "does not exist in example-org/teams-notifier-function-app"
  assert_equal "$(grep -c '^gh ' "${STUB_CALLS}")" "1"
}

@test "other gh failures are retried" {
  respond 1 fail "HTTP 502: Bad Gateway"
  respond 2 ok app-requirements.json teams-notifier-function-app-v2.1.0.zip
  run_script wait-for-assets.sh
  assert_success
  assert_output_contains "Could not read release teams-notifier-function-app-v2.1.0 (attempt 1/3): HTTP 502"
}
