#!/usr/bin/env bash
set -euo pipefail

MODE=check
ROOT=""
CACHE=""
for argument in "$@"; do
  case "$argument" in
    --write) MODE=write ;;
    -h|--help)
      cat >&2 <<'USAGE'
usage: verify-gate-native-package-hashes.sh [repo-root] [package-cache] [--write]

Compares native/rgb-verify/gate-native-package-manifest.txt against the RgbVerifyCffi copies the
restore placed in the NuGet package cache.

The gate native is no longer a blob in this repository, so without this record nothing in the
repository states which bytes ship. The manifest names the package version and the sha256 of every
native the package delivers, one '<sha256>  <package-cache-relative path>' line per RID, so a version
bump cannot land without rewriting those hashes where a reviewer sees them. It also requires the
plugin's PackageReference to be an exact single-version pin, since a floating range makes any recorded
hash meaningless.

What it does NOT establish: that the recorded bytes were compiled from this repository's crate sources
-- that tie is what native/rgb-verify/gate-native-source-manifest.txt records, weakly, and neither
manifest can close it while the package is built elsewhere. --write records whatever the cache holds,
so the hashes it produces must be reviewed against the published package rather than trusted for
having been regenerated.
USAGE
      exit 2
      ;;
    *)
      if [ -z "$ROOT" ]; then
        ROOT="$argument"
      elif [ -z "$CACHE" ]; then
        CACHE="$argument"
      else
        echo "verify-gate-native-package-hashes: unexpected argument: $argument" >&2
        exit 2
      fi
      ;;
  esac
done

if [ -z "$ROOT" ]; then
  ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fi
ROOT="$(cd "$ROOT" && pwd)"

if [ -z "$CACHE" ]; then
  CACHE="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
fi

python3 - "$ROOT" "$CACHE" "$MODE" <<'PY'
import hashlib
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
cache = pathlib.Path(sys.argv[2]).expanduser()
mode = sys.argv[3]

PACKAGE_ID = "RgbVerifyCffi"
manifest_path = root / "native/rgb-verify/gate-native-package-manifest.txt"
project_path = root / "BTCPayServer.Plugins.RgbUtexo.csproj"
repair = (
    "bash scripts/verify-gate-native-package-hashes.sh --write, then review every hash it changed"
    " against the published package"
)
write_warning = (
    "These hashes record the bytes the restore delivered. Regenerating them proves nothing about the"
    " package: review each changed hash against the published RgbVerifyCffi package before committing."
)
LINE = re.compile(r"^([0-9a-f]{64})  (\S.*)$")
REFERENCE = re.compile(
    r"<PackageReference\s+Include=\"" + PACKAGE_ID + r"\"\s+Version=\"([^\"]+)\"",
)
EXACT_PIN = re.compile(r"^\[\s*([^,\[\]\s]+)\s*\]$")

if not project_path.is_file():
    sys.exit(f"gate-native package manifest: plugin project is absent: {project_path}")

declared = REFERENCE.findall(project_path.read_text(encoding="utf-8"))
if len(declared) != 1:
    sys.exit(
        f"gate-native package manifest: {project_path} declares {len(declared)} {PACKAGE_ID}"
        " PackageReference(s); exactly one is required so there is a single version to pin hashes to."
    )

pin = EXACT_PIN.match(declared[0].strip())
if not pin:
    sys.exit(
        f"gate-native package manifest: {PACKAGE_ID} is referenced as {declared[0]!r}, which is not an"
        " exact version pin. Write it as Version=\"[<version>]\": a floating range lets the gate native"
        " change without any recorded hash changing, which makes this whole record worthless."
    )
version = pin.group(1)

package_dir = cache / PACKAGE_ID.lower() / version
prefix = f"{PACKAGE_ID.lower()}/{version}/"
if not package_dir.is_dir():
    sys.exit(
        f"gate-native package manifest: {PACKAGE_ID} {version} is not in the package cache at"
        f" {package_dir}. Restore the plugin project first; an absent package is a rejection rather"
        " than a skip, because the shipped bytes were never seen."
    )

runtimes_dir = package_dir / "runtimes"
if not runtimes_dir.is_dir():
    sys.exit(
        f"gate-native package manifest: {PACKAGE_ID} {version} carries no runtimes/ directory at"
        f" {runtimes_dir}, so it delivers no gate native at all."
    )

current = {}
for path in sorted(runtimes_dir.rglob("*")):
    if not path.is_file():
        continue
    relative = path.relative_to(cache).as_posix()
    current[relative] = hashlib.sha256(path.read_bytes()).hexdigest()

if not current:
    sys.exit(
        f"gate-native package manifest: {runtimes_dir} holds no files. A check that passes on an empty"
        " package is no check at all."
    )

lines = [f"{digest}  {relative}" for relative, digest in sorted(current.items())]

if mode == "write":
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"recorded {len(lines)} {PACKAGE_ID} {version} package natives in {manifest_path}")
    print(write_warning)
    raise SystemExit(0)

if not manifest_path.is_file():
    sys.exit(
        f"gate-native package manifest is absent: {manifest_path}. Nothing then records which native"
        f" bytes ship. Repair with: {repair}"
    )

recorded_text = manifest_path.read_text(encoding="utf-8")
if not recorded_text.strip():
    sys.exit(
        f"gate-native package manifest is empty: {manifest_path}. A check that passes on an empty"
        f" record is no check at all. Repair with: {repair}"
    )

recorded = {}
for number, line in enumerate(recorded_text.splitlines(), start=1):
    match = LINE.match(line)
    if not match:
        sys.exit(
            f"gate-native package manifest line {number} is malformed: {line!r}. Every line must be"
            f" '<64 lowercase hex>  <package-cache-relative path>'. Repair with: {repair}"
        )
    digest, relative = match.group(1), match.group(2)
    if relative in recorded:
        sys.exit(
            f"gate-native package manifest lists {relative} more than once. Repair with: {repair}"
        )
    recorded[relative] = digest

foreign = sorted(relative for relative in recorded if not relative.startswith(prefix))
if foreign:
    report = [
        f"gate-native package manifest records paths outside {PACKAGE_ID} {version}, the version"
        f" {project_path.name} pins:"
    ]
    report += [f"  {relative}" for relative in foreign]
    report.append(
        "The pinned version was changed without re-recording the natives it delivers, so the shipped"
        f" bytes are unreviewed. Repair with: {repair}"
    )
    sys.exit("\n".join(report))

differing = sorted(p for p in recorded.keys() & current.keys() if recorded[p] != current[p])
appeared = sorted(current.keys() - recorded.keys())
disappeared = sorted(recorded.keys() - current.keys())

if differing or appeared or disappeared:
    report = [f"gate-native package manifest does not match the package cache ({manifest_path}):"]
    for relative in differing:
        report.append(
            f"  does not match the recorded hash: {relative}"
            f" (recorded {recorded[relative]}, cache holds {current[relative]})"
        )
    for relative in appeared:
        report.append(f"  the package delivers a native the manifest does not record: {relative}")
    for relative in disappeared:
        report.append(f"  a recorded package native is absent from the package cache: {relative}")
    report.append(
        "The gate native that would ship is not the one this repository recorded, so nothing here"
        " states which bytes a merchant would run."
    )
    report.append(f"Repair with: {repair}")
    sys.exit("\n".join(report))

print(
    f"gate-native package manifest matches all {len(lines)} {PACKAGE_ID} {version} package natives"
)
PY
