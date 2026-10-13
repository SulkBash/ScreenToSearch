"""Exercise the packaged PP-OCRv6 files through RapidOCR on Linux."""

from pathlib import Path
import sys
import time
import weakref

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from v6_ocr import recognize_text
import v6_ocr


image = np.full((180, 800, 3), 255, dtype=np.uint8)
cv2.putText(image, "CIRCLE IMAGE TEXT", (28, 105), cv2.FONT_HERSHEY_SIMPLEX,
            1.7, (0, 0, 0), 3, cv2.LINE_AA)
assert v6_ocr._engine is None, "OCR loaded before the first request"
v6_ocr.IDLE_SECONDS = 0.5  # Exercise the real timer without waiting a minute in CI.
text = recognize_text(image)
assert "CIRCLE" in text.upper(), f"PP-OCRv6 did not recognize smoke image: {text!r}"
cached = weakref.ref(v6_ocr._engine)
for section in ("Det", "Rec"):
    options = v6_ocr._engine.cfg[section].session.get_session_options()
    assert not options.enable_cpu_mem_arena
    assert options.get_session_config_entry("session.intra_op.allow_spinning") == "0"
    assert options.get_session_config_entry("session.inter_op.allow_spinning") == "0"
assert "CIRCLE" in recognize_text(image).upper()
assert v6_ocr._engine is cached(), "Warm recognition did not reuse the engine"
deadline = time.monotonic() + 10
while v6_ocr._engine is not None and time.monotonic() < deadline:
    time.sleep(0.05)
assert v6_ocr._engine is None, "Idle timer did not unload OCR"
assert cached() is None, "An extra reference retained the idle OCR engine"
assert "CIRCLE" in recognize_text(image).upper(), "OCR failed after reloading"
print(text)
print("Lazy loading, warm reuse, low-memory sessions, idle release, and reload passed")
