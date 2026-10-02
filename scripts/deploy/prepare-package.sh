#!/usr/bin/env bash
#
# Unpacks the release ZIP into a publish directory the func CLI accepts.
#
# `func azure functionapp publish` reads a .csproj to learn the target framework, and refuses to
# publish without one. The release ZIP holds only compiled output, so a stub project is written
# next to it, with the <TargetFramework> of the real project at the release's tag.
#
# Environment:
#   ZIP_PATH     the verified release ZIP
#   CSPROJ_PATH  TeamsNotificationBot.csproj, checked out at the release's tag
#   PUBLISH_DIR  the directory to (re)create
#
# Outputs:
#   tfm          the target framework, e.g. net10.0
set -euo pipefail
shopt -s inherit_errexit

# shellcheck source=lib.bash
source "$(dirname -- "${BASH_SOURCE[0]}")/lib.bash"

require_env ZIP_PATH CSPROJ_PATH PUBLISH_DIR
[[ -f "${ZIP_PATH}" ]] || die "Release ZIP ${ZIP_PATH} does not exist."
[[ -f "${CSPROJ_PATH}" ]] || die "Project file ${CSPROJ_PATH} does not exist."

# The first <TargetFramework> element; sed quits after it.
tfm="$(sed -n -E '/<TargetFramework>/{s:.*<TargetFramework>([^<]+)</TargetFramework>.*:\1:p;q;}' -- "${CSPROJ_PATH}")"
[[ "${tfm}" =~ ^net[0-9]+\.[0-9]+$ ]] ||
  die "Could not read a single <TargetFramework> (netX.Y) from ${CSPROJ_PATH}; got '${tfm}'."

# The ZIP is attested, but a caller may deploy a pre-release built from an unreviewed branch, and
# this runs on the caller's persistent deploy runner. No symlink or path out of PUBLISH_DIR may
# get through unzip, or the stub write below could follow it.
check_zip_entries "${ZIP_PATH}"
rm -rf -- "${PUBLISH_DIR}"
mkdir -p -- "${PUBLISH_DIR}"
unzip -q -- "${ZIP_PATH}" -d "${PUBLISH_DIR}"

# Replace, never write through, whatever the ZIP put at this name.
rm -rf -- "${PUBLISH_DIR}/stub.csproj"
cat >"${PUBLISH_DIR}/stub.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>${tfm}</TargetFramework></PropertyGroup>
</Project>
CSPROJ

notice "Target framework" "${tfm}"
set_output tfm "${tfm}"
