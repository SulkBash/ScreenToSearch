"""PP-OCRv6 text and word geometry for the Linux app."""

import os
from pathlib import Path
import threading

from word_selection import Word, text_for_indices


def model_dir():
    override = os.environ.get("SCREEN_TO_SEARCH_MODEL_DIR")
    if override:
        return Path(override)
    here = Path(__file__).resolve().parent
    installed = here / "models" / "v6"
    source = here.parent / "src" / "ScreenToSearch" / "models" / "v6"
    return installed if installed.is_dir() else source


def available():
    try:
        import rapidocr  # noqa: F401
    except ImportError:
        return False
    root = model_dir()
    return all((root / name).is_file() for name in (
        "PP-OCRv6_det_small.onnx", "PP-OCRv6_rec_small.onnx", "ppocrv6_small_dict.txt"))


_engine = None
_engine_lock = threading.Lock()
_idle_timer = None
_generation = 0
IDLE_SECONDS = 60


def _create_engine():
    from rapidocr import OCRVersion, RapidOCR
    from onnxruntime import GraphOptimizationLevel, InferenceSession, SessionOptions
    from omegaconf import flag_override

    root = model_dir()
    if not available():
        raise RuntimeError("Install rapidocr and onnxruntime, and keep the PP-OCRv6 models with the app.")
    runtime = RapidOCR(params={
        "Global.use_cls": False,
        "Global.return_word_box": True,
        "Det.ocr_version": OCRVersion.PPOCRV6,
        "Det.model_path": str(root / "PP-OCRv6_det_small.onnx"),
        "Rec.ocr_version": OCRVersion.PPOCRV6,
        "Rec.model_path": str(root / "PP-OCRv6_rec_small.onnx"),
        "Rec.rec_keys_path": str(root / "ppocrv6_small_dict.txt"),
    })
    options = SessionOptions()
    options.enable_cpu_mem_arena = False
    options.graph_optimization_level = GraphOptimizationLevel.ORT_ENABLE_ALL
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    options.add_session_config_entry("session.inter_op.allow_spinning", "0")
    # RapidOCR supports supplied ONNX sessions; allow their opaque objects in its config.
    with flag_override(runtime.cfg, "allow_objects", True):
        for section, name in (("Det", "PP-OCRv6_det_small.onnx"),
                              ("Rec", "PP-OCRv6_rec_small.onnx")):
            runtime.cfg[section].session = InferenceSession(
                str(root / name), sess_options=options, providers=["CPUExecutionProvider"])
    return runtime


def _release_idle_engine(generation):
    global _engine, _idle_timer
    with _engine_lock:
        # A canceled timer may already be waiting on recognition's lock.
        if generation == _generation:
            _engine = None
            _idle_timer = None


def _run_ocr(image):
    global _engine, _idle_timer, _generation
    # Screen and crop OCR can arrive on different threads. Never run or unload
    # the shared engine concurrently: RapidOCR mutates its per-request state.
    with _engine_lock:
        _generation += 1
        if _idle_timer is not None:
            _idle_timer.cancel()
            _idle_timer = None
        try:
            if _engine is None:
                _engine = _create_engine()
            return _engine(image, use_cls=False, return_word_box=True)
        finally:
            if _engine is not None:
                _idle_timer = threading.Timer(IDLE_SECONDS, _release_idle_engine, (_generation,))
                _idle_timer.daemon = True  # The one-shot macOS helper must exit immediately.
                _idle_timer.start()


def _bounds(polygon):
    left = round(min(point[0] for point in polygon))
    top = round(min(point[1] for point in polygon))
    right = round(max(point[0] for point in polygon))
    bottom = round(max(point[1] for point in polygon))
    return left, top, max(1, right - left), max(1, bottom - top)


def recognize_words(image):
    result = _run_ocr(image)
    if result.boxes is None or result.txts is None:
        return []
    words = []
    for index, (box, line_text) in enumerate(zip(result.boxes, result.txts)):
        line_words = result.word_results[index] if result.word_results and index < len(result.word_results) else ()
        if line_words:
            for text, score, polygon in line_words:
                if not text or polygon is None or score < 0.3:
                    continue
                words.append(Word(text, *_bounds(polygon), (0, 0, index)))
        elif line_text.strip():
            # Some model outputs lack word alignment. Keep the line selectable.
            words.append(Word(line_text.strip(), *_bounds(box), (0, 0, index)))
    rows = []
    for word in sorted(words, key=lambda item: item.y + item.height / 2):
        for row in rows:
            anchor = row[0]
            overlap = max(0, min(anchor.y + anchor.height, word.y + word.height)
                          - max(anchor.y, word.y))
            centers = abs(anchor.y + anchor.height / 2 - word.y - word.height / 2)
            if overlap >= min(anchor.height, word.height) * 0.45 and centers <= max(anchor.height, word.height) * 0.7:
                row.append(word)
                break
        else:
            rows.append([word])
    return [Word(word.text, word.x, word.y, word.width, word.height, (0, 0, index))
            for index, row in enumerate(rows)
            for word in sorted(row, key=lambda item: item.x)]


def recognize_text(image):
    words = recognize_words(image)
    return text_for_indices(words, range(len(words)))
