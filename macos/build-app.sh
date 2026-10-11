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
"$app/Contents/MacOS/ScreenToSearchMac" --check
echo "$app"
