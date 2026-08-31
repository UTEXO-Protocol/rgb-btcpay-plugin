#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACT="${1:?usage: verify-artifact-native-loads.sh <publish-dir-or-btcpay> [archive-entry]}"
ENTRY="${2:-runtimes/linux-x64/native/librgbverifycffi.so}"
LIB="$(basename "$ENTRY")"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

if [ -d "$ARTIFACT" ]; then
  if [ ! -f "$ARTIFACT/$ENTRY" ]; then
    echo "verify-artifact-native-loads: $ENTRY is absent from publish directory $ARTIFACT" >&2
    exit 1
  fi
  cp "$ARTIFACT/$ENTRY" "$WORK/$LIB"
else
  python3 - "$ARTIFACT" "$ENTRY" "$WORK/$LIB" <<'PY'
import pathlib
import sys
import zipfile

archive, entry, destination = sys.argv[1:4]
try:
    with zipfile.ZipFile(archive) as bundle:
        matches = [
            info
            for info in bundle.infolist()
            if not info.is_dir()
            and info.filename.replace("\\", "/").removeprefix("./") == entry
        ]
        if len(matches) != 1:
            sys.exit(
                f"verify-artifact-native-loads: {entry} occurs {len(matches)} times in {archive};"
                " expected exactly one"
            )
        pathlib.Path(destination).write_bytes(bundle.read(matches[0]))
except (OSError, zipfile.BadZipFile) as fault:
    sys.exit(f"verify-artifact-native-loads: cannot read archive {archive}: {fault}")
PY
fi

bash "$REPO_ROOT/scripts/verify-native-loads-debian.sh" "$WORK/$LIB"
