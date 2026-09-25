#!/usr/bin/env bash
#
# security-findings.sh
#
# Lists the open findings on the repository's "Security and quality" tab:
# code scanning (CodeQL, Trivy, BinSkim), Code Quality standard findings,
# Dependabot vulnerability and malware alerts, and secret scanning default and
# generic alerts. Findings recorded as permanently ignored in
# docs/security-findings.md are left out; entries there that are no longer open
# are reported so they can be pruned.
#
# Code Quality AI findings are not covered: GitHub has no API for them.
# Read-only: nothing is dismissed or changed on GitHub.
#
# Usage:
#   bash security-findings.sh [--repo <owner/name>] [--wait] [--all] [--json]
#
#   --repo   repository to query (default: dsb-norge/teams-notifier-function-app)
#   --wait   first wait until the scans on main's HEAD commit have finished
#   --all    also list the permanently ignored findings, marked "(ignored)"
#   --json   print one JSON object instead of the text report
#
# Requires gh (authenticated, `repo` scope) and jq.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IGNORE_DOC="${SCRIPT_DIR}/../docs/security-findings.md"

REPO="dsb-norge/teams-notifier-function-app"
BRANCH="main"
WAIT=false
SHOW_IGNORED=false
JSON=false

# Workflow runs that feed the Security tab. Matched against the run's `path`.
SCAN_RUN_PATTERN='^dynamic/github-code-(scanning|quality)/|^dynamic/dependency-graph/|/msdo\.yml$'
WAIT_INTERVAL=20
WAIT_TIMEOUT=1800

# Generic secret types are not returned unless asked for by name.
# https://docs.github.com/en/code-security/secret-scanning/introduction/supported-secret-scanning-patterns
GENERIC_SECRET_TYPES="ec_private_key,generic_private_key,http_basic_authentication_header,http_bearer_authentication_header,mongodb_connection_string,mysql_connection_url,openssh_private_key,pgp_private_key,postgres_connection_string,rsa_private_key,password"

# --- Parse arguments ---
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) REPO="$2"; shift 2 ;;
    --wait) WAIT=true; shift ;;
    --all) SHOW_IGNORED=true; shift ;;
    --json) JSON=true; shift ;;
    -h|--help)
      echo "Usage: $(basename "$0") [--repo <owner/name>] [--wait] [--all] [--json]"
      exit 0
      ;;
    *)
      echo "Error: Unknown option '$1'" >&2
      exit 1
      ;;
  esac
done

# --- Helpers ---

# gh api across all pages, merged into one array.
api_list() {
  gh api --paginate "$1" | jq -s 'add // []'
}

scan_runs() {
  gh api "repos/${REPO}/actions/runs?head_sha=$1&per_page=100" \
    | jq --arg p "$SCAN_RUN_PATTERN" \
        '[.workflow_runs[] | select(.path | test($p)) | {name, status, conclusion}]'
}

# --- Scan freshness ---
HEAD_SHA="$(gh api "repos/${REPO}/commits/${BRANCH}" -q .sha)"
RUNS="$(scan_runs "$HEAD_SHA")"

if $WAIT; then
  waited=0
  until jq -e 'length > 0 and all(.status == "completed")' <<<"$RUNS" >/dev/null; do
    if (( waited >= WAIT_TIMEOUT )); then
      echo "Error: scans on ${HEAD_SHA:0:7} not finished after ${WAIT_TIMEOUT}s" >&2
      exit 2
    fi
    sleep "$WAIT_INTERVAL"
    waited=$((waited + WAIT_INTERVAL))
    RUNS="$(scan_runs "$HEAD_SHA")"
  done
fi

# --- Permanently ignored findings ---
# Table rows in the ignore doc start with the finding ID in backticks.
IGNORED='[]'
if [[ -f "$IGNORE_DOC" ]]; then
  # shellcheck disable=SC2016  # the backticks are literal Markdown
  IGNORED="$(grep -oE '^\| *`(code-scanning|code-quality|dependabot|secret-scanning)#[0-9]+`' "$IGNORE_DOC" \
    | grep -oE '[a-z-]+#[0-9]+' \
    | jq -R -s 'split("\n") | map(select(length > 0)) | unique' || true)"
  [[ -n "$IGNORED" ]] || IGNORED='[]'
fi

# --- Fetch and normalise ---
CODE_SCANNING="$(api_list "repos/${REPO}/code-scanning/alerts?state=open&per_page=100" | jq '
  map({
    id: "code-scanning#\(.number)",
    tool: .tool.name,
    severity: (.rule.security_severity_level // .rule.severity),
    rule: .rule.id,
    location: "\(.most_recent_instance.location.path):\(.most_recent_instance.location.start_line)",
    title: .rule.description,
    url: .html_url
  })')"

CODE_QUALITY="$(api_list "repos/${REPO}/code-quality/findings?state=open&per_page=100" | jq '
  map({
    id: "code-quality#\(.number)",
    tool: "CodeQL",
    severity: .rule.severity,
    rule: .rule.id,
    location: "\(.location.path):\(.location.start_line)",
    title: .rule.title,
    url: null
  })')"

DEPENDABOT="$(api_list "repos/${REPO}/dependabot/alerts?state=open&classification=malware,general&per_page=100" | jq '
  map({
    id: "dependabot#\(.number)",
    tool: (if .security_advisory.classification == "malware" then "Dependabot malware" else "Dependabot" end),
    severity: .security_advisory.severity,
    rule: .security_advisory.ghsa_id,
    location: "\(.dependency.package.ecosystem):\(.dependency.package.name) (\(.dependency.manifest_path))",
    title: (.security_advisory.summary
      + (if .security_vulnerability.first_patched_version.identifier
         then " (fixed in \(.security_vulnerability.first_patched_version.identifier))" else "" end)),
    url: .html_url
  })')"

# hide_secret keeps secret values out of the output.
secret_alerts() {
  api_list "repos/${REPO}/secret-scanning/alerts?state=open&hide_secret=true&per_page=100$1" | jq --arg tool "$2" '
    map({
      id: "secret-scanning#\(.number)",
      tool: $tool,
      severity: "secret",
      rule: .secret_type,
      location: (if .first_location_detected.path
                 then "\(.first_location_detected.path):\(.first_location_detected.start_line)" else null end),
      title: .secret_type_display_name,
      url: .html_url
    })'
}
SECRETS="$(jq -s 'add | unique_by(.id)' \
  <(secret_alerts "" "Secret scanning") \
  <(secret_alerts "&secret_type=${GENERIC_SECRET_TYPES}" "Secret scanning (generic)"))"

REPORT="$(jq -n \
  --arg repo "$REPO" --arg branch "$BRANCH" --arg sha "$HEAD_SHA" \
  --argjson runs "$RUNS" --argjson ignored "$IGNORED" \
  --argjson cs "$CODE_SCANNING" --argjson cq "$CODE_QUALITY" \
  --argjson dep "$DEPENDABOT" --argjson sec "$SECRETS" '
  ($cs + $cq + $dep + $sec | map(.id as $id | .ignored = any($ignored[]; . == $id))) as $all
  | {
      repo: $repo,
      branch: $branch,
      head_sha: $sha,
      scans_complete: ($runs | length > 0 and all(.status == "completed")),
      scan_runs: $runs,
      findings: $all,
      stale_ignores: ($ignored - ($all | map(.id)))
    }')"

if $JSON; then
  echo "$REPORT"
  exit 0
fi

# --- Text report ---
jq -r --argjson show_ignored "$SHOW_IGNORED" '
  def section($prefix; $label):
    [.findings[] | select(.id | startswith($prefix + "#"))] as $f
    | ($f | map(select(.ignored | not))) as $open
    | ($f | map(select(.ignored))) as $ign
    | "\n\($label): \($open | length) to assess"
      + (if ($ign | length) > 0 then ", \($ign | length) ignored" else "" end),
      ( ($open + (if $show_ignored then $ign else [] end))[]
        | "  \(.id)  \(.severity // "-")  \(.tool): \(.rule)  \(.location // "")"
          + (if .ignored then "  (ignored)" else "" end),
          (if .title then "      \(.title)" else empty end) );

  "Security and quality findings for \(.repo) (\(.branch) at \(.head_sha[0:7]))",
  ( if (.scan_runs | length) == 0 then
      "WARNING: no scan runs found for \(.head_sha[0:7]) yet; results may predate it. Rerun with --wait."
    elif .scans_complete then
      "Scans on \(.head_sha[0:7]): \(.scan_runs | length) runs, all completed."
    else
      "WARNING: scans on \(.head_sha[0:7]) still running (\([.scan_runs[] | select(.status != "completed") | .name] | join(", "))). Rerun with --wait."
    end ),
  section("code-scanning"; "Code scanning"),
  section("code-quality"; "Code Quality (standard findings)"),
  section("dependabot"; "Dependabot"),
  section("secret-scanning"; "Secret scanning"),
  ( if (.stale_ignores | length) > 0 then
      "\nNo longer open, prune from docs/security-findings.md: \(.stale_ignores | join(", "))"
    else empty end ),
  "\nTotal: \([.findings[] | select(.ignored | not)] | length) to assess, \([.findings[] | select(.ignored)] | length) permanently ignored.",
  "Not covered: Code Quality AI findings (no API; UI only)."
' <<<"$REPORT"
