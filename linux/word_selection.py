"""Word geometry shared by the Wayland capture overlay."""

from __future__ import annotations

from dataclasses import dataclass
from math import floor


@dataclass(frozen=True)
class Character:
    text: str
    x: int
    y: int
    width: int
    height: int


@dataclass(frozen=True)
class Word:
    text: str
    x: int
    y: int
    width: int
    height: int
    line: tuple[int, int, int]
    space_before: bool | None = None
    characters: tuple[Character, ...] = ()

    def contains(self, x, y):
        return self.x - 3 <= x <= self.x + self.width + 3 and self.y - 3 <= y <= self.y + self.height + 3

    def intersects(self, rect):
        left, top, right, bottom = rect
        return self.x < right and self.x + self.width > left and self.y < bottom and self.y + self.height > top

    def offset(self, dx, dy):
        return Word(self.text, self.x + dx, self.y + dy, self.width, self.height, self.line,
                    self.space_before, tuple(Character(char.text, char.x + dx, char.y + dy,
                                                       char.width, char.height)
                                             for char in self.characters))


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
            if word.line != previous_word.line:
                result.append("\n")
            elif word.space_before is True:
                result.append(" ")
            elif word.space_before is None and not (is_cjk(previous_word.text[-1]) and is_cjk(word.text[0])):
                result.append(" ")
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


def character_at(word, x, y):
    if not word.characters:
        return 0
    return min(range(len(word.characters)), key=lambda index:
               (word.characters[index].x + word.characters[index].width / 2 - x) ** 2
               + (word.characters[index].y + word.characters[index].height / 2 - y) ** 2)


def move_character(words, position, step):
    word, char = position
    if step < 0:
        if char > 0:
            char -= 1
        elif word > 0:
            word -= 1
            char = max(1, len(words[word].characters)) - 1
    elif char + 1 < max(1, len(words[word].characters)):
        char += 1
    elif word + 1 < len(words):
        word, char = word + 1, 0
    return word, char


class WordIndex:
    """Index word boxes once so pointer movement checks only nearby targets."""
    CELL_SIZE = 96
    MAX_PADDING = 30

    def __init__(self, words):
        self.words = words
        self.cells = {}
        for index, word in enumerate(words):
            left = floor((word.x - self.MAX_PADDING) / self.CELL_SIZE)
            right = floor((word.x + word.width + self.MAX_PADDING) / self.CELL_SIZE)
            top = floor((word.y - 3) / self.CELL_SIZE)
            bottom = floor((word.y + word.height + 3) / self.CELL_SIZE)
            for y in range(top, bottom + 1):
                for x in range(left, right + 1):
                    self.cells.setdefault((x, y), []).append(index)

    def word_at(self, x, y, extend=False):
        cell = floor(x / self.CELL_SIZE), floor(y / self.CELL_SIZE)
        best = None
        for index in self.cells.get(cell, ()):
            word = self.words[index]
            dx = max(0, word.x - x, x - word.x - word.width)
            dy = max(0, word.y - y, y - word.y - word.height)
            padding = max(8, min(self.MAX_PADDING, word.height * 0.75)) if extend else 3
            if dx > padding or dy > 3:
                continue
            center_distance = (word.x + word.width / 2 - x) ** 2 + (word.y + word.height / 2 - y) ** 2
            candidate = (dx * dx + dy * dy, center_distance, index)
            if best is None or candidate < best:
                best = candidate
        return None if best is None else best[2]


def character_range_words(words, first, last):
    """Return selected word fragments, keeping their original spacing and line metadata."""
    if first > last:
        first, last = last, first
    selected = []
    for index in range(first[0], last[0] + 1):
        word = words[index]
        chars = word.characters
        start = first[1] if index == first[0] else 0
        end = last[1] if index == last[0] else len(chars) - 1
        if not chars or (start == 0 and end == len(chars) - 1):
            selected.append(word)
            continue
        chosen = chars[start:end + 1]
        left = min(char.x for char in chosen)
        top = min(char.y for char in chosen)
        right = max(char.x + char.width for char in chosen)
        bottom = max(char.y + char.height for char in chosen)
        selected.append(Word("".join(char.text for char in chosen), left, top,
                             right - left, bottom - top, word.line, word.space_before))
    return selected


def text_for_character_range(words, first, last):
    selected = character_range_words(words, first, last)
    return text_for_indices(selected, range(len(selected)))


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
