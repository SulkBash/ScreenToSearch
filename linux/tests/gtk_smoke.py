"""GTK constructor smoke check, run under xvfb on Linux CI."""

from pathlib import Path
import sys
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from wayland_app import CaptureOverlay, WaylandHost
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
