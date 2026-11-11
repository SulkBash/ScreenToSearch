"""GTK constructor smoke check, run under xvfb on Linux CI."""

from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from wayland_app import CaptureOverlay, WaylandHost
from word_selection import Character, Word
from gi.repository import Gdk, GdkPixbuf, Gtk


class Host:
    def overlay_closed(self, _overlay):
        pass


pixbuf = GdkPixbuf.Pixbuf.new(GdkPixbuf.Colorspace.RGB, False, 8, 100, 100)
pixbuf.fill(0xffffffff)
overlay = CaptureOverlay(Host(), pixbuf, [], 0)
overlay.rect = (10, 10, 80, 80)
assert overlay.crop_png().startswith(b"\x89PNG")
overlay.polygon = [(10, 10), (80, 10), (80, 80), (10, 80)]
assert overlay.crop_png().startswith(b"\x89PNG")
while Gtk.events_pending():
    Gtk.main_iteration_do(False)
def word(text, x, space):
    characters = tuple(Character(char, x + index * 10, 10, 8, 12)
                       for index, char in enumerate(text))
    return Word(text, x, 10, len(text) * 10 - 2, 12, (0, 0, 0), space, characters)

overlay.set_words([word("one", 10, None), word("two", 50, True)])
overlay.image_point = lambda x, y: (x, y)
def gesture(first, last):
    event = lambda point: SimpleNamespace(button=1, x=point[0], y=point[1],
                                           state=Gdk.ModifierType.BUTTON1_MASK)
    overlay.on_press(None, event(first))
    overlay.on_motion(None, event(last))
    overlay.on_release(None, event(last))
    return overlay.selection_text

assert gesture((25, 15), (65, 15)) == "one two"
overlay.set_mode("text")
assert gesture((25, 15), (55, 15)) == "ne t"
overlay.destroy()

# A tray host should not build the settings widgets until the user opens them.
with patch.object(WaylandHost, "make_indicator",
                  lambda host: setattr(host, "indicator", object())), \
     patch.object(WaylandHost, "start_shortcut"):
    host = WaylandHost()
    assert not host._settings_built
    assert not host.window.get_children()
    host.show_settings()
    assert host._settings_built
    assert len(host.window.get_children()) == 1
    host.window.hide()
    host.show_settings()
    assert len(host.window.get_children()) == 1
    host.window.destroy()
