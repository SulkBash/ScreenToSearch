#!/usr/bin/env python3
"""Return PP-OCRv6 word boxes as JSON for the macOS overlay."""

import json
from pathlib import Path
import sys

here = Path(__file__).resolve().parent
sys.path.insert(0, str(here))
sys.path.insert(0, str(here.parents[1] / "linux"))

from v6_ocr import recognize_words


def main():
    if len(sys.argv) != 2:
        raise SystemExit("usage: ocr_helper.py image.png")
    words = recognize_words(sys.argv[1])
    print(json.dumps([{"text": word.text, "x": word.x, "y": word.y,
                       "width": word.width, "height": word.height,
                       "line": list(word.line)} for word in words], ensure_ascii=False))


if __name__ == "__main__":
    main()
