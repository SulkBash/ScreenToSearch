"""Full-screen capture and shortcut adapters for Linux desktops."""

import os
from pathlib import Path
import select
import shutil
import socket
import subprocess
import tempfile
import threading
import time


def runtime_paths():
    runtime = Path(os.environ.get("XDG_RUNTIME_DIR") or tempfile.gettempdir())
    basename = f"screen-to-search-{os.getuid()}"
    return runtime / (basename + ".lock"), runtime / (basename + ".sock")


def send_command(command):
    if command not in ("capture", "settings", "press", "release"):
        raise ValueError("Unknown Screen to Search command.")
    _, socket_path = runtime_paths()
    try:
        with socket.socket(socket.AF_UNIX) as client:
            client.connect(str(socket_path))
            client.sendall(command.encode("ascii"))
    except (FileNotFoundError, ConnectionRefusedError) as exc:
        raise RuntimeError("Start Screen to Search before using a compositor shortcut.") from exc


def fullscreen_commands():
    if os.environ.get("WAYLAND_DISPLAY"):
        return [("grim", ["grim", "{path}"]),
                ("spectacle", ["spectacle", "-b", "-n", "-o", "{path}"])]
    return [("maim", ["maim", "{path}"]),
            ("import", ["import", "-window", "root", "{path}"]),
            ("gnome-screenshot", ["gnome-screenshot", "-f", "{path}"]),
            ("spectacle", ["spectacle", "-b", "-n", "-o", "{path}"])]


def available_fullscreen_commands():
    return [(name, command) for name, command in fullscreen_commands() if shutil.which(name)]


def capture_fullscreen(portal=None):
    """Return (image path, owned by caller). The caller removes owned captures after OCR."""
    errors = []
    commands = available_fullscreen_commands()
    # X11 screenshot tools avoid portal dialogs. On Wayland, use the portal
    # when present, then a compositor-native tool such as grim.
    sources = (["portal"] + commands) if os.environ.get("WAYLAND_DISPLAY") else (commands + ["portal"])
    for source in sources:
        if source == "portal":
            if portal is None or not portal.has_interface("Screenshot"):
                continue
            try:
                return portal.screenshot(), False
            except Exception as exc:
                errors.append(f"portal: {exc}")
            continue
        name, command = source
        fd, filename = tempfile.mkstemp(prefix="screen-to-search-", suffix=".png")
        os.close(fd)
        path = Path(filename)
        try:
            result = subprocess.run([part.replace("{path}", filename) for part in command],
                                    capture_output=True, timeout=30, check=False)
            if result.returncode == 0 and path.stat().st_size:
                return path, True
            errors.append(f"{name}: {result.stderr.decode(errors='replace').strip() or 'capture failed'}")
        except (OSError, subprocess.TimeoutExpired) as exc:
            errors.append(f"{name}: {exc}")
        finally:
            if not path.exists() or not path.stat().st_size:
                path.unlink(missing_ok=True)
    raise RuntimeError("Full-screen capture unavailable. " + " ".join(errors))


class ExternalShortcut:
    """Compositor keybindings send press/release commands to the resident app."""

    def start(self):
        pass

    def stop(self):
        pass

    def configure(self):
        raise RuntimeError("Configure press and release bindings in your compositor; see README.md.")


class X11Shortcut:
    """Grab a function key on X11 and forward its press/release events to GTK."""

    def __init__(self, pressed, released, key="F8"):
        self.pressed = pressed
        self.released = released
        self.key = key
        self.display = None
        self.root = None
        self.keycode = None
        self.stopping = threading.Event()
        self.thread = None
        self._wake_reader = None
        self._wake_writer = None

    def start(self):
        from Xlib import X, XK, display
        from gi.repository import GLib

        if self.thread is not None:
            return
        self.stopping.clear()
        self.display = display.Display()
        self.root = self.display.screen().root
        self.keycode = self.display.keysym_to_keycode(XK.string_to_keysym(self.key))
        if not self.keycode:
            self.display.close()
            self.display = None
            raise RuntimeError(f"X11 cannot resolve {self.key} on this keyboard.")
        try:
            self.root.grab_key(self.keycode, X.AnyModifier, False,
                               X.GrabModeAsync, X.GrabModeAsync)
            self.display.sync()
            self._wake_reader, self._wake_writer = socket.socketpair()
        except Exception as exc:
            self.display.close()
            self.display = None
            raise RuntimeError(f"X11 could not reserve {self.key}; choose another key or free its desktop binding.") from exc

        def listen():
            connection = self.display
            while not self.stopping.is_set():
                try:
                    if not connection.pending_events():
                        # Sleep until an X11 event or an explicit stop signal, with no idle polling.
                        ready, _, _ = select.select([connection.fileno(), self._wake_reader], [], [])
                        if self._wake_reader in ready:
                            break
                    while connection.pending_events():
                        event = connection.next_event()
                        if event.type in (X.KeyPress, X.KeyRelease) and event.detail == self.keycode:
                            if event.type == X.KeyPress:
                                GLib.idle_add(self.pressed)
                            elif event.type == X.KeyRelease:
                                # X11 autorepeat may emit a synthetic release/press pair.
                                time.sleep(0.01)
                                keymap = connection.query_keymap()
                                if not keymap[self.keycode // 8] & (1 << (self.keycode % 8)):
                                    GLib.idle_add(self.released)
                except Exception:
                    break

        self.thread = threading.Thread(target=listen, daemon=True)
        self.thread.start()

    def stop(self):
        self.stopping.set()
        if self._wake_writer is not None:
            try:
                self._wake_writer.send(b"\0")
            except OSError:
                pass
        if self.thread is not None:
            self.thread.join(timeout=0.5)
            self.thread = None
        if self.display is not None:
            from Xlib import X
            try:
                self.root.ungrab_key(self.keycode, X.AnyModifier)
                self.display.close()
            except (OSError, ValueError):
                pass
            self.display = None
        for channel in (self._wake_reader, self._wake_writer):
            if channel is not None:
                channel.close()
        self._wake_reader = self._wake_writer = None

    def configure(self):
        raise RuntimeError("Choose F8, F9, or F10 in Settings to change the X11 shortcut.")
