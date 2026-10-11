"""Exercise the capture overlay as a Wayland client under headless Mutter."""

import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from wayland_app import CaptureOverlay
from gi.repository import Gdk, GdkPixbuf, Gtk


assert os.environ.get("GDK_BACKEND") == "wayland"
display = Gdk.Display.get_default()
assert display is not None and display.get_name().startswith("wayland"), display


class Host:
    def overlay_closed(self, _overlay):
        pass


pixbuf = GdkPixbuf.Pixbuf.new(GdkPixbuf.Colorspace.RGB, False, 8, 320, 200)
pixbuf.fill(0xffffffff)
overlay = CaptureOverlay(Host(), pixbuf, [], 0)
overlay.show_all()
while Gtk.events_pending():
    Gtk.main_iteration_do(False)
assert overlay.get_realized()
overlay.rect = (20, 20, 200, 120)
assert overlay.crop_png().startswith(b"\x89PNG")
overlay.destroy()
