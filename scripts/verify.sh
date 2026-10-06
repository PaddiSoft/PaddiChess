#!/usr/bin/env bash
# Full local/CI gate. Does not launch a GUI, send native input, package or sign releases.
set -euo pipefail

if [[ $# -ne 0 ]]; then
  echo 'Usage: bash scripts/verify.sh (always runs the complete test project)' >&2
  exit 2
fi
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
for tool in dotnet python3 clang++; do
  command -v "$tool" >/dev/null || { echo "Missing required tool: $tool" >&2; exit 1; }
done
case "$(uname -s):$(uname -m)" in
  Darwin:arm64|Darwin:x86_64)
    command -v xcrun >/dev/null || { echo 'Xcode Command Line Tools are required.' >&2; exit 1; }
    engine_binary='Pikafish.2026-09-25/Pikafish-MacOS-universal'
    ;;
  Linux:x86_64)
    engine_binary='Pikafish.2026-09-25/Pikafish-Linux-x86-64-universal'
    ;;
  *)
    echo 'This verification entry point currently supports macOS arm64/x64 and Linux x64.' >&2
    echo 'Windows requires a separately built PaddiRules.exe; see docs/architecture.md.' >&2
    exit 1
    ;;
esac

# Publishing selects the target binary. Check all supported release assets if a source checkout
# omitted them: several engine integration tests deliberately allow non-engine hosts.
python3 - <<'PY'
from pathlib import Path
import hashlib

assets = {
    'Pikafish.2026-09-25/Pikafish-MacOS-universal': 1024 * 1024,
    'Pikafish.2026-09-25/Pikafish-Linux-x86-64-universal': 1024 * 1024,
    'Pikafish.2026-09-25/Pikafish-Windows-x86-64-universal.exe': 1024 * 1024,
    'Pikafish.2026-09-25/pikafish.nnue': 1024 * 1024,
    'PaddiChess/Assets/Ocr/ch_PP-OCRv5_rec_mobile.onnx': 1024 * 1024,
    'PaddiChess/Assets/Recognition/xiangqi-nano-v3.onnx': 1024 * 1024,
    'PaddiChess.Tests/Fixtures/web-default-opening.png': 1024,
    'PaddiChess.Tests/Fixtures/jj-wechat-midgame-1.png': 1024,
}
missing = [str(path) for name, minimum in assets.items()
           if not (path := Path(name)).is_file() or path.stat().st_size < minimum]
if missing:
    raise SystemExit('Missing/truncated resources (including possible Git LFS pointers):\n  ' +
                     '\n  '.join(missing) +
                     '\nRestore the matching release assets before verification; no tests have run.')
model = Path('PaddiChess/Assets/Ocr/ch_PP-OCRv5_rec_mobile.onnx')
if hashlib.sha256(model.read_bytes()).hexdigest() != '5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5':
    raise SystemExit('OCR model checksum differs from Assets/Ocr/SOURCE.md; review the model before verification.')
classifier = Path('PaddiChess/Assets/Recognition/xiangqi-nano-v3.onnx')
if hashlib.sha256(classifier.read_bytes()).hexdigest() != 'da66ba9809f15127f8ae729b1755e42ee61c100c4f9979ce0ef13602ac471298':
    raise SystemExit('Board classifier checksum differs from Assets/Recognition/SOURCE.md.')
PY
chmod +x "$engine_binary"

run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
results="$repo_root/artifacts/verification/$run_id"
mkdir -p "$results"
dotnet --info > "$results/dotnet-info.txt"
printf 'Verification evidence: %s\n' "$results"
dotnet restore PaddiChess.slnx --locked-mode 2>&1 | tee "$results/restore.log"
dotnet build PaddiChess.slnx --configuration Release --no-restore -warnaserror 2>&1 | tee "$results/build.log"
dotnet test PaddiChess.Tests/PaddiChess.Tests.csproj --configuration Release --no-build --no-restore \
  --logger 'trx;LogFileName=verification.trx' --results-directory "$results" 2>&1 | tee "$results/test.log"
printf 'Verification completed. Review platform-specific skips in %s/verification.trx\n' "$results"
