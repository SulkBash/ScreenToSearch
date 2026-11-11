"""PP-OCRv6 text and word geometry for the Linux app."""

import os
from pathlib import Path
import threading

from word_selection import Character, Word, is_cjk, text_for_indices


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


def _run_ocr(image, include_characters=False):
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
            return _engine(image, use_cls=False, return_word_box=True,
                           return_single_char_box=include_characters)
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


def _line_targets(line_text, line_words, line_index):
    """Reassemble recognized character boxes into selectable words in one OCR pass."""
    result = []
    current = []
    scores = []
    current_space = None
    offset = 0

    def flush():
        nonlocal current, scores
        if current and sum(scores) / len(scores) >= 0.3:
            left = min(char.x for char in current)
            top = min(char.y for char in current)
            right = max(char.x + char.width for char in current)
            bottom = max(char.y + char.height for char in current)
            result.append(Word("".join(char.text for char in current), left, top,
                               right - left, bottom - top, (0, 0, line_index),
                               current_space, tuple(current) if len(current) > 1 else ()))
        current = []
        scores = []

    for text, score, polygon in line_words:
        if not text or polygon is None or text.isspace():
            continue
        found = line_text.find(text, offset)
        between = line_text[offset:found] if found >= 0 else ""
        space = bool(between) and between.isspace()
        # If alignment is incomplete, keep the tokens separate rather than inventing text.
        uncertain = found < 0 or bool(between) and not between.isspace()
        if found >= 0:
            offset = found + len(text)
        is_cjk_token = len(text) == 1 and is_cjk(text)
        after_label = (len(current) > 1 and current[-1].text in (":", "=")
                       and text[0].isalnum()
                       and any(char.text.isalpha() for char in current[:-1])
                       and all(char.text.isalnum() or char.text in ("-", "_") for char in current[:-1]))
        if current and (space or uncertain or is_cjk_token or after_label or len(text) > 1):
            flush()
        if not current:
            current_space = None if not result or uncertain else space
        current.append(Character(text, *_bounds(polygon)))
        scores.append(float(score))
        # Existing word-level outputs and CJK glyphs are already indivisible targets.
        if len(text) > 1 or is_cjk_token:
            flush()
    flush()
    return result


def recognize_words(image, *, include_characters=True):
    result = _run_ocr(image, include_characters=include_characters)
    if result.boxes is None or result.txts is None:
        return []
    words = []
    for index, (box, line_text) in enumerate(zip(result.boxes, result.txts)):
        line_words = result.word_results[index] if result.word_results and index < len(result.word_results) else ()
        if line_words:
            targets = _line_targets(line_text, line_words, index)
            # Some OCR versions omit/misalign box entries; never silently copy
            # a different line or drop characters just to gain finer targets.
            if "".join("".join(word.text.split()) for word in targets) == "".join(line_text.split()):
                words.extend(targets)
                continue
        if line_text.strip():
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
    return [Word(word.text, word.x, word.y, word.width, word.height, (0, 0, index),
                 word.space_before, word.characters)
            for index, row in enumerate(rows)
            for word in sorted(row, key=lambda item: item.x)]


def recognize_text(image):
    words = recognize_words(image, include_characters=False)
    return text_for_indices(words, range(len(words)))
