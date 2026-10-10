"""GTK constructor smoke check, run under xvfb on Linux CI."""

from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from gnome_app import CaptureOverlay
from gi.repository import GdkPixbuf, Gtk


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
overlay.destroy()
