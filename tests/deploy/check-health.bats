#!/usr/bin/env bats
# Unit tests for scripts/deploy/check-health.sh, the skip_if_current decision, against a curl
# stub that plays back one response per attempt.
# shellcheck disable=SC2030,SC2031,SC2154 # bats runs each test in a subshell and sets output/status

load test_helper

setup() {
  setup_sandbox
  stub_sleep
  export SKIP_IF_CURRENT="true"
  export FUNCTION_APP_NAME="func-example-notifier"
  export TARGET_VERSION="2.1.0"
  # Attempt N plays curl-response.N: first line "ok" (the rest is the body) or "fail" (the rest
  # is curl's error). The last response repeats once they run out.
  export CURL_RESPONSES="${BATS_TEST_TMPDIR}/responses"
  mkdir -p "${CURL_RESPONSES}"
  stub curl <<'EOF'
n=$(grep -c '^curl ' "${STUB_CALLS}")
file="${CURL_RESPONSES}/${n}"
if [[ ! -f "${file}" ]]; then
  file="${CURL_RESPONSES}/$(find "${CURL_RESPONSES}" -type f -printf '%f\n' | sort -n | tail -n 1)"
fi
if [[ "$(head -n 1 "${file}")" == "ok" ]]; then tail -n +2 "${file}"; else tail -n +2 "${file}" >&2; exit 22; fi
EOF
}

respond() {
  local n="${1}"
  shift
  printf '%s\n' "${@}" >"${CURL_RESPONSES}/${n}"
}

@test "skip_if_current off: deploys without asking /api/health" {
  export SKIP_IF_CURRENT="false"
  run_script check-health.sh
  assert_success
  assert_equal "$(output_value deploy)" "true"
  assert_equal "$(output_value reason)" "deploy"
  refute_calls_contain "curl"
}

@test "the target version already runs: nothing to deploy" {
  respond 1 ok '{"status":"ok","version":"2.1.0","timestamp":"2026-01-01T00:00:00Z"}'
  run_script check-health.sh
  assert_success
  assert_equal "$(output_value deploy)" "false"
  # "current" lets package-manifest still build the Teams package.
  assert_equal "$(output_value reason)" "current"
  assert_equal "$(output_value current_version)" "2.1.0"
  assert_output_contains "already runs 2.1.0: nothing to deploy"
  assert_calls_contain "curl -fsS --max-time 10 https://func-example-notifier.azurewebsites.net/api/health"
}

@test "another version runs: deploy" {
  respond 1 ok '{"status":"ok","version":"2.0.3"}'
  run_script check-health.sh
  assert_success
  assert_equal "$(output_value deploy)" "true"
  assert_equal "$(output_value reason)" "deploy"
  assert_equal "$(output_value current_version)" "2.0.3"
}

@test "a pre-release runs and the release is the target: deploy" {
  respond 1 ok '{"version":"2.1.0-pre.3"}'
  run_script check-health.sh
  assert_success
  assert_equal "$(output_value deploy)" "true"
}

@test "unreachable on all three tries: nothing deployed, with a warning" {
  respond 1 fail "curl: (28) Connection timed out"
  run_script check-health.sh
  assert_success
  assert_equal "$(output_value deploy)" "false"
  # Not "current": a package must never be built for a version nobody saw running.
  assert_equal "$(output_value reason)" "unreachable"
  assert_equal "$(output_value current_version)" ""
  assert_output_contains "::warning title=Health check::"
  assert_equal "$(grep -c '^curl ' "${STUB_CALLS}")" "3"
  assert_equal "$(grep -c '^sleep 5$' "${STUB_CALLS}")" "2"
  assert_file_contains "${GITHUB_STEP_SUMMARY}" "never deploys blind"
}

@test "a flaky first try doesn't decide" {
  respond 1 fail "curl: (22) The requested URL returned error: 503"
  respond 2 ok '{"version":"2.1.0"}'
  run_script check-health.sh
  assert_success
  assert_equal "$(output_value deploy)" "false"
  assert_equal "$(grep -c '^curl ' "${STUB_CALLS}")" "2"
}

@test "an answer without a usable version counts as no answer" {
  for body in '<html>404</html>' '{"status":"ok"}' '{"version":null}' '{"version":5}' '{"version":"latest"}'; do
    : >"${STUB_CALLS}"
    : >"${GITHUB_OUTPUT}"
    respond 1 ok "${body}"
    run_script check-health.sh
    assert_success
    assert_equal "$(output_value deploy)" "false"
    assert_equal "$(output_value reason)" "unreachable"
    assert_output_contains "answered without a version (attempt 3/3)"
  done
}

@test "a function app name that isn't one is refused before any request" {
  for name in "evil.example.com/x" "-leading" "a" "name with space"; do
    export FUNCTION_APP_NAME="${name}"
    run_script check-health.sh
    assert_failure
    assert_output_contains "is not a valid function app name"
  done
  refute_calls_contain "curl"
}

@test "every outcome sets deploy and reason together, exactly once" {
  for body in '{"version":"2.1.0"}' '{"version":"2.0.3"}' '{"status":"ok"}'; do
    : >"${GITHUB_OUTPUT}"
    : >"${STUB_CALLS}"
    respond 1 ok "${body}"
    run_script check-health.sh
    assert_success
    assert_equal "$(grep -c '^deploy=' "${GITHUB_OUTPUT}")" "1"
    assert_equal "$(grep -c '^reason=' "${GITHUB_OUTPUT}")" "1"
    case "$(output_value reason)" in
      deploy) assert_equal "$(output_value deploy)" "true" ;;
      current | unreachable) assert_equal "$(output_value deploy)" "false" ;;
      *) fail "unexpected reason '$(output_value reason)'" ;;
    esac
  done
}

@test "skip_if_current must be a boolean" {
  export SKIP_IF_CURRENT="yes"
  run_script check-health.sh
  assert_failure
}
