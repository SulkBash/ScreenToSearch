"""PP-OCRv6 text and word geometry for the Linux app."""

from functools import lru_cache
from pathlib import Path

from word_selection import Word, text_for_indices


def model_dir():
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


@lru_cache(maxsize=1)
def engine():
    from rapidocr import OCRVersion, RapidOCR

    root = model_dir()
    if not available():
        raise RuntimeError("Install rapidocr and onnxruntime, and keep the PP-OCRv6 models with the app.")
    return RapidOCR(params={
        "Global.use_cls": False,
        "Global.return_word_box": True,
        "Det.ocr_version": OCRVersion.PPOCRV6,
        "Det.model_path": str(root / "PP-OCRv6_det_small.onnx"),
        "Rec.ocr_version": OCRVersion.PPOCRV6,
        "Rec.model_path": str(root / "PP-OCRv6_rec_small.onnx"),
        "Rec.rec_keys_path": str(root / "ppocrv6_small_dict.txt"),
    })


def _bounds(polygon):
    left = round(min(point[0] for point in polygon))
    top = round(min(point[1] for point in polygon))
    right = round(max(point[0] for point in polygon))
    bottom = round(max(point[1] for point in polygon))
    return left, top, max(1, right - left), max(1, bottom - top)


def recognize_words(image):
    result = engine()(image, use_cls=False, return_word_box=True)
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
