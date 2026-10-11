#!/bin/sh
set -eu

repo=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
swift build -c release --package-path "$repo/macos"
binary=$(swift build -c release --package-path "$repo/macos" --show-bin-path)
destination=${1:-"$repo/macos/dist"}
app="$destination/Screen to Search.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources/models/v6"
cp "$binary/ScreenToSearchMac" "$app/Contents/MacOS/ScreenToSearchMac"
cp "$repo/macos/Info.plist" "$app/Contents/Info.plist"
cp "$repo/macos/Resources/ocr_helper.py" "$repo/linux/v6_ocr.py" \
   "$repo/linux/word_selection.py" "$app/Contents/Resources/"
cp "$repo/src/ScreenToSearch/models/v6/PP-OCRv6_det_small.onnx" \
   "$repo/src/ScreenToSearch/models/v6/PP-OCRv6_rec_small.onnx" \
   "$repo/src/ScreenToSearch/models/v6/ppocrv6_small_dict.txt" \
   "$app/Contents/Resources/models/v6/"
if python3 -m PyInstaller --version >/dev/null 2>&1; then
  python3 -m PyInstaller --noconfirm --clean --onedir --name OcrHelper \
    --paths "$repo/linux" --collect-all rapidocr --collect-all onnxruntime \
    --collect-all cv2 --distpath "$app/Contents/Resources" \
    --workpath "$repo/macos/.build/pyinstaller" --specpath "$repo/macos/.build" \
    "$repo/macos/Resources/ocr_helper.py"
fi
"$app/Contents/MacOS/ScreenToSearchMac" --check
echo "$app"
