"""Word geometry shared by the GNOME capture overlay."""

from dataclasses import dataclass


@dataclass(frozen=True)
class Word:
    text: str
    x: int
    y: int
    width: int
    height: int
    line: tuple[int, int, int]

    def contains(self, x, y):
        return self.x - 3 <= x <= self.x + self.width + 3 and self.y - 3 <= y <= self.y + self.height + 3

    def intersects(self, rect):
        left, top, right, bottom = rect
        return self.x < right and self.x + self.width > left and self.y < bottom and self.y + self.height > top


def is_cjk(character):
    code = ord(character)
    return (0x2E80 <= code <= 0x9FFF or 0xAC00 <= code <= 0xD7AF
            or 0xF900 <= code <= 0xFAFF or 0x3040 <= code <= 0x30FF)


def text_for_indices(words, indices):
    result = []
    previous_word = None
    for index in sorted(indices):
        word = words[index]
        if previous_word is not None:
            result.append("\n" if word.line != previous_word.line else
                          "" if is_cjk(previous_word.text[-1]) and is_cjk(word.text[0]) else " ")
        result.append(word.text)
        previous_word = word
    return "".join(result)


def bounds_for_indices(words, indices):
    selected = [words[index] for index in indices]
    if not selected:
        return None
    return (min(word.x for word in selected), min(word.y for word in selected),
            max(word.x + word.width for word in selected),
            max(word.y + word.height for word in selected))


def monitor_crops(geometries, image_width, image_height):
    """Map GDK's virtual monitor layout to a composite portal screenshot."""
    if not geometries:
        return []
    left = min(x for x, _, _, _ in geometries)
    top = min(y for _, y, _, _ in geometries)
    right = max(x + width for x, _, width, _ in geometries)
    bottom = max(y + height for _, y, _, height in geometries)
    scale_x = image_width / (right - left)
    scale_y = image_height / (bottom - top)
    crops = []
    for x, y, width, height in geometries:
        x0 = max(0, round((x - left) * scale_x))
        y0 = max(0, round((y - top) * scale_y))
        x1 = min(image_width, round((x + width - left) * scale_x))
        y1 = min(image_height, round((y + height - top) * scale_y))
        crops.append((x0, y0, max(1, x1 - x0), max(1, y1 - y0)))
    return crops
