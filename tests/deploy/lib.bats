#!/usr/bin/env bats
# Unit tests for scripts/deploy/lib.bash, sourced into the test.
# shellcheck disable=SC1091,SC2030,SC2031,SC2154 # sourced via a variable path; bats subshells and output/status

load test_helper

setup() {
  setup_sandbox
  # shellcheck source=../../scripts/deploy/lib.bash
  source "${DEPLOY_SCRIPTS}/lib.bash"
}

@test "escape_data keeps a newline from starting a second workflow command" {
  run escape_data $'tag\n::add-mask::secret'
  assert_equal "${output}" 'tag%0A::add-mask::secret'
  run escape_data '100% done'
  assert_equal "${output}" '100%25 done'
}

@test "escape_property also escapes ':' and ','" {
  run escape_property 'a:b,c'
  assert_equal "${output}" 'a%3Ab%2Cc'
}

@test "die prints an error annotation and exits 1" {
  run die "boom"
  assert_failure
  assert_equal "${output}" "::error::boom"
}

@test "require_env names the first missing variable" {
  export PRESENT="x"
  unset ABSENT
  run require_env PRESENT ABSENT
  assert_failure
  assert_output_contains "ABSENT is required"
}

@test "require_bool accepts only true and false" {
  export FLAG="true"
  run require_bool FLAG
  assert_success
  export FLAG="yes"
  run require_bool FLAG
  assert_failure
  assert_output_contains "FLAG must be 'true' or 'false'"
}

@test "set_output refuses a multi-line value" {
  run set_output name $'a\nb'
  assert_failure
  assert_equal "$(cat "${GITHUB_OUTPUT}")" ""
}

@test "trim strips surrounding whitespace only" {
  run trim $'  teams-notifier-function-app-v1.2.3 \t\n'
  assert_equal "${output}" "teams-notifier-function-app-v1.2.3"
}

@test "json_string_field fails on a missing, empty or non-string field" {
  printf '{"a":"x","b":"","c":5}' >"${BATS_TEST_TMPDIR}/f.json"
  run json_string_field "${BATS_TEST_TMPDIR}/f.json" a
  assert_success
  assert_equal "${output}" "x"
  for field in b c d; do
    run json_string_field "${BATS_TEST_TMPDIR}/f.json" "${field}"
    assert_failure
  done
  printf 'not json' >"${BATS_TEST_TMPDIR}/g.json"
  run json_string_field "${BATS_TEST_TMPDIR}/g.json" a
  assert_failure
}

@test "teams_package_shorthash keeps the formula existing package releases were named with" {
  # Golden value: the first 8 hex digits of sha256("manifest" "color" "outline"), in that order.
  # If this changes, every instance republishes its Teams package under a new name.
  printf 'manifest' >"${BATS_TEST_TMPDIR}/manifest.json"
  printf 'color' >"${BATS_TEST_TMPDIR}/color.png"
  printf 'outline' >"${BATS_TEST_TMPDIR}/outline.png"
  run teams_package_shorthash "${BATS_TEST_TMPDIR}/manifest.json" "${BATS_TEST_TMPDIR}/color.png" "${BATS_TEST_TMPDIR}/outline.png"
  assert_equal "${output}" "$(printf 'manifestcoloroutline' | sha256sum | cut -c1-8)"
  assert_equal "${output}" "57f9eff2"
}

@test "the release version pattern accepts releases and pre-releases only" {
  for good in 1.2.3 10.20.30 1.8.2-pre.2 2.0.0-rc.1; do
    [[ "${good}" =~ ${RELEASE_VERSION_RE} ]] || fail "rejected ${good}"
  done
  for bad in 1.2 v1.2.3 1.2.3- 1.2.3-pre..1 '1.2.3 ' 1.2.3+build; do
    if [[ "${bad}" =~ ${RELEASE_VERSION_RE} ]]; then fail "accepted '${bad}'"; fi
  done
}

@test "check_zip_entries accepts regular files and directories" {
  mkdir -p "${BATS_TEST_TMPDIR}/src/sub"
  echo a >"${BATS_TEST_TMPDIR}/src/a.dll"
  echo b >"${BATS_TEST_TMPDIR}/src/sub/b.json"
  (cd "${BATS_TEST_TMPDIR}/src" && zip -q -r "${BATS_TEST_TMPDIR}/ok.zip" .)
  run check_zip_entries "${BATS_TEST_TMPDIR}/ok.zip"
  assert_success
  assert_equal "${output}" ""
}

@test "check_zip_entries refuses symlinks and paths out of the extraction directory" {
  local zip="${BATS_TEST_TMPDIR}/bad.zip" entry
  for entry in link.dll:link ../up.dll:file a/../../up.dll:file /abs.dll:file 'win\\path.dll:file' ..:file; do
    rm -f "${zip}"
    make_zip "${zip}" ok.dll:file "${entry}"
    run check_zip_entries "${zip}"
    assert_failure
    assert_output_contains "refusing to unpack it"
  done
}

@test "check_zip_entries keeps names and types paired across a name with a line break" {
  # unzip lists control characters escaped (^J), one entry per line, so the link after the
  # odd name is still seen as a link.
  make_zip "${BATS_TEST_TMPDIR}/nl.zip" ok.dll:file $'two\nlines.dll:file' link.dll:link
  run check_zip_entries "${BATS_TEST_TMPDIR}/nl.zip"
  assert_failure
  assert_output_contains "has entry 'link.dll' that is not a regular file or a directory (type l)"
}

@test "check_zip_entries refuses what isn't a ZIP" {
  echo "not a zip" >"${BATS_TEST_TMPDIR}/x.zip"
  run check_zip_entries "${BATS_TEST_TMPDIR}/x.zip"
  assert_failure
  assert_output_contains "is not a readable ZIP"
}
