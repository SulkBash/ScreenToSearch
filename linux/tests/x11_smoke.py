"""Exercise the X11 hold shortcut, full-screen capture, and shared overlay."""

from pathlib import Path
import os
import socket
import sys
import tempfile
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from Xlib import X, XK, display
from Xlib.ext import xtest
from gi.repository import GdkPixbuf, Gtk

from desktop_backends import X11Shortcut, capture_fullscreen, runtime_paths, send_command
from wayland_app import CaptureOverlay, WaylandHost


class Host:
    def overlay_closed(self, _overlay):
        pass


path, owned = capture_fullscreen()
try:
    pixbuf = GdkPixbuf.Pixbuf.new_from_file(str(path))
    assert pixbuf.get_width() > 0 and pixbuf.get_height() > 0
    overlay = CaptureOverlay(Host(), pixbuf, [], None)
    overlay.show_all()
    while Gtk.events_pending():
        Gtk.main_iteration_do(False)
    assert overlay.get_realized()
    overlay.destroy()
finally:
    if owned:
        path.unlink(missing_ok=True)

events = []
shortcut = X11Shortcut(lambda: events.append("press"), lambda: events.append("release"))
shortcut.start()
sender = display.Display()
code = sender.keysym_to_keycode(XK.string_to_keysym("F8"))
for kind in (X.KeyPress, X.KeyRelease):
    xtest.fake_input(sender, kind, code)
    sender.sync()
    deadline = time.monotonic() + 3
    while len(events) < (1 if kind == X.KeyPress else 2) and time.monotonic() < deadline:
        while Gtk.events_pending():
            Gtk.main_iteration_do(False)
        time.sleep(0.01)
assert events == ["press", "release"], events
stopped = time.monotonic()
shortcut.stop()
assert time.monotonic() - stopped < 0.5, "Idle X11 listener did not wake promptly to stop"
# Restarting the same listener must clear its stop signal and release its old descriptors.
shortcut.start()
for kind in (X.KeyPress, X.KeyRelease):
    xtest.fake_input(sender, kind, code)
    sender.sync()
    deadline = time.monotonic() + 3
    expected = 3 if kind == X.KeyPress else 4
    while len(events) < expected and time.monotonic() < deadline:
        while Gtk.events_pending():
            Gtk.main_iteration_do(False)
        time.sleep(0.01)
assert events == ["press", "release", "press", "release"], events
shortcut.stop()
sender.close()
with tempfile.TemporaryDirectory() as runtime:
    os.environ["XDG_RUNTIME_DIR"] = runtime
    _, socket_path = runtime_paths()
    with socket.socket(socket.AF_UNIX) as server:
        server.bind(str(socket_path))
        server.listen(1)
        send_command("press")
        with server.accept()[0] as connection:
            assert connection.recv(32) == b"press"
        send_command("release")
        with server.accept()[0] as connection:
            assert connection.recv(32) == b"release"
host = WaylandHost()
assert isinstance(host.shortcut, X11Shortcut)
host.shortcut.stop()
host.window.destroy()
print("X11 host, shortcut, full-screen capture, shared overlay, and compositor commands passed")
