#!/usr/bin/env bash
#
# Builds, stages and packs the RgbVerifyCffi native package.
#
#   --stage              stage natives into native/rgb-verify/runtimes/<rid>/native/
#   --pack-only          pack whatever is already staged (CI's assemble job, whose natives arrive
#                        as artifacts)
#   --require-all-rids   fail the pack unless every declared RID is present
#   --version <v>        package version, e.g. 0.11.1-rc.10-native.1 (required to pack)
#   --verify             run the pack-pipeline checks (layout, all three pack-time guards, and a
#                        Debian load of the native extracted from a package this run packed; requires a
#                        STAGED linux-x64 native to be present and fails if it is not, so run --stage
#                        first -- a bare checkout carries no native)
#
# --stage and --pack-only are independent switches, not modes: passing both stages then packs, and
# passing neither does both.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CRATE_DIR="$REPO_ROOT/native/rgb-verify"
PACKAGING_DIR="$CRATE_DIR/packaging"
PROJECT="$PACKAGING_DIR/RgbVerifyCffi.csproj"
FEED="$REPO_ROOT/local-nuget-feed"

STAGE=0
PACK=0
REQUIRE_ALL_RIDS=0
VERIFY=0
VERSION=""

while [ $# -gt 0 ]; do
  case "$1" in
    --stage) STAGE=1 ;;
    --pack-only) PACK=1 ;;
    --require-all-rids) REQUIRE_ALL_RIDS=1 ;;
    --verify) VERIFY=1 ;;
    --version) shift; VERSION="${1:-}" ;;
    -h|--help) sed -n '2,15p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "pack-rgbverify: unknown flag '$1'" >&2; exit 2 ;;
  esac
  shift
done

if [ "$VERIFY" -eq 0 ] && [ "$STAGE" -eq 0 ] && [ "$PACK" -eq 0 ]; then
  STAGE=1
  PACK=1
fi

# The RID set is read from the packaging project's GateRid items so the script, the pack-time guards
# and CI share one declaration.
declared_rids() {
  sed -n 's/.*<GateRid Include="\([^"]*\)"[[:space:]]*Lib="\([^"]*\)".*/\1 \2/p' "$PROJECT"
}

host_rid() {
  case "$(rustc -vV | sed -n 's/^host: //p')" in
    aarch64-apple-darwin)      echo osx-arm64 ;;
    x86_64-apple-darwin)       echo osx-x64 ;;
    x86_64-unknown-linux-gnu)  echo linux-x64 ;;
    aarch64-unknown-linux-gnu) echo linux-arm64 ;;
    *)                         echo unknown ;;
  esac
}

container_platform() {
  case "$1" in
    linux-x64)   echo linux/amd64 ;;
    linux-arm64) echo linux/arm64 ;;
    *)           echo "" ;;
  esac
}

# rust:1-bookworm pins the glibc floor to Debian 12, which is what BTCPay's images run. A separate
# CARGO_TARGET_DIR per RID keeps container objects out of the host build's target/release, where they
# would silently be packed as the wrong architecture. Runs as root because cmake/clang come from apt,
# then chowns the outputs back or the next host-side build fails on permissions.
build_in_container() {
  rid="$1"; lib="$2"; platform="$3"
  echo "==> building $rid in a $platform container (emulated builds take minutes, not seconds)"
  docker run --rm --platform "$platform" \
    -v "$CRATE_DIR":/w -w /w rust:1-bookworm bash -euo pipefail -c "
      apt-get update -qq && apt-get install -y -qq cmake clang >/dev/null
      export CARGO_TARGET_DIR=/w/target/$rid
      cargo build --release
      mkdir -p /w/runtimes/$rid/native
      cp /w/target/$rid/release/$lib /w/runtimes/$rid/native/$lib
      chown -R $(id -u):$(id -g) /w/target/$rid /w/runtimes/$rid
    "
}

# GNU nm cannot read Mach-O and `nm -gU` is BSD-only, so each object format is inspected on an OS
# that can read it. A library that loads but lacks an export yields EntryPointNotFound — the second
# failure mode the finding names.
assert_exports() {
  rid="$1"; lib="$2"
  path="$CRATE_DIR/runtimes/$rid/native/$lib"
  [ -f "$path" ] || { echo "pack-rgbverify: $path missing" >&2; return 1; }

  case "$lib" in
    *.dylib)
      if [ "$(uname -s)" != "Darwin" ]; then
        echo "==> skipping export check for $rid: Mach-O needs a macOS host"
        return 0
      fi
      symbols="$(nm -gU "$path")"
      object_format=macho
      ;;
    *.so)
      if [ "$(uname -s)" = "Darwin" ]; then
        symbols="$(docker run --rm --platform linux/amd64 \
          -v "$(dirname "$path")":/n rust:1-bookworm nm -D --defined-only "/n/$lib")"
      else
        symbols="$(nm -D --defined-only "$path")"
      fi
      object_format=elf
      ;;
    *)
      echo "==> skipping export check for $rid: unsupported object format $lib"
      return 0
      ;;
  esac

  printf '%s\n' "$symbols" | python3 "$REPO_ROOT/scripts/assert-native-exports.py" \
    --symbol-table - --format "$object_format" \
    || { echo "pack-rgbverify: $rid native failed the exact export check" >&2; return 1; }
}

stage() {
  host="$(host_rid)"
  declared_rids | while read -r rid lib; do
    if [ "$rid" = "$host" ]; then
      echo "==> building $rid on the host"
      bash "$CRATE_DIR/build-native.sh"
    else
      platform="$(container_platform "$rid")"
      if [ -z "$platform" ]; then
        echo "==> cannot cross-build $rid from $host — supply it as a CI artifact" >&2
        continue
      fi
      build_in_container "$rid" "$lib" "$platform"
    fi
    assert_exports "$rid" "$lib"
  done
}

pack() {
  [ -n "$VERSION" ] || { echo "pack-rgbverify: --version is required to pack" >&2; exit 2; }
  mkdir -p "$FEED"

  set -- -c Release "-p:Version=$VERSION" -o "$FEED"
  if [ "$REQUIRE_ALL_RIDS" -eq 1 ]; then
    set -- "$@" -p:RequireAllRids=true
  fi
  dotnet pack "$PROJECT" "$@"

  # Re-read the architecture of the entries that were actually PACKED, not the ones that were staged.
  # The pack-time guard cannot see a packaging mistake -- a wrong PackagePath, a stale obj/ copy -- and
  # this is RID-set-agnostic, so it never rejects an interim pack for carrying fewer RIDs. A rejected
  # package is DELETED rather than left in the feed: a consumable artifact that a guard has already
  # refused is the same defect this check exists to prevent, one directory further on.
  # NuGet normalizes the version in the file name (a two-part "2.0" is written as "2.0.0"), so name the
  # cause here rather than letting the architecture check report a missing file. pack-native.yml makes
  # the same assumption about this path.
  packed_nupkg="$FEED/RgbVerifyCffi.$VERSION.nupkg"
  if [ ! -f "$packed_nupkg" ]; then
    echo "pack-rgbverify: packed no file at $packed_nupkg. If --version was not already a normalized" >&2
    echo "  three-part NuGet version, pass the normalized form (for example 2.0.0 rather than 2.0)." >&2
    exit 1
  fi
  if ! python3 "$REPO_ROOT/scripts/native_architecture.py" --package "$packed_nupkg"; then
    rm -f "$packed_nupkg"
    echo "pack-rgbverify: removed $packed_nupkg because its packed natives failed the architecture check" >&2
    exit 1
  fi

  # A rebuilt nupkg at the same version is otherwise served from the extracted cache and the new
  # bytes never reach a consumer. Honour NUGET_PACKAGES or the eviction silently no-ops.
  rm -rf "${NUGET_PACKAGES:-$HOME/.nuget/packages}/rgbverifycffi/$VERSION"

  echo "==> packed $FEED/RgbVerifyCffi.$VERSION.nupkg"
  # Absolute, because measured on this project graph a relative folder source misses the feed:
  # ./local-nuget-feed fails with NU1101 even from the repo root, while the same restore with an
  # absolute path succeeds.
  echo "    consume with: dotnet restore <proj> --source https://api.nuget.org/v3/index.json --source $FEED --force-evaluate"
}

# P3-P5 run against a scratch copy of the packaging project with dummy natives, so they are
# deterministic on any host and cannot touch the real runtimes/ tree — a git clean there would
# irreversibly destroy the container-built linux-x64 artifact.
# The scratch tree mirrors the repo's directory layout, not just the project file, because the
# packaging project's architecture guard resolves scripts/native_architecture.py relative to its own
# location. A flat scratch/packaging/ would put that script out of reach and the guard would fail for
# a reason that has nothing to do with what P3-P5 assert.
scratch_tree() {
  scratch="$(mktemp -d)"
  mkdir -p "$scratch/native/rgb-verify/packaging" "$scratch/scripts"
  cp "$PROJECT" "$scratch/native/rgb-verify/packaging/"
  cp "$PACKAGING_DIR/_._" "$scratch/native/rgb-verify/packaging/"
  cp "$REPO_ROOT/scripts/native_architecture.py" "$scratch/scripts/"
  echo "$scratch"
}

scratch_project() {
  echo "$1/native/rgb-verify/packaging/RgbVerifyCffi.csproj"
}

# Synthetic object headers rather than the literal string "dummy": the pack now reads e_machine, so a
# text file would make P3 fail for the wrong reason, and a header carrying the RID's real machine type
# is what lets P3 exercise the guard's accept path.
plant_dummy_natives() {
  scratch="$1"; skip="${2:-}"
  declared_rids | while read -r rid lib; do
    [ "$rid" = "$skip" ] && continue
    python3 "$REPO_ROOT/scripts/native_architecture.py" \
      --synthesize "$rid" "$scratch/native/rgb-verify/runtimes/$rid/native/$lib" >/dev/null
  done
}

verify() {
  failures=0

  echo "=== P3: pack layout ==="
  scratch="$(scratch_tree)"
  plant_dummy_natives "$scratch"
  if dotnet pack "$(scratch_project "$scratch")" -c Release \
      -p:Version=0.0.0-verify -p:RequireAllRids=true -o "$scratch/out" >"$scratch/pack.log" 2>&1; then
    if python3 "$REPO_ROOT/scripts/verify_plugin_artifact.py" \
        "$scratch/out/RgbVerifyCffi.0.0.0-verify.nupkg" \
        --gate-package --provenance strict; then
      echo "P3 ok: shared contract accepted the complete gate package"
    else
      echo "P3 FAIL: shared gate-package contract rejected the nupkg"
      failures=$((failures + 1))
    fi
  else
    echo "P3 FAIL: pack failed"; sed -n '1,20p' "$scratch/pack.log"; failures=$((failures + 1))
  fi
  rm -rf "$scratch"

  echo "=== P4: pack fails without the production RID ==="
  scratch="$(scratch_tree)"
  plant_dummy_natives "$scratch" linux-x64
  if dotnet pack "$(scratch_project "$scratch")" -c Release \
      -p:Version=0.0.0-verify -o "$scratch/out" >"$scratch/pack.log" 2>&1; then
    echo "P4 FAIL: pack succeeded without runtimes/linux-x64"; failures=$((failures + 1))
  else
    grep -q "librgbverifycffi.so missing" "$scratch/pack.log" \
      && echo "P4 ok: RequireProdNative refused the pack" \
      || { echo "P4 FAIL: pack failed for the wrong reason"; sed -n '1,20p' "$scratch/pack.log"; failures=$((failures + 1)); }
  fi
  rm -rf "$scratch"

  echo "=== P5: pack fails without every declared RID ==="
  scratch="$(scratch_tree)"
  plant_dummy_natives "$scratch" linux-arm64
  if dotnet pack "$(scratch_project "$scratch")" -c Release \
      -p:Version=0.0.0-verify -p:RequireAllRids=true -o "$scratch/out" >"$scratch/pack.log" 2>&1; then
    echo "P5 FAIL: pack succeeded with a declared RID absent"; failures=$((failures + 1))
  else
    grep -q "declared RID linux-arm64" "$scratch/pack.log" \
      && echo "P5 ok: RequireAllRids refused the pack" \
      || { echo "P5 FAIL: pack failed for the wrong reason"; sed -n '1,20p' "$scratch/pack.log"; failures=$((failures + 1)); }
  fi
  rm -rf "$scratch"

  # Absence is a FAILURE, not a skip. This check previously printed "P6 SKIPPED" and let the script
  # report that all pack-pipeline checks passed on a tree carrying no trust core at all, which is worse
  # than having no check, because an engineer runs it and believes the green.
  #
  # It also loads the native extracted from a package THIS RUN PACKED, rather than from the staging
  # tree. P3 verifies a scratch package built from synthetic natives, so before this change nothing in
  # --verify ever loaded bytes that had been through a real pack.
  echo "=== P6: the real linux-x64 native, packed by this run, loads on Debian ==="
  real_native="$CRATE_DIR/runtimes/linux-x64/native/librgbverifycffi.so"
  if [ ! -f "$real_native" ]; then
    echo "P6 FAIL: $real_native is absent, so nothing was verified."
    echo "  That file is NOT tracked in git -- the shipped native comes from the published"
    echo "  RgbVerifyCffi package, so a bare checkout has nothing to pack. Stage it with:"
    echo "    bash scripts/pack-rgbverify.sh --stage"
    failures=$((failures + 1))
  else
    scratch="$(mktemp -d)"
    if dotnet pack "$PROJECT" -c Release -p:Version=0.0.0-verify-real -o "$scratch/out" \
        >"$scratch/pack.log" 2>&1; then
      packed="$scratch/out/RgbVerifyCffi.0.0.0-verify-real.nupkg"
      if bash "$REPO_ROOT/scripts/verify-artifact-native-loads.sh" \
          "$packed" runtimes/linux-x64/native/librgbverifycffi.so; then
        echo "P6 ok: the gate native inside $(basename "$packed") loaded on Debian 12"
      else
        echo "P6 FAIL: the packed gate native did not pass the Debian 12 load check; its own reason is printed above"
        failures=$((failures + 1))
      fi
    else
      echo "P6 FAIL: packing the real project failed, so no packed native could be loaded"
      sed -n '1,20p' "$scratch/pack.log"
      failures=$((failures + 1))
    fi
    rm -rf "$scratch"
  fi

  [ "$failures" -eq 0 ] || { echo "pack-rgbverify: $failures verification check(s) failed" >&2; exit 1; }
  echo "=== all pack-pipeline checks passed ==="
}

[ "$STAGE" -eq 1 ] && stage
[ "$PACK" -eq 1 ] && pack
[ "$VERIFY" -eq 1 ] && verify
exit 0
