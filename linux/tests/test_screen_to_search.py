import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import screen_to_search as app
import desktop_backends
from word_selection import (Character, Word, WordIndex, bounds_for_indices, character_at,
                            character_range_words, monitor_crops, move_character,
                            text_for_character_range, text_for_indices)
import v6_ocr


class LinuxAppTests(unittest.TestCase):
    def test_resident_wayland_desktop_selection(self):
        for name, expected in (("KDE", "KDE Plasma"), ("KDE:Plasma", "KDE Plasma"),
                               ("GNOME", "GNOME"), ("ubuntu:GNOME", "GNOME"),
                               ("sway", None)):
            with self.subTest(name=name), patch.dict(os.environ, {
                "WAYLAND_DISPLAY": "wayland-0", "XDG_CURRENT_DESKTOP": name,
            }):
                self.assertEqual(app.wayland_desktop(), expected)
        with patch.dict(os.environ, {"WAYLAND_DISPLAY": "", "XDG_CURRENT_DESKTOP": "KDE"}):
            self.assertIsNone(app.wayland_desktop())

    def test_word_level_selection_and_bounds(self):
        words = [Word("Hello", 10, 20, 20, 10, (0, 0, 0)),
                 Word("world", 35, 20, 25, 10, (0, 0, 0)),
                 Word("Again", 10, 40, 30, 10, (0, 0, 1))]
        self.assertEqual(len(words), 3)
        self.assertTrue(words[0].contains(15, 25))
        self.assertEqual(text_for_indices(words, {0, 1, 2}), "Hello world\nAgain")
        self.assertEqual(bounds_for_indices(words, {1, 2}), (10, 20, 60, 50))

    def test_monitor_crops_map_negative_origin_and_scale(self):
        self.assertEqual(monitor_crops([(-1920, 0, 1920, 1080), (0, 0, 2560, 1440)],
                                       4480, 1440),
                         [(0, 0, 1920, 1080), (1920, 0, 2560, 1440)])

    def test_cjk_characters_can_be_selected_individually(self):
        words = [Word(char, 10 + index * 10, 20, 10, 12, (0, 0, 0))
                 for index, char in enumerate("搜索词")]
        self.assertEqual([word.text for word in words], ["搜", "索", "词"])
        self.assertEqual(text_for_indices(words, {0, 1, 2}), "搜索词")
        self.assertEqual(bounds_for_indices(words, {1}), (20, 20, 30, 32))

    def test_desktop_install_creates_portal_desktop_identity(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[2]) as directory, \
             patch.dict(os.environ, {"XDG_DATA_HOME": directory}):
            app.install_desktop()
            desktop = Path(directory) / "applications" / (app.APP_ID + ".desktop")
            self.assertIn("Name=Screen to Search", desktop.read_text(encoding="utf-8"))
            self.assertTrue((Path(directory) / "screen-to-search" / "launch").exists())
            self.assertTrue((Path(directory) / "screen-to-search" / "icon.svg").exists())

    def test_capture_backend_order(self):
        tools = {"grim", "slurp", "spectacle", "flameshot", "maim"}
        with patch.dict(os.environ, {"WAYLAND_DISPLAY": "wayland-0"}), \
             patch.object(app.shutil, "which", side_effect=lambda name: name if name in tools else None):
            self.assertEqual(app.available_capture_methods(), ["grim", "spectacle", "flameshot"])
        with patch.dict(os.environ, {"WAYLAND_DISPLAY": ""}), \
             patch.object(app.shutil, "which", side_effect=lambda name: name if name in tools else None):
            self.assertEqual(app.available_capture_methods(), ["maim", "flameshot", "spectacle"])

    def test_fullscreen_capture_uses_x11_tool_before_portal(self):
        class Portal:
            def has_interface(self, _name):
                return True

            def screenshot(self):
                self.fail("X11 should use direct capture")

        def run(command, **_kwargs):
            Path(command[-1]).write_bytes(b"image")
            return app.subprocess.CompletedProcess(command, 0, b"", b"")

        with patch.dict(os.environ, {"WAYLAND_DISPLAY": ""}), \
             patch.object(desktop_backends, "available_fullscreen_commands",
                          return_value=[("maim", ["maim", "{path}"])]), \
             patch.object(desktop_backends.subprocess, "run", side_effect=run):
            path, owned = desktop_backends.capture_fullscreen(Portal())
        self.assertTrue(owned)
        self.assertEqual(path.read_bytes(), b"image")
        path.unlink()

    def test_wayland_fullscreen_capture_uses_portal(self):
        class Portal:
            def has_interface(self, name):
                return name == "Screenshot"

            def screenshot(self):
                return Path("/portal/capture.png")

        with patch.dict(os.environ, {"WAYLAND_DISPLAY": "wayland-0"}), \
             patch.object(desktop_backends, "available_fullscreen_commands", return_value=[]):
            self.assertEqual(desktop_backends.capture_fullscreen(Portal()),
                             (Path("/portal/capture.png"), False))

    def test_capture_uses_slurp_geometry_without_shell(self):
        calls = []

        def run(command, **kwargs):
            calls.append(command)
            if command[0] == "slurp":
                return app.subprocess.CompletedProcess(command, 0, "10,20 30x40", "")
            Path(command[-1]).write_bytes(b"png")
            return app.subprocess.CompletedProcess(command, 0, b"", b"")

        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[2]) as directory, \
             patch.object(app, "available_capture_methods", return_value=["grim"]), \
             patch.object(app.subprocess, "run", side_effect=run):
            path = Path(directory) / "capture.png"
            self.assertTrue(app.capture_region(path))
            self.assertEqual(calls, [["slurp"], ["grim", "-g", "10,20 30x40", str(path)]])

    def test_cancelled_slurp_stops_capture(self):
        error = app.subprocess.CalledProcessError(1, ["slurp"], stderr=b"")
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[2]) as directory, \
             patch.object(app, "available_capture_methods", return_value=["grim", "flameshot"]), \
             patch.object(app.subprocess, "run", side_effect=error) as run:
            self.assertFalse(app.capture_region(Path(directory) / "capture.png"))
            self.assertEqual(run.call_count, 1)

    def test_search_and_translate_encode_selected_text(self):
        settings = app.DEFAULTS.copy()
        with patch.object(app.webbrowser, "open", return_value=True) as browser:
            app.open_text("search", "a & b", settings)
            self.assertEqual(browser.call_args.args[0], "https://www.google.com/search?q=a%20%26%20b")
            app.open_text("translate", "café", settings)
            self.assertIn("text=caf%C3%A9", browser.call_args.args[0])

    def test_v6_word_geometry(self):
        class Result:
            boxes = [[[10, 20], [60, 20], [60, 30], [10, 30]]]
            txts = ["Hello world"]
            word_results = [[("Hello", 0.9, [[10, 20], [30, 20], [30, 30], [10, 30]]),
                             ("world", 0.9, [[35, 20], [60, 20], [60, 30], [35, 30]])]]

        with patch.object(v6_ocr, "_run_ocr", return_value=Result()):
            words = v6_ocr.recognize_words(b"png")
        self.assertEqual([word.text for word in words], ["Hello", "world"])
        self.assertEqual(bounds_for_indices(words, {0, 1}), (10, 20, 60, 30))

    def test_character_boxes_form_words_without_losing_text(self):
        def targets(text):
            boxes = [(char, 0.9, [[index * 10, 0], [index * 10 + 8, 0],
                                  [index * 10 + 8, 15], [index * 10, 15]])
                     for index, char in enumerate(text) if not char.isspace()]
            return v6_ocr._line_targets(text, boxes, 0)

        for source, expected in (("one two three four five six seven eight nine ten",
                                  ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"]),
                                 ("CLABE:123456789", ["CLABE:", "123456789"]),
                                 ("account=123456789", ["account=", "123456789"]),
                                 ("order-id=12345", ["order-id=", "12345"]),
                                 ("https://example.com", ["https://example.com"]),
                                 ("12:30", ["12:30"]),
                                 ("English 中文", ["English", "中", "文"])):
            with self.subTest(source=source):
                words = targets(source)
                self.assertEqual([word.text for word in words], expected)
                self.assertEqual(text_for_indices(words, range(len(words))), source)
                self.assertTrue(all(word.characters for word in words if len(word.text) > 1))

    def test_missing_character_alignment_falls_back_to_original_line(self):
        class Result:
            boxes = [[[10, 20], [100, 20], [100, 35], [10, 35]]]
            txts = ["Hello world"]
            word_results = [[("X", 0.9, [[10, 20], [20, 20], [20, 35], [10, 35]])]]

        with patch.object(v6_ocr, "_run_ocr", return_value=Result()):
            words = v6_ocr.recognize_words(b"png")
        self.assertEqual([word.text for word in words], ["Hello world"])
        self.assertEqual(text_for_indices(words, {0}), "Hello world")

    def test_word_offset_preserves_character_geometry_and_spacing(self):
        word = Word("ab", 50, 60, 20, 10, (0, 0, 0), False,
                    (Character("a", 50, 60, 8, 10), Character("b", 60, 60, 10, 10)))
        shifted = word.offset(-30, -40)
        self.assertEqual((shifted.x, shifted.y, shifted.space_before), (20, 20, False))
        self.assertEqual([(c.x, c.y) for c in shifted.characters], [(20, 20), (30, 20)])

    def test_character_range_respects_word_spacing_and_reverse_selection(self):
        def word(text, x, space):
            chars = tuple(Character(char, x + i * 10, 10, 8, 12) for i, char in enumerate(text))
            return Word(text, x, 10, len(text) * 10 - 2, 12, (0, 0, 0), space, chars)
        words = [word("CIRCLE", 0, None), word("IMAGE", 70, True)]
        self.assertEqual(character_at(words[0], 34, 15), 3)
        self.assertEqual(text_for_character_range(words, (0, 3), (1, 2)), "CLE IMA")
        self.assertEqual(text_for_character_range(words, (1, 2), (0, 3)), "CLE IMA")
        self.assertEqual(bounds_for_indices(character_range_words(words, (0, 3), (1, 2)), {0, 1}),
                         (30, 10, 98, 22))
        self.assertEqual(move_character(words, (0, 5), 1), (1, 0))
        self.assertEqual(move_character(words, (1, 0), -1), (0, 5))

    def test_word_index_prefers_actual_target_and_handles_cell_edges(self):
        words = [Word("A", -5, -3, 12, 15, (0, 0, 0)),
                 Word("B", 90, 90, 20, 20, (0, 0, 1)),
                 Word("C", 110, 90, 12, 20, (0, 0, 1))]
        index = WordIndex(words)
        self.assertEqual(index.word_at(111, 100), 2)
        self.assertEqual(index.word_at(128, 100, extend=True), 2)
        self.assertEqual(index.word_at(-4, 0), 0)
        self.assertIsNone(index.word_at(300, 300))

    def test_clipboard_passes_png_bytes_to_wayland_tool(self):
        with patch.dict(os.environ, {"WAYLAND_DISPLAY": "wayland-0"}), \
             patch.object(app.shutil, "which", side_effect=lambda name: name if name == "wl-copy" else None), \
             patch.object(app.subprocess, "run", return_value=app.subprocess.CompletedProcess([], 0)) as run:
            app.copy_data(b"\x89PNG", "image/png")
            self.assertEqual(run.call_args.args[0], ["wl-copy", "--type", "image/png"])
            self.assertEqual(run.call_args.kwargs["input"], b"\x89PNG")


if __name__ == "__main__":
    unittest.main()
