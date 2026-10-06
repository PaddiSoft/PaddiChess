#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$HERE/../../.." && pwd)"
TARGET="${1:-osx-arm64}"
SRC="$HERE/upstream/src"
OUT="$HERE/bin/$TARGET"
mkdir -p "$OUT"
compiler=(clang++)
flags=(-std=c++17 -O2 -DNDEBUG -DIS_64BIT -flto -ffunction-sections -fdata-sections)
# Keep source locations portable, including __FILE__ and compiler-generated
# diagnostics. Linker stripping below also removes debug paths in Zig's cached
# runtime objects, which are not rebuilt with these translation-unit flags.
flags+=(-g0 "-ffile-prefix-map=$PROJECT_ROOT=/_" "-fdebug-prefix-map=$PROJECT_ROOT=/_" -fdebug-compilation-dir=/_)
name=PaddiRules
case "$TARGET" in
  osx-arm64) flags+=(-arch arm64 -Wl,-dead_strip) ;;
  osx-x64) flags+=(-arch x86_64 -Wl,-dead_strip) ;;
  win-x64|linux-x64)
    flags+=(-s)
    if [[ "$TARGET" == linux-x64 && "$(uname -s)" == Linux ]]; then
      flags+=(-Wl,--gc-sections)
    else
      zig="${PADDI_ZIG:-$(command -v zig || true)}"
      if [[ -z "$zig" && -x "$PROJECT_ROOT/artifacts/toolchains/zig-macos-aarch64-0.13.0/zig" ]]; then
        zig="$PROJECT_ROOT/artifacts/toolchains/zig-macos-aarch64-0.13.0/zig"
      fi
      if [[ ! -x "$zig" ]]; then echo "Cross compilation requires Zig; set PADDI_ZIG to its executable." >&2; exit 1; fi
      compiler=("$zig" c++)
      if [[ "$TARGET" == win-x64 ]]; then flags+=(-target x86_64-windows-gnu -include "$HERE/windows_threads.h"); name=PaddiRules.exe
      else flags+=(-target x86_64-linux-musl); fi
      flags+=(-Wl,--gc-sections)
    fi ;;
  *) echo "Unsupported rules target: $TARGET" >&2; exit 1 ;;
esac
temporary_binary="$OUT/.$name.$$.tmp"
trap 'rm -f "$temporary_binary"' EXIT
"${compiler[@]}" "${flags[@]}" -I"$SRC" "$HERE/main.cpp" \
  "$SRC/position.cpp" "$SRC/movegen.cpp" "$SRC/attacks.cpp" "$SRC/bitboard.cpp" \
  "$HERE/standalone_support.cpp" "$SRC/nnue/features/half_ka_v2_hm.cpp" -o "$temporary_binary"
if [[ "$TARGET" == osx-* ]]; then codesign --force --sign - --identifier com.paddi.xiangqi.rules "$temporary_binary"; fi
mv -f "$temporary_binary" "$OUT/$name"
python3 - "$HERE" <<'PY'
import pathlib, sys, tempfile, zipfile
root = pathlib.Path(sys.argv[1])
with tempfile.NamedTemporaryFile(dir=root / 'bin', suffix='.tmp.zip', delete=False) as file:
    temporary = pathlib.Path(file.name)
try:
    with zipfile.ZipFile(temporary, 'w', zipfile.ZIP_DEFLATED) as package:
        for path in sorted(root.rglob('*')):
            if (path.is_file() and 'bin' not in path.relative_to(root).parts
                    and '__pycache__' not in path.relative_to(root).parts and path.name != '.DS_Store'):
                package.write(path, path.relative_to(root).as_posix())
    temporary.replace(root / 'bin' / 'PikafishRules-source.zip')
finally:
    temporary.unlink(missing_ok=True)
PY
