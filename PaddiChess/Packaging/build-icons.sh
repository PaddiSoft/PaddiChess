#!/usr/bin/env bash
set -euo pipefail

PACKAGING="$(cd "$(dirname "$0")" && pwd)"
SOURCE="${1:-$PACKAGING/../Assets/Brand/Logo.png}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/Paddi.iconset" "$WORK/ico"

cp "$SOURCE" "$PACKAGING/Paddi.png"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$SOURCE" --out "$WORK/Paddi.iconset/icon_${size}x${size}.png" >/dev/null
  doubled=$((size * 2))
  sips -z "$doubled" "$doubled" "$SOURCE" --out "$WORK/Paddi.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$WORK/Paddi.iconset" -o "$PACKAGING/Paddi.icns"

for size in 16 32 48 64 128 256; do
  sips -z "$size" "$size" "$SOURCE" --out "$WORK/ico/$size.png" >/dev/null
done
swift "$PACKAGING/MakeIcon.swift" "$PACKAGING/Paddi.ico" \
  "$WORK/ico/16.png" "$WORK/ico/32.png" "$WORK/ico/48.png" \
  "$WORK/ico/64.png" "$WORK/ico/128.png" "$WORK/ico/256.png"
echo "Paddi app icons generated from $SOURCE"
