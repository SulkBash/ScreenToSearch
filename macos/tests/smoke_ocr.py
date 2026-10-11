"""Exercise the packaged macOS OCR helper and its bundled v6 models."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

import cv2
import numpy as np


repo = Path(__file__).resolve().parents[2]
resources = repo / "macos" / "dist" / "Screen to Search.app" / "Contents" / "Resources"
helper = resources / "OcrHelper" / "OcrHelper"
assert helper.is_file(), f"Missing packaged OCR helper: {helper}"
image = np.full((180, 800, 3), 255, dtype=np.uint8)
cv2.putText(image, "CIRCLE IMAGE TEXT", (28, 105), cv2.FONT_HERSHEY_SIMPLEX,
            1.7, (0, 0, 0), 3, cv2.LINE_AA)
with tempfile.TemporaryDirectory() as directory:
    path = Path(directory) / "smoke.png"
    assert cv2.imwrite(str(path), image)
    env = os.environ.copy()
    env["SCREEN_TO_SEARCH_MODEL_DIR"] = str(resources / "models" / "v6")
    result = subprocess.run([str(helper), str(path)], capture_output=True, text=True,
                            env=env, check=True, timeout=90)
words = json.loads(result.stdout)
assert "CIRCLE" in " ".join(word["text"] for word in words).upper(), words
print("Packaged macOS OCR helper recognized:", " ".join(word["text"] for word in words))
