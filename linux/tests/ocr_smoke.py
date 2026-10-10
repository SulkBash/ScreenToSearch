"""Exercise the packaged PP-OCRv6 files through RapidOCR on Linux."""

from pathlib import Path
import sys

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from v6_ocr import recognize_text


image = np.full((180, 800, 3), 255, dtype=np.uint8)
cv2.putText(image, "CIRCLE IMAGE TEXT", (28, 105), cv2.FONT_HERSHEY_SIMPLEX,
            1.7, (0, 0, 0), 3, cv2.LINE_AA)
text = recognize_text(image)
assert "CIRCLE" in text.upper(), f"PP-OCRv6 did not recognize smoke image: {text!r}"
print(text)
