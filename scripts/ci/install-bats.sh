#!/usr/bin/env bash
#
# Installs bats-core at an exact commit, which git verifies.
#
# Not bats-core/bats-action: it downloads bats by tag name with no checksum, so a moved
# upstream tag would put different code into CI with no diff here. Fetching the pinned commit
# over the git protocol makes git check every object against the commit hash, so the ref is the
# integrity check and there is no checksum file to maintain.
#
# Environment:
#   BATS_CORE_REF  "vX.Y.Z@<40-hex commit>": the tag names the version for people, the commit
#                  is what installs. ci.yml sets it.
#   BATS_PREFIX    install prefix (default: $RUNNER_TEMP/bats-core, or /tmp/bats-core)
set -euo pipefail
shopt -s inherit_errexit

ref="${BATS_CORE_REF:?BATS_CORE_REF (vX.Y.Z@sha) is required}"
tag="${ref%%@*}"
sha="${ref##*@}"
if [[ ! "${sha}" =~ ^[0-9a-f]{40}$ ]]; then
  echo "::error::BATS_CORE_REF has no 40-hex commit: ${ref}"
  exit 1
fi

prefix="${BATS_PREFIX:-${RUNNER_TEMP:-/tmp}/bats-core}"
workdir="$(mktemp -d)"
trap 'rm -rf -- "${workdir}"' EXIT

git -C "${workdir}" init --quiet
git -C "${workdir}" fetch --quiet --depth 1 https://github.com/bats-core/bats-core.git "${sha}"
git -C "${workdir}" checkout --quiet FETCH_HEAD

actual="$(git -C "${workdir}" rev-parse HEAD)"
if [[ "${actual}" != "${sha}" ]]; then
  echo "::error::bats-core checkout mismatch: wanted ${sha}, got ${actual}"
  exit 1
fi

"${workdir}/install.sh" "${prefix}" >/dev/null
echo "bats-core ${tag} (${sha}) installed to ${prefix}"
"${prefix}/bin/bats" --version

if [[ -n "${GITHUB_PATH:-}" ]]; then
  echo "${prefix}/bin" >>"${GITHUB_PATH}"
else
  echo "Add to PATH: ${prefix}/bin"
fi
