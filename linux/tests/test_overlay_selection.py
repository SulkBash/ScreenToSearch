"""Exercise the GTK overlay's selection events without requiring a desktop session."""

import sys
import types
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from word_selection import Character, Word, WordIndex


def load_overlay():
    gi = types.ModuleType("gi")
    gi.require_foreign = lambda *_: None
    gi.require_version = lambda *_: None
    repository = types.ModuleType("gi.repository")
    keys = {name: index + 10 for index, name in enumerate((
        "Escape", "z", "Z", "Delete", "BackSpace", "a", "t", "r", "l", "Left", "Up",
        "Right", "Down", "Return", "space"))}
    repository.Gdk = types.SimpleNamespace(
        ModifierType=types.SimpleNamespace(BUTTON1_MASK=1, SHIFT_MASK=2, CONTROL_MASK=4),
        **{"KEY_" + name: value for name, value in keys.items()})
    repository.Gtk = types.SimpleNamespace(Window=type("Window", (), {}))
    repository.GdkPixbuf = repository.Gio = repository.GLib = types.SimpleNamespace()
    module = types.ModuleType("overlay_selection_test")
    path = Path(__file__).resolve().parents[1] / "wayland_app.py"
    with patch.dict(sys.modules, {"gi": gi, "gi.repository": repository,
                                 "cairo": types.ModuleType("cairo")}):
        exec(compile(path.read_text(encoding="utf-8"), str(path), "exec"), module.__dict__)
    return module


class OverlaySelectionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.module = load_overlay()

    def setUp(self):
        def word(text, x, space):
            chars = tuple(Character(char, x + index * 10, 10, 8, 12)
                          for index, char in enumerate(text))
            return Word(text, x, 10, len(text) * 10 - 2, 12, (0, 0, 0), space, chars)
        self.overlay = object.__new__(self.module.CaptureOverlay)
        overlay = self.overlay
        overlay.words = [word("one", 10, None), word("two", 50, True)]
        overlay.word_index = WordIndex(overlay.words)
        overlay.mode = "smart"
        overlay.selected = set()
        overlay.character_range = None
        overlay.selection_text = ""
        overlay.rect = overlay.polygon = None
        overlay.history = []
        overlay.focused_word = -1
        overlay.focused_character = 0
        overlay.keyboard_anchor = None
        overlay.start_word = overlay.start_point = None
        overlay.start_character = 0
        overlay.before_drag = None
        overlay.actions = Mock()
        overlay.status = Mock()
        overlay.surface = Mock()
        overlay.image_point = lambda x, y: (x, y)
        overlay.commit = Mock()

    def gesture(self, first, last):
        event = lambda point: types.SimpleNamespace(button=1, x=point[0], y=point[1], state=1)
        self.overlay.on_press(None, event(first))
        self.overlay.on_motion(None, event(last))
        self.overlay.on_release(None, event(last))
        return self.overlay.selection_text

    def test_smart_drag_selects_whole_words_in_both_directions(self):
        self.assertEqual(self.gesture((25, 15), (65, 15)), "one two")
        self.assertEqual(self.gesture((65, 15), (25, 15)), "one two")

    def test_text_mode_selects_characters_and_extends_with_keyboard(self):
        self.overlay.mode = "text"
        self.assertEqual(self.gesture((25, 15), (55, 15)), "ne t")
        self.assertEqual(self.gesture((25, 15), (25, 15)), "n")
        event = types.SimpleNamespace(keyval=self.module.Gdk.KEY_Right,
                                      state=self.module.Gdk.ModifierType.SHIFT_MASK)
        self.overlay.on_key(None, event)
        self.assertEqual(self.overlay.selection_text, "ne")


if __name__ == "__main__":
    unittest.main()
