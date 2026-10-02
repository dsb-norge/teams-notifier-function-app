#!/usr/bin/env bats
# Unit tests for scripts/deploy/download-verified-asset.sh, against a gh stub for the tag's
# commit lookup, `gh release download` and `gh attestation verify`.
# shellcheck disable=SC2030,SC2031,SC2154 # bats runs each test in a subshell and sets output/status

load test_helper

setup() {
  setup_sandbox
  export APP_REPO="example-org/teams-notifier-function-app"
  export TAG="teams-notifier-function-app-v2.1.0"
  export ASSET="app-requirements.json"
  export DEST_DIR="${BATS_TEST_TMPDIR}/release"
  # GH_TAG_COMMIT: what the commit lookup answers ("" fails it). GH_DOWNLOAD_WRITES: "yes"
  # writes the asset. GH_ATTESTED_COMMIT: the commit the asset's attestation names.
  export GH_TAG_COMMIT="1111111111111111111111111111111111111111"
  export GH_DOWNLOAD_WRITES="yes"
  export GH_ATTESTED_COMMIT="${GH_TAG_COMMIT}"
  stub gh <<'EOF'
case "$1 $2" in
  "api repos/example-org/teams-notifier-function-app/commits/refs/tags/teams-notifier-function-app-v2.1.0")
    [[ -n "${GH_TAG_COMMIT}" ]] || { echo "gh: No commit found for SHA (HTTP 422)" >&2; exit 1; }
    echo "${GH_TAG_COMMIT}"
    ;;
  "release download")
    dir=""; pattern=""
    shift 2
    while (($#)); do
      case "$1" in
        --dir) dir="$2"; shift 2 ;;
        --pattern) pattern="$2"; shift 2 ;;
        *) shift ;;
      esac
    done
    if [[ "${GH_DOWNLOAD_WRITES}" == "yes" ]]; then echo '{"downloaded": true}' >"${dir}/${pattern}"; fi
    ;;
  "attestation verify")
    digest=""
    while (($#)); do
      if [[ "$1" == "--source-digest" ]]; then digest="$2"; fi
      shift
    done
    # Like the real check: an attestation for another commit doesn't verify.
    [[ -n "${digest}" && "${digest}" == "${GH_ATTESTED_COMMIT}" ]] || { echo "Error: verification failed" >&2; exit 1; }
    ;;
  *) echo "gh stub: unhandled: $*" >&2; exit 64 ;;
esac
EOF
}

@test "downloads the asset, verifies it against the tag's commit, and outputs path and digest" {
  run_script download-verified-asset.sh
  assert_success
  assert_equal "$(output_value path)" "${DEST_DIR}/app-requirements.json"
  assert_equal "$(output_value sha256)" "$(sha256sum "${DEST_DIR}/app-requirements.json" | cut -d' ' -f1)"
  assert_file_contains "${DEST_DIR}/app-requirements.json" '"downloaded": true'
  assert_calls_contain "gh api repos/example-org/teams-notifier-function-app/commits/refs/tags/teams-notifier-function-app-v2.1.0 --jq .sha"
  assert_calls_contain "gh release download teams-notifier-function-app-v2.1.0 --repo example-org/teams-notifier-function-app --pattern app-requirements.json --dir ${DEST_DIR}"
  # From this app's repository, for this tag's commit, built on a GitHub-hosted runner.
  assert_calls_contain "gh attestation verify ${DEST_DIR}/app-requirements.json --repo example-org/teams-notifier-function-app --source-digest 1111111111111111111111111111111111111111 --deny-self-hosted-runners"
}

@test "an attested file from another commit, such as an older release, fails" {
  export GH_ATTESTED_COMMIT="2222222222222222222222222222222222222222"
  run_script download-verified-asset.sh
  assert_failure
  assert_output_contains "for the tag's commit 1111111111111111111111111111111111111111"
  assert_equal "$(cat "${GITHUB_OUTPUT}")" ""
}

@test "an unresolvable tag fails before downloading" {
  export GH_TAG_COMMIT=""
  run_script download-verified-asset.sh
  assert_failure
  assert_output_contains "Could not resolve the commit of tag ${TAG}"
  refute_calls_contain "gh release download"
}

@test "a lookup that answers something other than a commit SHA fails" {
  export GH_TAG_COMMIT="not-a-sha"
  run_script download-verified-asset.sh
  assert_failure
  assert_output_contains "not a commit SHA"
  refute_calls_contain "gh release download"
}

@test "a stale file from an earlier run is never mistaken for a download" {
  mkdir -p "${DEST_DIR}"
  echo stale >"${DEST_DIR}/app-requirements.json"
  export GH_DOWNLOAD_WRITES="no"
  run_script download-verified-asset.sh
  assert_failure
  assert_output_contains "has no asset app-requirements.json"
  refute_calls_contain "gh attestation"
}

@test "an asset name with glob characters is refused" {
  export ASSET="*.zip"
  run_script download-verified-asset.sh
  assert_failure
  refute_calls_contain "gh"
}
