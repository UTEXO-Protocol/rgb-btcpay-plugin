#!/usr/bin/env bash
set -euo pipefail

CRATE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$CRATE_DIR"

HOST_TRIPLE="$(rustc -vV | sed -n 's/^host: //p')"

case "$HOST_TRIPLE" in
  aarch64-apple-darwin) RID="osx-arm64"; LIB="librgbverifycffi.dylib" ;;
  x86_64-apple-darwin)  RID="osx-x64";   LIB="librgbverifycffi.dylib" ;;
  x86_64-unknown-linux-gnu) RID="linux-x64"; LIB="librgbverifycffi.so" ;;
  aarch64-unknown-linux-gnu) RID="linux-arm64"; LIB="librgbverifycffi.so" ;;
  x86_64-pc-windows-msvc) RID="win-x64"; LIB="rgbverifycffi.dll" ;;
  *) echo "unsupported host triple: $HOST_TRIPLE" >&2; exit 1 ;;
esac

cargo build --release --locked

DEST="runtimes/$RID/native"
mkdir -p "$DEST"
cp "target/release/$LIB" "$DEST/$LIB"
echo "staged $DEST/$LIB"
