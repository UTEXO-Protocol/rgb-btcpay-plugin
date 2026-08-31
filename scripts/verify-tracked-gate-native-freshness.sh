#!/usr/bin/env bash
set -euo pipefail

MODE=check
ROOT=""
for argument in "$@"; do
  case "$argument" in
    --write) MODE=write ;;
    -h|--help)
      cat >&2 <<'USAGE'
usage: verify-tracked-gate-native-freshness.sh [repo-root] [--write]

Compares the gate-native source manifest against the working tree, and with --write records it.

The manifest records the crate sources and build recipe the gate native is built from. It no longer
records the binary itself: the shipped native arrives from the published RgbVerifyCffi package, not from
a blob in this repository. --write ONLY records what is on disk. It does not build anything and cannot
tell whether any binary was compiled from the sources it records, so it establishes recorded-input
consistency and nothing more. Rebuild the native with scripts/pack-rgbverify.sh --stage, then rerun this
script with --write.
USAGE
      exit 2
      ;;
    *) ROOT="$argument" ;;
  esac
done

if [ -z "$ROOT" ]; then
  ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fi
ROOT="$(cd "$ROOT" && pwd)"

python3 - "$ROOT" "$MODE" <<'PY'
import hashlib
import pathlib
import re
import subprocess
import sys

root = pathlib.Path(sys.argv[1])
mode = sys.argv[2]
manifest_path = root / "native/rgb-verify/gate-native-source-manifest.txt"
repair = "bash scripts/pack-rgbverify.sh --stage, then bash scripts/verify-tracked-gate-native-freshness.sh --write"
plugin_builder_warning = (
    "This manifest records the inputs the gate native is built from, not the binary that ships:"
    " plugin-builder.btcpayserver.org builds the plugin from the tagged source and the native arrives"
    " from the published RgbVerifyCffi package. Regenerating this manifest cannot show that any binary"
    " was compiled from the inputs it records."
)
LINE = re.compile(r"^([0-9a-f]{64})  (\S.*)$")

fixed_inputs = [
    "native/rgb-verify/Cargo.toml",
    "native/rgb-verify/Cargo.lock",
    "native/rgb-verify/build.rs",
    "native/rgb-verify/cbindgen.toml",
    "native/rgb-verify/build-native.sh",
    "scripts/pack-rgbverify.sh",
]
binary_inputs = set()
recognised_source_suffixes = {".rs"}

source_dir = root / "native/rgb-verify/src"
if not source_dir.is_dir():
    sys.exit(f"gate-native manifest: crate source directory is absent: {source_dir}")

crate_dir = root / "native/rgb-verify"
recipe_ancestors = [crate_dir, *crate_dir.parents]
discovered_recipe = []
for ancestor in recipe_ancestors:
    if root not in ancestor.parents and ancestor != root:
        continue
    cargo_dir = ancestor / ".cargo"
    if cargo_dir.is_dir():
        discovered_recipe += [p for p in cargo_dir.rglob("*") if p.is_file()]
    for pinned in ("rust-toolchain", "rust-toolchain.toml"):
        candidate = ancestor / pinned
        if candidate.is_file():
            discovered_recipe.append(candidate)

recipe_relatives = {path.relative_to(root).as_posix() for path in discovered_recipe}
candidates = sorted(
    {path.relative_to(root).as_posix() for path in source_dir.rglob("*") if path.is_file()}
    | recipe_relatives
)


def ask_git(arguments, feed=None):
    try:
        finished = subprocess.run(
            ["git", "-C", str(root), *arguments],
            input=feed,
            capture_output=True,
            check=False,
        )
    except OSError:
        return None
    if finished.returncode > 1:
        return None
    return finished.stdout.decode("utf-8", "replace")


committed_names = None
ignored_names = None
if candidates:
    committed_names = ask_git(
        ["ls-tree", "-r", "-z", "--name-only", "HEAD", "--", *candidates]
    )
    ignored_names = ask_git(
        ["check-ignore", "-z", "--stdin"],
        "".join(f"{candidate}\0" for candidate in candidates).encode("utf-8"),
    )

git_answered = candidates and committed_names is not None and ignored_names is not None
committed = set(committed_names.split("\0")) - {""} if git_answered else set()
ignored = set(ignored_names.split("\0")) - {""} if git_answered else set()


def counts_as_build_input(relative):
    if relative in committed:
        return True
    recognised = (
        relative in recipe_relatives
        or pathlib.PurePosixPath(relative).suffix in recognised_source_suffixes
    )
    return recognised and relative not in ignored


for relative in sorted(candidates):
    if relative in ignored and relative not in committed:
        print(
            "gate-native manifest: git ignores this path, so it is not a build input and was not"
            f" recorded: {relative}"
        )

relatives = sorted(
    {relative for relative in candidates if counts_as_build_input(relative)}
    | set(fixed_inputs)
)

lines = []
for relative in relatives:
    path = root / relative
    if not path.is_file():
        sys.exit(
            f"gate-native manifest: declared build input is absent: {relative}. Every input must exist"
            f" before the manifest can be computed. Repair with: {repair}"
        )
    payload = path.read_bytes()
    if relative not in binary_inputs:
        payload = payload.replace(b"\r\n", b"\n")
    digest = hashlib.sha256(payload).hexdigest()
    lines.append(f"{digest}  {relative}")

computed = "\n".join(lines) + "\n"

if mode == "write":
    manifest_path.write_text(computed, encoding="utf-8")
    print(f"recorded {len(lines)} gate-native build inputs in {manifest_path}")
    print(plugin_builder_warning)
    raise SystemExit(0)

if not manifest_path.is_file():
    sys.exit(
        f"gate-native manifest is absent: {manifest_path}. Layer S cannot pass without it, and an"
        f" absent manifest is a rejection rather than a skip. Repair with: {repair}"
    )
recorded_text = manifest_path.read_text(encoding="utf-8")
if not recorded_text.strip():
    sys.exit(
        f"gate-native manifest is empty: {manifest_path}. A check that passes on an empty record is no"
        f" check at all. Repair with: {repair}"
    )

recorded = {}
for number, line in enumerate(recorded_text.splitlines(), start=1):
    match = LINE.match(line)
    if not match:
        sys.exit(
            f"gate-native manifest line {number} is malformed: {line!r}. Every line must be"
            f" '<64 lowercase hex>  <repo-relative path>'. Repair with: {repair}"
        )
    digest, relative = match.group(1), match.group(2)
    if relative in recorded:
        sys.exit(
            f"gate-native manifest lists {relative} more than once. Repair with: {repair}"
        )
    recorded[relative] = digest

current = {line.split("  ", 1)[1]: line.split("  ", 1)[0] for line in lines}
differing = sorted(p for p in recorded.keys() & current.keys() if recorded[p] != current[p])
appeared = sorted(current.keys() - recorded.keys())
disappeared = sorted(recorded.keys() - current.keys())

if differing or appeared or disappeared:
    report = [f"gate-native manifest does not match the working tree ({manifest_path}):"]
    for relative in differing:
        report.append(f"  changed since the native was recorded: {relative}")
    for relative in appeared:
        report.append(f"  a build input appeared that the manifest does not record: {relative}")
    for relative in disappeared:
        report.append(f"  a recorded build input has disappeared: {relative}")
    report.append(
        "The gate native was recorded against different inputs than the ones on disk, so the published"
        " package may not have been built from them."
    )
    report.append(f"Repair with: {repair}")
    report.append(plugin_builder_warning)
    sys.exit("\n".join(report))

print(f"gate-native manifest matches all {len(lines)} recorded build inputs")
PY
