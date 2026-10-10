import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import screen_to_search as app
from word_selection import Word, bounds_for_indices, monitor_crops, text_for_indices
import v6_ocr


class LinuxAppTests(unittest.TestCase):
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

    def test_gnome_install_creates_portal_desktop_identity(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[2]) as directory, \
             patch.dict(os.environ, {"XDG_DATA_HOME": directory}):
            app.install_gnome()
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

        with patch.object(v6_ocr, "engine", return_value=lambda *args, **kwargs: Result()):
            words = v6_ocr.recognize_words(b"png")
        self.assertEqual([word.text for word in words], ["Hello", "world"])
        self.assertEqual(bounds_for_indices(words, {0, 1}), (10, 20, 60, 30))

    def test_clipboard_passes_png_bytes_to_wayland_tool(self):
        with patch.dict(os.environ, {"WAYLAND_DISPLAY": "wayland-0"}), \
             patch.object(app.shutil, "which", side_effect=lambda name: name if name == "wl-copy" else None), \
             patch.object(app.subprocess, "run", return_value=app.subprocess.CompletedProcess([], 0)) as run:
            app.copy_data(b"\x89PNG", "image/png")
            self.assertEqual(run.call_args.args[0], ["wl-copy", "--type", "image/png"])
            self.assertEqual(run.call_args.kwargs["input"], b"\x89PNG")


if __name__ == "__main__":
    unittest.main()
