#!/bin/bash
set -euo pipefail
NATIVE_ROOT="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$NATIVE_ROOT/bin"
# Swift permits top-level executable statements in main.swift when compiling
# multiple files. Keep the checked-in bridge filename and stage only a symlink.
BUILD_STAGE="$(mktemp -d "$NATIVE_ROOT/bin/.bridge-build.XXXXXX")"
trap 'rm -rf "$BUILD_STAGE"' EXIT
ln -s "$NATIVE_ROOT/PaddiBridge.swift" "$BUILD_STAGE/main.swift"
xcrun swiftc -O -target arm64-apple-macos12.0 "$BUILD_STAGE/main.swift" "$NATIVE_ROOT/CaptureGeometry.swift" -o "$NATIVE_ROOT/bin/PaddiBridge-arm64"
xcrun swiftc -O -target x86_64-apple-macos12.0 "$BUILD_STAGE/main.swift" "$NATIVE_ROOT/CaptureGeometry.swift" -o "$NATIVE_ROOT/bin/PaddiBridge-x64"
xcrun lipo -create "$NATIVE_ROOT/bin/PaddiBridge-arm64" "$NATIVE_ROOT/bin/PaddiBridge-x64" -output "$NATIVE_ROOT/bin/PaddiBridge"
chmod +x "$NATIVE_ROOT/bin/PaddiBridge"
codesign --force --sign - "$NATIVE_ROOT/bin/PaddiBridge"
