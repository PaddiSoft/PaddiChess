#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/Paddi象棋/Paddi象棋.csproj"
DIST="${PADDI_DIST_DIR:-$ROOT/dist}"
PACKAGING="$ROOT/Paddi象棋/Packaging"
TARGET="${1:-all}"
# Ad-hoc signing works on a clean development machine. Release maintainers can
# explicitly supply their own installed identity; never commit a personal one.
# Reusing that identity and the bundle identifiers preserves update continuity.
SIGNING_IDENTITY="${PADDI_SIGN_IDENTITY:--}"

if [[ "$(uname -s)" == "Darwin" ]] &&
   [[ "$ROOT/Paddi象棋/Assets/Brand/Logo.png" -nt "$PACKAGING/Paddi.png" ||
      ! -f "$PACKAGING/Paddi.icns" || ! -f "$PACKAGING/Paddi.ico" ]]; then
  bash "$PACKAGING/build-icons.sh"
fi

mkdir -p "$DIST"
chmod +x "$ROOT/Pikafish.2026-09-25/Pikafish-MacOS-universal"
chmod +x "$ROOT/Pikafish.2026-09-25/Pikafish-Linux-x86-64-universal"

package_mac() (
  local rid="$1"
  local destination="$2"
  local archive="$3"
  local staging
  staging="$(mktemp -d "$DIST/.paddi-package.XXXXXX")"
  trap 'rm -rf "$staging"' EXIT
  # Test private-key access before publishing; certificate discovery alone does
  # not guarantee the keychain will allow a signing operation.
  if [[ "$SIGNING_IDENTITY" != "-" ]]; then
    cp /usr/bin/true "$staging/signing-probe"
    if ! codesign --force --identifier com.paddi.xiangqi.signing-probe --sign "$SIGNING_IDENTITY" "$staging/signing-probe"; then
      echo "签名预检失败：尚未开始编译，现有应用和安装包保持不变。" >&2
      echo "请根据上方 codesign 错误检查签名身份；脚本不会自动更换证书或修改钥匙串。" >&2
      exit 1
    fi
    rm "$staging/signing-probe"
  fi
  local app="$staging/$(basename "$destination")"
  dotnet publish "$PROJECT" -p:RestoreLockedMode=true -c Release -r "$rid" --self-contained true -o "$app/Contents/MacOS"
  mkdir -p "$app/Contents/Resources"
  # Notices are resources, not nested code bundles. Versioned NuGet directory
  # names under MacOS are otherwise interpreted as bundles by codesign --deep.
  mv "$app/Contents/MacOS/Licenses" "$app/Contents/Resources/Licenses"
  cp "$PACKAGING/Info.plist" "$app/Contents/Info.plist"
  cp "$PACKAGING/Paddi.icns" "$app/Contents/Resources/Paddi.icns"
  codesign --force --identifier com.paddi.xiangqi.bridge --sign "$SIGNING_IDENTITY" "$app/Contents/MacOS/Native/PaddiBridge"
  codesign --force --identifier com.paddi.xiangqi.rules --sign "$SIGNING_IDENTITY" "$app/Contents/MacOS/Native/PaddiRules"
  codesign --force --deep --identifier com.paddi.xiangqi --sign "$SIGNING_IDENTITY" "$app"
  codesign --verify --deep --strict "$app"
  ditto -c -k --sequesterRsrc --keepParent "$app" "$staging/$archive"
  python3 "$PACKAGING/normalize-zip-utf8.py" "$staging/$archive"
  # A signing failure must leave the previously working app and archive intact.
  if [[ -d "$destination" ]]; then mv "$destination" "$staging/previous.app"; fi
  if ! mv "$app" "$destination"; then
    if [[ -d "$staging/previous.app" ]]; then mv "$staging/previous.app" "$destination"; fi
    return 1
  fi
  mv -f "$staging/$archive" "$DIST/$archive"
)

package_windows() (
  local staging
  staging="$(mktemp -d "$DIST/.paddi-windows-package.XXXXXX")"
  trap 'rm -rf "$staging"' EXIT
  local name="Paddi象棋-Windows-x64"
  local bundle="$staging/$name"
  local destination="$DIST/$name"
  dotnet publish "$PROJECT" -p:RestoreLockedMode=true -c Release -r win-x64 --self-contained true -o "$bundle"
  # Asset selection is a project responsibility, including plain dotnet publish.
  # Do not mask a broken cross-publish by silently deleting foreign binaries.
  if [[ -f "$bundle/Engine/Pikafish-MacOS-universal" ||
        -f "$bundle/Engine/Pikafish-Linux-x86-64-universal" ||
        -f "$bundle/Native/PaddiBridge" ]]; then
    echo "Windows 发布目录包含其他平台的程序，请检查项目的 RID 资源配置。" >&2
    return 1
  fi
  cp "$PACKAGING/Windows-使用说明与推荐设置.txt" "$bundle/使用说明与推荐设置.txt"
  # Python's ZIP writer sets the UTF-8 filename flag. macOS's zip otherwise
  # stores these Chinese names without that flag, which garbles them on Windows.
  python3 - "$staging" "$name" <<'PY'
import pathlib, sys, zipfile
staging = pathlib.Path(sys.argv[1])
name = sys.argv[2]
archive = staging / (name + '.zip')
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as package:
    for path in sorted((staging / name).rglob('*')):
        if path.is_file():
            package.write(path, path.relative_to(staging).as_posix())
with zipfile.ZipFile(archive) as package:
    bad_file = package.testzip()
    if bad_file is not None:
        raise RuntimeError('ZIP integrity check failed: ' + bad_file)
print('Windows ZIP 完整性与 UTF-8 文件名编码检查通过')
PY
  # Build a fresh archive so old files cannot survive an update.
  if [[ -d "$destination" ]]; then mv "$destination" "$staging/previous"; fi
  if ! mv "$bundle" "$destination"; then
    if [[ -d "$staging/previous" ]]; then mv "$staging/previous" "$destination"; fi
    return 1
  fi
  mv -f "$staging/$name.zip" "$DIST/$name.zip"
)

package_linux() {
  dotnet publish "$PROJECT" -p:RestoreLockedMode=true -c Release -r linux-x64 --self-contained true -o "$DIST/Paddi象棋-Linux-x64"
  chmod +x "$DIST/Paddi象棋-Linux-x64/Paddi象棋"
  chmod +x "$DIST/Paddi象棋-Linux-x64/Engine/Pikafish-Linux-x86-64-universal"
  tar -C "$DIST" -czf "$DIST/Paddi象棋-Linux-x64.tar.gz" Paddi象棋-Linux-x64
}

case "$TARGET" in
  arm) package_mac osx-arm64 "$DIST/Paddi象棋.app" "Paddi象棋-macOS-AppleSilicon.zip" ;;
  intel) package_mac osx-x64 "$DIST/Paddi象棋-Intel.app" "Paddi象棋-macOS-Intel.zip" ;;
  windows) package_windows ;;
  linux) package_linux ;;
  all)
    package_mac osx-arm64 "$DIST/Paddi象棋.app" "Paddi象棋-macOS-AppleSilicon.zip"
    package_mac osx-x64 "$DIST/Paddi象棋-Intel.app" "Paddi象棋-macOS-Intel.zip"
    package_windows
    package_linux
    ;;
  *) echo "Usage: $0 [arm|intel|windows|linux|all]" >&2; exit 2 ;;
esac
