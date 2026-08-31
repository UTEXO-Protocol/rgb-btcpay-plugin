#!/usr/bin/env bash
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE_TREE="${1:?usage: verify-gate-native-controls.sh <publish-dir> [package-cache]}"
PACKAGE_CACHE="${2:-$HOME/.nuget/packages}"
ENTRY="runtimes/linux-x64/native/librgbverifycffi.so"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
TREE="$WORK/tree"
EXPECTED="$WORK/expected.so"
ARCHIVE="$WORK/row.btcpay"

cp -R "$SOURCE_TREE" "$TREE"
if [ ! -f "$TREE/$ENTRY" ]; then
  echo "verify-gate-native-controls: $ENTRY absent from $SOURCE_TREE; there is no honest row to build on" >&2
  exit 1
fi
cp "$TREE/$ENTRY" "$EXPECTED"

failures=0
PROVENANCE_STATUS=0
PROVENANCE_OUTPUT=""
LOAD_STATUS=0
LOAD_OUTPUT=""

pack_row() {
  rm -f "$ARCHIVE"
  ( cd "$TREE" && zip -r -0 -q "$ARCHIVE" . )
}

pack_row_deflated() {
  rm -f "$ARCHIVE"
  ( cd "$TREE" && zip -r -q "$ARCHIVE" . )
}

run_gate() {
  PROVENANCE_OUTPUT="$(python3 "$REPO_ROOT/scripts/verify_plugin_artifact.py" "$ARCHIVE" \
    --provenance pre-package \
    --package-cache "$PACKAGE_CACHE" \
    --gate-native-source "linux-x64=$EXPECTED" 2>&1)"
  PROVENANCE_STATUS=$?
  LOAD_OUTPUT="$(bash "$REPO_ROOT/scripts/verify-artifact-native-loads.sh" "$ARCHIVE" 2>&1)"
  LOAD_STATUS=$?
}

# The row that matters most once the native comes from a package: strict mode roots the comparison in
# the RgbVerifyCffi package-cache copy rather than in a reference copied out of the publish tree, so a
# native substituted anywhere between the cache and the artifact is rejected.
run_gate_strict() {
  PROVENANCE_OUTPUT="$(python3 "$REPO_ROOT/scripts/verify_plugin_artifact.py" "$ARCHIVE" \
    --provenance strict \
    --package-cache "$PACKAGE_CACHE" 2>&1)"
  PROVENANCE_STATUS=$?
}

restore_entry() {
  mkdir -p "$(dirname "$TREE/$ENTRY")"
  cp "$EXPECTED" "$TREE/$ENTRY"
  if ! cmp -s "$EXPECTED" "$TREE/$ENTRY"; then
    echo "  RESTORE FAILED: $ENTRY does not match the reference bytes" >&2
    failures=$((failures + 1))
  fi
}

expect_provenance_pass() {
  if [ "$PROVENANCE_STATUS" -ne 0 ]; then
    echo "  FAIL: expected the artifact gate to accept, got exit $PROVENANCE_STATUS" >&2
    echo "$PROVENANCE_OUTPUT" | sed 's/^/    /' >&2
    failures=$((failures + 1))
  else
    echo "  provenance: PASS"
  fi
}

expect_provenance_fail() {
  local needle="$1"
  if [ "$PROVENANCE_STATUS" -eq 0 ]; then
    echo "  FAIL: the artifact gate ACCEPTED a deficient gate native" >&2
    failures=$((failures + 1))
  elif ! grep -Fq "$needle" <<<"$PROVENANCE_OUTPUT"; then
    echo "  FAIL: the artifact gate rejected for the wrong reason; expected to contain: $needle" >&2
    echo "$PROVENANCE_OUTPUT" | sed 's/^/    /' >&2
    failures=$((failures + 1))
  else
    echo "  provenance: FAIL as required -- $needle"
  fi
}

expect_load_pass() {
  if [ "$LOAD_STATUS" -ne 0 ] || ! grep -Fq "with all five exports" <<<"$LOAD_OUTPUT"; then
    echo "  FAIL: expected the native to load with all five exports, got exit $LOAD_STATUS" >&2
    echo "$LOAD_OUTPUT" | sed 's/^/    /' >&2
    failures=$((failures + 1))
  else
    echo "  loadability: PASS"
  fi
}

expect_load_fail() {
  if [ "$LOAD_STATUS" -eq 0 ] || grep -Fq "with all five exports" <<<"$LOAD_OUTPUT"; then
    echo "  FAIL: the native LOADED when it should not have" >&2
    echo "$LOAD_OUTPUT" | sed 's/^/    /' >&2
    failures=$((failures + 1))
  else
    echo "  loadability: FAIL as required -- $(tail -1 <<<"$LOAD_OUTPUT")"
  fi
}

echo "=== row: honest artifact ==="
pack_row
run_gate
expect_provenance_pass
expect_load_pass

echo "=== row: gate native missing ==="
rm -f "$TREE/$ENTRY"
pack_row
run_gate
expect_provenance_fail "missing required artifact path: $ENTRY"
expect_load_fail
restore_entry

echo "=== row: wrong architecture (e_machine aarch64 in an x86-64 ELF) ==="
printf '\xb7\x00' | dd of="$TREE/$ENTRY" bs=1 seek=18 conv=notrunc status=none
pack_row
run_gate
expect_provenance_fail "is not byte-identical to the build output it must come from"
expect_load_fail
restore_entry

echo "=== row: garbage bytes ==="
printf 'junk' > "$TREE/$ENTRY"
pack_row
run_gate
expect_provenance_fail "is not byte-identical to the build output it must come from"
expect_load_fail
restore_entry

echo "=== row: altered bytes, the stale surrogate -- loads but is not the build output ==="
printf 'X' | dd of="$TREE/$ENTRY" bs=1 seek=22000000 conv=notrunc status=none
pack_row
run_gate
expect_provenance_fail "is not byte-identical to the build output it must come from"
expect_load_pass
restore_entry

echo "=== row: property preserving -- deflate rezip plus a benign extra file ==="
printf 'not part of the contract\n' > "$TREE/gate-native-controls-note.txt"
pack_row_deflated
run_gate
expect_provenance_pass
expect_load_pass
rm -f "$TREE/gate-native-controls-note.txt"

SCRATCH_ROOT="$WORK/scratch-root"
FRESHNESS="$REPO_ROOT/scripts/verify-tracked-gate-native-freshness.sh"
mkdir -p "$SCRATCH_ROOT/native/rgb-verify" "$SCRATCH_ROOT/scripts"
cp -R "$REPO_ROOT/native/rgb-verify/src" "$SCRATCH_ROOT/native/rgb-verify/src"
for relative in Cargo.toml Cargo.lock build.rs cbindgen.toml build-native.sh; do
  cp "$REPO_ROOT/native/rgb-verify/$relative" "$SCRATCH_ROOT/native/rgb-verify/$relative"
done
cp "$REPO_ROOT/scripts/pack-rgbverify.sh" "$SCRATCH_ROOT/scripts/"
cp "$REPO_ROOT/BTCPayServer.Plugins.RgbUtexo.csproj" "$SCRATCH_ROOT/"
cp "$REPO_ROOT/native/rgb-verify/gate-native-package-manifest.txt" "$SCRATCH_ROOT/native/rgb-verify/"
PACKAGE_HASHES="$REPO_ROOT/scripts/verify-gate-native-package-hashes.sh"
PACKAGE_MANIFEST="$SCRATCH_ROOT/native/rgb-verify/gate-native-package-manifest.txt"

echo "=== row: source manifest matches the scratch tree it was written from ==="
if bash "$FRESHNESS" "$SCRATCH_ROOT" --write >/dev/null 2>&1 \
  && bash "$FRESHNESS" "$SCRATCH_ROOT" >/dev/null 2>&1; then
  echo "  freshness: PASS"
else
  echo "  FAIL: freshness rejected an unmutated scratch tree it had just recorded" >&2
  failures=$((failures + 1))
fi

echo "=== row: source manifest rejects an edited input, naming it ==="
printf '\n' >> "$SCRATCH_ROOT/native/rgb-verify/src/lib.rs"
FRESHNESS_OUTPUT="$(bash "$FRESHNESS" "$SCRATCH_ROOT" 2>&1)"
FRESHNESS_STATUS=$?
if [ "$FRESHNESS_STATUS" -eq 0 ]; then
  echo "  FAIL: freshness ACCEPTED an edited build input" >&2
  failures=$((failures + 1))
elif ! grep -Fq "native/rgb-verify/src/lib.rs" <<<"$FRESHNESS_OUTPUT"; then
  echo "  FAIL: freshness rejected without naming the edited file" >&2
  echo "$FRESHNESS_OUTPUT" | sed 's/^/    /' >&2
  failures=$((failures + 1))
else
  echo "  freshness: FAIL as required, naming native/rgb-verify/src/lib.rs"
fi

echo "=== row: package manifest matches the natives the restore delivered ==="
PACKAGE_OUTPUT="$(bash "$PACKAGE_HASHES" "$REPO_ROOT" "$PACKAGE_CACHE" 2>&1)"
PACKAGE_STATUS=$?
if [ "$PACKAGE_STATUS" -eq 0 ]; then
  echo "  package hashes: PASS"
else
  echo "  FAIL: the recorded package-native hashes do not match the package cache" >&2
  echo "$PACKAGE_OUTPUT" | sed 's/^/    /' >&2
  failures=$((failures + 1))
fi

echo "=== row: package manifest rejects an altered recorded hash, naming the path ==="
sed 's#^[0-9a-f]\{64\}\(  .*linux-x64.*\)$#0000000000000000000000000000000000000000000000000000000000000000\1#' \
  "$PACKAGE_MANIFEST" > "$PACKAGE_MANIFEST.mutated"
mv "$PACKAGE_MANIFEST.mutated" "$PACKAGE_MANIFEST"
PACKAGE_OUTPUT="$(bash "$PACKAGE_HASHES" "$SCRATCH_ROOT" "$PACKAGE_CACHE" 2>&1)"
PACKAGE_STATUS=$?
if [ "$PACKAGE_STATUS" -eq 0 ]; then
  echo "  FAIL: the package-hash check ACCEPTED a native it had not recorded" >&2
  failures=$((failures + 1))
elif ! grep -Fq "does not match the recorded hash" <<<"$PACKAGE_OUTPUT"; then
  echo "  FAIL: the package-hash check rejected for the wrong reason" >&2
  echo "$PACKAGE_OUTPUT" | sed 's/^/    /' >&2
  failures=$((failures + 1))
else
  echo "  package hashes: FAIL as required -- does not match the recorded hash"
fi

echo "=== row: strict provenance accepts the honest package-delivered artifact ==="
restore_entry
pack_row
run_gate_strict
expect_provenance_pass

echo "=== row: strict provenance rejects a substituted native that still loads ==="
printf 'X' | dd of="$TREE/$ENTRY" bs=1 seek=22000000 conv=notrunc status=none
pack_row
run_gate_strict
expect_provenance_fail "is not byte-identical to the RgbVerifyCffi package-cache copy"
restore_entry

if [ "$failures" -ne 0 ]; then
  echo "verify-gate-native-controls: $failures control row(s) did not behave as required" >&2
  exit 1
fi
echo "=== every gate-native control row behaved as required ==="
