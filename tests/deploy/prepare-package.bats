#!/usr/bin/env bats
# Unit tests for scripts/deploy/prepare-package.sh, with a real ZIP and the repository's own
# project file.
# shellcheck disable=SC2030,SC2031,SC2154 # bats runs each test in a subshell and sets output/status

load test_helper

setup() {
  setup_sandbox
  mkdir -p "${BATS_TEST_TMPDIR}/build"
  echo "binary" >"${BATS_TEST_TMPDIR}/build/TeamsNotificationBot.dll"
  echo '{"version":"2.0"}' >"${BATS_TEST_TMPDIR}/build/host.json"
  (cd "${BATS_TEST_TMPDIR}/build" && zip -q -r "${BATS_TEST_TMPDIR}/app.zip" .)
  export ZIP_PATH="${BATS_TEST_TMPDIR}/app.zip"
  export CSPROJ_PATH="${REPO_ROOT}/src/TeamsNotificationBot/TeamsNotificationBot.csproj"
  export PUBLISH_DIR="${BATS_TEST_TMPDIR}/publish"
}

@test "unpacks the ZIP and writes a stub project with the real target framework" {
  expected="$(sed -n 's:.*<TargetFramework>\([^<]*\)</TargetFramework>.*:\1:p' "${CSPROJ_PATH}")"
  run_script prepare-package.sh
  assert_success
  assert_equal "$(output_value tfm)" "${expected}"
  [[ -f "${PUBLISH_DIR}/TeamsNotificationBot.dll" && -f "${PUBLISH_DIR}/host.json" ]]
  assert_file_contains "${PUBLISH_DIR}/stub.csproj" "<TargetFramework>${expected}</TargetFramework>"
}

@test "leftovers from an earlier run on the same runner are removed" {
  mkdir -p "${PUBLISH_DIR}"
  echo old >"${PUBLISH_DIR}/Stale.dll"
  run_script prepare-package.sh
  assert_success
  [[ ! -e "${PUBLISH_DIR}/Stale.dll" ]]
}

@test "a ZIP that would write through a symlink on the runner is refused" {
  # stub.csproj as a link: writing the stub would overwrite the link's target, a file on the
  # caller's persistent deploy runner.
  local target="${BATS_TEST_TMPDIR}/runner-file"
  echo "original" >"${target}"
  python3 - "${ZIP_PATH}" "${target}" <<'PY'
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1], "a") as archive:
    info = zipfile.ZipInfo("stub.csproj")
    info.create_system = 3
    info.external_attr = 0o120777 << 16
    archive.writestr(info, sys.argv[2])
PY
  run_script prepare-package.sh
  assert_failure
  assert_output_contains "has entry 'stub.csproj' that is not a regular file or a directory (type l)"
  assert_equal "$(cat "${target}")" "original"
  [[ ! -e "${PUBLISH_DIR}" ]]
}

@test "a ZIP with a path out of the publish directory is refused before unpacking" {
  for entry in ../evil.dll /tmp/evil.dll 'sub\..\evil.dll'; do
    make_zip "${ZIP_PATH}" "TeamsNotificationBot.dll:file" "${entry}:file"
    run_script prepare-package.sh
    assert_failure
    assert_output_contains "with an absolute, parent or backslash path"
  done
  [[ ! -e "${PUBLISH_DIR}" ]]
}

@test "a project without a single target framework fails" {
  printf '<Project><PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup></Project>\n' \
    >"${BATS_TEST_TMPDIR}/multi.csproj"
  export CSPROJ_PATH="${BATS_TEST_TMPDIR}/multi.csproj"
  run_script prepare-package.sh
  assert_failure
  assert_output_contains "Could not read a single <TargetFramework>"
}
