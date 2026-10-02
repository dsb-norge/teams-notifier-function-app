#!/usr/bin/env bats
# Structural tests for .github/workflows/reusable-deploy.yml: the properties nothing at runtime
# would notice losing. A version read from the wrong place still deploys, just the wrong
# release; a dropped persist-credentials still goes green. Hence tests, not comments.
#
# The workflow is read with yq (mikefarah v4, preinstalled on GitHub's ubuntu runners), so the
# checks are on parsed YAML.
# shellcheck disable=SC2016,SC2030,SC2031,SC2154 # literal ${{ }} expressions are expected values; bats

load test_helper

WORKFLOW="${REPO_ROOT}/.github/workflows/reusable-deploy.yml"

setup() {
  command -v yq >/dev/null || fail "yq (mikefarah v4) is required for this suite"
}

wf() {
  yq -r "${1}" "${WORKFLOW}"
}

# --- the workflow's version -------------------------------------------------------------------

@test "release-please never edits a file under .github/" {
  # release.yml runs release-please with GITHUB_TOKEN, and GITHUB_TOKEN can't be granted the
  # `workflows` permission: GitHub rejects any push from it that changes a file under
  # .github/workflows/. An extra-files entry there would break the release PR the first time
  # release-please tried to update it, and every release after that. CI can't catch that, because
  # release-please only runs on main. The workflow reads its version from the manifest instead.
  run jq -r '.packages[]."extra-files"[]? | if type == "string" then . else .path end
    | select(startswith(".github/") or startswith("./.github/"))' "${REPO_ROOT}/release-please-config.json"
  assert_success
  [[ -z "${output}" ]] ||
    fail "release-please-config.json extra-files lists ${output}: GITHUB_TOKEN can't write workflow files, so the release PR would fail to update."
}

@test "preflight reads the workflow's version from the manifest at the workflow's own commit" {
  local checkout='.jobs.preflight.steps[] | select(.with.path == "${{ env.TOOLING_DIR }}")'
  assert_equal "$(wf "${checkout} | .with.ref")" '${{ steps.tooling.outputs.sha }}'
  assert_equal "$(wf "${checkout} | .with.\"sparse-checkout\"")" "/scripts/deploy/
/.release-please-manifest.json"
  assert_equal "$(wf "${checkout} | .with.\"sparse-checkout-cone-mode\"")" "false"
  assert_equal "$(wf '.jobs.preflight.steps[] | select(.id == "target") | .env.RELEASE_PLEASE_MANIFEST')" \
    '${{ env.TOOLING_DIR }}/.release-please-manifest.json'
  # The version check compares against that same version.
  assert_equal "$(wf '.jobs.preflight.steps[] | select(.run | test("check-requirements")) | .env.WORKFLOW_VERSION')" \
    '${{ steps.target.outputs.workflow_version }}'
}

# --- interface ----------------------------------------------------------------------------------

@test "the inputs are the documented set, with the documented required flags" {
  # docs/deployment-guide.md "Inputs" documents this table. Renaming or dropping one breaks every
  # caller that passes it, so it is a breaking change.
  run wf '.on.workflow_call.inputs | to_entries | .[] | .key + " " + (.value.required | tostring) + " " + .value.type'
  assert_equal "${output}" "tag false string
skip_if_current false boolean
app_requirements_file true string
teams_app_package_dir true string
instance_name true string
function_app_name true string
azure_environment true string
azure_tenant_id false string
azure_subscription_id false string
azure_client_id false string
runner true string
concurrency_group true string
bot_app_id true string
teams_app_id true string
upgrade_doc_url false string"
  assert_equal "$(wf '.on.workflow_call.inputs.tag.default')" ""
  assert_equal "$(wf '.on.workflow_call.inputs.skip_if_current.default')" "false"
  for id in azure_tenant_id azure_subscription_id azure_client_id; do
    assert_equal "$(wf ".on.workflow_call.inputs.${id}.default")" ""
  done
}

@test "the default upgrade_doc_url points at a heading that exists in this repository's docs" {
  local url anchor slugs
  url="$(wf '.on.workflow_call.inputs.upgrade_doc_url.default')"
  [[ "${url}" == "https://github.com/dsb-norge/teams-notifier-function-app/blob/main/docs/deployment-guide.md#"* ]] ||
    fail "unexpected default: ${url}"
  anchor="${url##*#}"
  # GitHub's heading anchors: lower case, punctuation dropped, spaces to hyphens.
  slugs="$(sed -n 's/^#\{1,6\} //p' "${REPO_ROOT}/docs/deployment-guide.md" |
    tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9 _-]//g; s/ /-/g')"
  grep -qxF -- "${anchor}" <<<"${slugs}" || fail "no heading in docs/deployment-guide.md has the anchor #${anchor}"
}

@test "the Azure ids fall back to the environment secrets, and no other secret is read" {
  run grep -oE 'secrets\.[A-Za-z_]+' "${WORKFLOW}"
  assert_equal "$(sort -u <<<"${output}")" "secrets.AZURE_CLIENT_ID
secrets.AZURE_SUBSCRIPTION_ID
secrets.AZURE_TENANT_ID"
  for id in tenant subscription client; do
    local upper="${id^^}"
    assert_equal "$(wf ".jobs.deploy.steps[] | select(.uses == \"azure/login@*\") | .with.\"${id}-id\"")" \
      "\${{ inputs.azure_${id}_id || secrets.AZURE_${upper}_ID }}"
  done
}

# --- least privilege and pinning ----------------------------------------------------------------

@test "the workflow grants nothing by default and each job only what it needs" {
  assert_equal "$(wf '.permissions | length')" "0"
  assert_equal "$(wf '.jobs.preflight.permissions | to_entries | .[] | .key + "=" + .value')" "contents=read"
  assert_equal "$(wf '.jobs.deploy.permissions | to_entries | .[] | .key + "=" + .value')" "contents=read
id-token=write"
  assert_equal "$(wf '.jobs."package-manifest".permissions | to_entries | .[] | .key + "=" + .value')" "contents=read"
  assert_equal "$(wf '.jobs."publish-package".permissions | to_entries | .[] | .key + "=" + .value')" "contents=write"
}

@test "no checkout leaves its token in .git/config" {
  run wf '.jobs[].steps[] | select(.uses == "actions/checkout@*") | .with."persist-credentials"'
  assert_success
  [[ -n "${output}" ]] || fail "no checkout steps found"
  [[ "$(sort -u <<<"${output}")" == "false" ]] || fail "persist-credentials values: ${output}"
}

@test "every action is pinned to a full commit SHA with a version comment" {
  run grep -nE '^\s*(- )?uses:' "${WORKFLOW}"
  assert_success
  while IFS= read -r line; do
    [[ "${line}" =~ uses:\ [A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}\ \#\ v[0-9] ]] || fail "not SHA-pinned: ${line}"
  done <<<"${output}"
}

@test "no run: script expands an expression; values arrive through env" {
  run wf '.jobs[].steps[] | select(has("run")) | .run | select(test("\$\{\{"))'
  assert_equal "${output}" ""
}

# --- the jobs -----------------------------------------------------------------------------------

@test "every job runs its scripts from this workflow's own commit" {
  assert_equal "$(wf '.jobs.preflight.steps[] | select(.id == "tooling") | .env.WORKFLOW_SHA')" '${{ job.workflow_sha }}'
  assert_equal "$(wf '.jobs.preflight.outputs.tooling_sha')" '${{ steps.tooling.outputs.sha }}'
  local job
  for job in preflight deploy package-manifest publish-package; do
    run wf ".jobs.\"${job}\".steps[] | select(.with.path == \"\${{ env.TOOLING_DIR }}\") | .with.ref"
    case "${job}" in
      preflight) assert_equal "${output}" '${{ steps.tooling.outputs.sha }}' ;;
      *) assert_equal "${output}" '${{ needs.preflight.outputs.tooling_sha }}' ;;
    esac
  done
}

@test "every script a step runs exists, and every script is run by a step" {
  local used
  used="$(grep -oE 'scripts/deploy/[a-z-]+\.sh' "${WORKFLOW}" | sort -u)"
  [[ -n "${used}" ]] || fail "the workflow runs no scripts"
  while IFS= read -r script; do
    [[ -f "${REPO_ROOT}/${script}" ]] || fail "${script} does not exist"
  done <<<"${used}"
  assert_equal "${used}" "$(cd "${REPO_ROOT}" && find scripts/deploy -name '*.sh' | sort)"
}

@test "deploy runs on the caller's runner, in the caller's environment, one at a time" {
  assert_equal "$(wf '.jobs.deploy.needs')" "preflight"
  assert_equal "$(wf '.jobs.deploy.if')" "needs.preflight.outputs.deploy == 'true'"
  assert_equal "$(wf '.jobs.deploy."runs-on"')" '${{ inputs.runner }}'
  assert_equal "$(wf '.jobs.deploy.environment')" '${{ inputs.azure_environment }}'
  assert_equal "$(wf '.jobs.deploy.concurrency.group')" '${{ inputs.concurrency_group }}'
  assert_equal "$(wf '.jobs.deploy.concurrency."cancel-in-progress"')" "false"
}

@test "only publish-package can write, and it runs no release code" {
  # The generator in package-manifest is release code, possibly a pre-release built from an
  # unreviewed branch, so that job never holds a write token. publish-package does, so it checks
  # out nothing but this workflow's scripts, and takes nothing from package-manifest but the ZIP.
  assert_equal "$(wf '[.jobs[] | select(.permissions.contents == "write")] | length')" "1"
  assert_equal "$(wf '.jobs."publish-package".permissions.contents')" "write"
  run wf '.jobs."publish-package".steps[] | select(.uses == "actions/checkout@*") | .with.path'
  assert_equal "${output}" '${{ env.TOOLING_DIR }}'
  run grep -c 'needs.package-manifest.outputs' "${WORKFLOW}"
  assert_equal "${output}" "0"
  assert_equal "$(wf '.jobs."publish-package".steps[] | select(.uses == "actions/download-artifact@*") | .with.name')" \
    "$(wf '.jobs."package-manifest".steps[] | select(.uses == "actions/upload-artifact@*") | .with.name')"
  # The only release code deploy checks out is the project file it reads with sed.
  assert_equal "$(wf '.jobs.deploy.steps[] | select(.with.path == "${{ env.APP_SOURCE_DIR }}") | .with."sparse-checkout"')" \
    "src/TeamsNotificationBot/TeamsNotificationBot.csproj"
}

@test "assets are verified once, against the tag's commit, and then taken by digest" {
  # gh attestation runs only in preflight, on a GitHub-hosted runner.
  for job in deploy package-manifest publish-package; do
    run wf ".jobs.\"${job}\".steps[] | select(.run | test(\"download-verified-asset\")) | .name"
    assert_equal "${output}" ""
  done
  assert_equal "$(wf '.jobs.preflight.outputs.zip_sha256')" '${{ steps.release-zip.outputs.sha256 }}'
  assert_equal "$(wf '.jobs.preflight.outputs.requirements_sha256')" '${{ steps.release-requirements.outputs.sha256 }}'
  assert_equal "$(wf '.jobs.deploy.steps[] | select(.id == "zip") | .env.EXPECTED_SHA256')" \
    '${{ needs.preflight.outputs.zip_sha256 }}'
  assert_equal "$(wf '.jobs."package-manifest".steps[] | select(.id == "release-requirements") | .env.EXPECTED_SHA256')" \
    '${{ needs.preflight.outputs.requirements_sha256 }}'
}

@test "publish-package runs whenever package-manifest succeeded" {
  # Spelled out, because package-manifest can succeed with deploy skipped, and a skipped job
  # further up the chain would otherwise skip publish-package too.
  local condition
  condition="$(wf '.jobs."publish-package".if')"
  condition="$(tr -s ' ' <<<"${condition//$'\n'/ }")"
  assert_equal "${condition}" "!cancelled() && needs.preflight.result == 'success' && needs.package-manifest.result == 'success'"
  assert_equal "$(wf '.jobs."publish-package".needs | join(" ")')" "preflight package-manifest"
}

@test "the Teams package is built after a deploy, or when the target version already runs" {
  assert_equal "$(wf '.jobs."package-manifest".needs | join(" ")')" "preflight deploy"
  assert_equal "$(wf '.jobs.preflight.outputs.reason')" '${{ steps.health.outputs.reason }}'
  # Whitespace-normalised: the YAML folds the expression over several lines.
  local condition
  condition="$(wf '.jobs."package-manifest".if')"
  condition="$(tr -s ' ' <<<"${condition//$'\n'/ }")"
  assert_equal "${condition}" \
    "!cancelled() && needs.preflight.result == 'success' && (needs.deploy.result == 'success' || (needs.deploy.result == 'skipped' && needs.preflight.outputs.reason == 'current'))"
}

@test "the package job's condition holds for every outcome of preflight and deploy" {
  # Evaluates the job's real if: expression, translated to Python, over the outcomes that can
  # happen. A term the translation doesn't know is a NameError, which fails the test rather
  # than being guessed at.
  run python3 "${BATS_TEST_DIRNAME}/eval_if.py" "$(wf '.jobs."package-manifest".if')"
  assert_success
  assert_equal "${output}" "all cases hold"
}

@test "the pinned func CLI is a version and a SHA-256" {
  [[ "$(wf '.jobs.deploy.env.FUNC_CLI_VERSION')" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
  [[ "$(wf '.jobs.deploy.env.FUNC_CLI_SHA256')" =~ ^[0-9a-f]{64}$ ]]
}
