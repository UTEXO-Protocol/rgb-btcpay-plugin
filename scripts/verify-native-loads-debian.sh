#!/usr/bin/env bash
#
# Loads the linux-x64 gate native named on the command line inside Debian 12 and resolves every
# required export. The path is required: the native is no longer a blob in this repository, so there
# is no in-repo default that could be silently stale -- callers name the copy they mean, whether that
# is a freshly staged build output or the RgbVerifyCffi package-cache copy.
#
# This is what catches a glibc-floor mistake at pack time instead of at a merchant's startup: a
# native linked against a newer glibc than the deployment target fails to dlopen there. Every
# pipeline that builds this native does so in rust:1-bookworm, and this is the check that proves
# the floor held. ctypes needs no .NET, so the check runs in seconds.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NATIVE="${1:?usage: verify-native-loads-debian.sh <native-library-path>}"

[ -f "$NATIVE" ] || { echo "verify-native-loads-debian: $NATIVE not found" >&2; exit 1; }

if ! docker version >/dev/null 2>&1; then
  echo "verify-native-loads-debian: docker is not usable, so loadability on Debian 12 was never judged." >&2
  echo "No load was attempted, so this says nothing about $NATIVE." >&2
  echo "Start docker and re-run, or let CI perform the check." >&2
  exit 1
fi

DIR="$(cd "$(dirname "$NATIVE")" && pwd)"
LIB="$(basename "$NATIVE")"

docker run --rm --platform linux/amd64 \
  -v "$DIR":/n:ro \
  -v "$REPO_ROOT/scripts":/s:ro \
  python:3-slim-bookworm \
  python3 /s/assert-native-exports.py --load "/n/$LIB"
