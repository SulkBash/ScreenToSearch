"""GNOME and KDE Plasma Wayland host: portal shortcut, indicator, and OCR overlay."""

import importlib
import io
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
from urllib.parse import unquote, urlparse
import uuid

import gi
import cairo

gi.require_foreign("cairo")
gi.require_version("Gdk", "3.0")
gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, GdkPixbuf, Gio, GLib, Gtk

from screen_to_search import (APP_ID, SEARCH_URLS, autostart_path, copy_data, load_settings,
                              open_text, save_settings, set_autostart, wayland_desktop)
from v6_ocr import recognize_text, recognize_words
from word_selection import Word, bounds_for_indices, monitor_crops, text_for_indices


PORTAL_NAME = "org.freedesktop.portal.Desktop"
PORTAL_PATH = "/org/freedesktop/portal/desktop"


class Portal:
    def __init__(self):
        self.bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        self.registration_error = None
        try:
            self.bus.call_sync(PORTAL_NAME, PORTAL_PATH, "org.freedesktop.host.portal.Registry",
                               "Register", GLib.Variant("(sa{sv})", (APP_ID, {})), None,
                               Gio.DBusCallFlags.NONE, 5000, None)
        except GLib.Error as exc:
            # Older portals may infer the application ID from the desktop launcher.
            self.registration_error = str(exc)

    def proxy(self, interface):
        return Gio.DBusProxy.new_sync(self.bus, Gio.DBusProxyFlags.NONE, None,
                                      PORTAL_NAME, PORTAL_PATH, interface, None)

    def request(self, interface, method, signature, values, timeout_seconds=120, extra_options=None):
        token = "sts" + uuid.uuid4().hex
        sender = self.bus.get_unique_name().lstrip(":").replace(".", "_")
        path = f"/org/freedesktop/portal/desktop/request/{sender}/{token}"
        result = {}
        loop = GLib.MainLoop()

        def response(_bus, _sender, _path, _interface, _signal, parameters):
            result["value"] = parameters.unpack()
            loop.quit()

        subscription = self.bus.signal_subscribe(PORTAL_NAME, "org.freedesktop.portal.Request",
                                                 "Response", path, None,
                                                 Gio.DBusSignalFlags.NONE, response)
        expired = {"value": False}

        def timeout():
            expired["value"] = True
            loop.quit()
            return False

        try:
            options = {"handle_token": GLib.Variant("s", token)}
            if extra_options:
                options.update(extra_options)
            args = (*values, options)
            self.proxy(interface).call_sync(method, GLib.Variant(signature, args),
                                            Gio.DBusCallFlags.NONE, 10000, None)
            timer = GLib.timeout_add_seconds(timeout_seconds, timeout)
            try:
                loop.run()
            finally:
                if not expired["value"]:
                    GLib.source_remove(timer)
        finally:
            self.bus.signal_unsubscribe(subscription)
        if "value" not in result:
            raise RuntimeError(f"{method} timed out.")
        code, payload = result["value"]
        if code != 0:
            raise RuntimeError(f"{method} was cancelled or denied by the desktop (code {code}).")
        return {key: variant.unpack() if hasattr(variant, "unpack") else variant
                for key, variant in payload.items()}

    def screenshot(self):
        options = {"interactive": GLib.Variant("b", False)}
        version = self.proxy("org.freedesktop.portal.Screenshot").get_cached_property("version")
        if version is not None and version.unpack() >= 3:
            options["target"] = GLib.Variant("u", 1)
        result = self.request("org.freedesktop.portal.Screenshot", "Screenshot",
                              "(sa{sv})", ("",), 120, options)
        uri = result.get("uri", "")
        if not uri.startswith("file:"):
            raise RuntimeError("The screenshot portal returned no local image.")
        return Path(unquote(urlparse(uri).path))


class Shortcut:
    def __init__(self, portal, activated, deactivated):
        self.portal = portal
        self.activated = activated
        self.deactivated = deactivated
        self.session = None
        self.signals = []

    def start(self):
        created = self.portal.request("org.freedesktop.portal.GlobalShortcuts", "CreateSession",
                                      "(a{sv})", (), 60, {
                                          "session_handle_token": GLib.Variant("s", "sts" + uuid.uuid4().hex),
                                      })
        self.session = created["session_handle"]
        shortcut = [("capture", {
            "description": GLib.Variant("s", "Hold to capture with Screen to Search"),
            "preferred_trigger": GLib.Variant("s", "F8"),
        })]
        bound = self.portal.request("org.freedesktop.portal.GlobalShortcuts", "BindShortcuts",
                                    "(oa(sa{sv})sa{sv})", (self.session, shortcut, ""), 120)
        if not bound.get("shortcuts"):
            raise RuntimeError("No global shortcut was assigned by the desktop.")

        def signal(_bus, _sender, _path, _interface, name, parameters):
            session, shortcut_id, _time, _options = parameters.unpack()
            if session == self.session and shortcut_id == "capture":
                (self.activated if name == "Activated" else self.deactivated)()

        for name in ("Activated", "Deactivated"):
            self.signals.append(self.portal.bus.signal_subscribe(
                PORTAL_NAME, "org.freedesktop.portal.GlobalShortcuts", name, PORTAL_PATH,
                None, Gio.DBusSignalFlags.NONE, signal))

    def stop(self):
        for subscription in self.signals:
            self.portal.bus.signal_unsubscribe(subscription)
        self.signals.clear()
        if self.session:
            try:
                self.portal.bus.call_sync(PORTAL_NAME, self.session, "org.freedesktop.portal.Session",
                                          "Close", None, None, Gio.DBusCallFlags.NONE, 5000, None)
            except GLib.Error:
                pass
            self.session = None

    def configure(self):
        if not self.session:
            raise RuntimeError("Enable the shortcut first.")
        self.portal.proxy("org.freedesktop.portal.GlobalShortcuts").call_sync(
            "ConfigureShortcuts", GLib.Variant("(osa{sv})", (self.session, "", {})),
            Gio.DBusCallFlags.NONE, 10000, None)


class CaptureOverlay(Gtk.Window):
    def __init__(self, host, pixbuf, words, monitor_index=None):
        super().__init__(title="Screen to Search capture")
        self.host = host
        self.pixbuf = pixbuf
        self.words = words
        self.mode = "smart"
        self.selected = set()
        self.selection_text = ""
        self.rect = None
        self.polygon = None
        self.drawing = []
        self.history = []
        self.focused_word = -1
        self.start_word = None
        self.start_point = None
        self.before_drag = None
        self.scale = 1.0
        self.offset_x = 0
        self.offset_y = 0
        self.set_decorated(False)
        self.set_keep_above(True)
        if monitor_index is None:
            self.fullscreen()
        else:
            self.fullscreen_on_monitor(Gdk.Screen.get_default(), monitor_index)
        self.connect("key-press-event", self.on_key)
        self.connect("destroy", lambda *_: self.host.overlay_closed(self))

        styles = Gtk.CssProvider()
        styles.load_from_data(b"""
            .sts-toolbar, .sts-actions, .sts-status {
                background: rgba(18, 24, 33, 0.92);
                border-radius: 8px;
                padding: 6px;
            }
            .sts-status { color: white; padding: 9px 14px; }
        """)
        Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), styles,
                                                  Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)

        overlay = Gtk.Overlay()
        self.add(overlay)
        self.surface = Gtk.DrawingArea()
        self.surface.add_events(Gdk.EventMask.BUTTON_PRESS_MASK | Gdk.EventMask.BUTTON_RELEASE_MASK |
                                Gdk.EventMask.POINTER_MOTION_MASK)
        self.surface.connect("draw", self.draw)
        self.surface.connect("button-press-event", self.on_press)
        self.surface.connect("button-release-event", self.on_release)
        self.surface.connect("motion-notify-event", self.on_motion)
        overlay.add(self.surface)

        toolbar = Gtk.Box(spacing=6)
        toolbar.set_halign(Gtk.Align.START)
        toolbar.set_valign(Gtk.Align.START)
        toolbar.set_margin_start(14)
        toolbar.set_margin_top(14)
        toolbar.get_style_context().add_class("sts-toolbar")
        for label, callback in (("Smart (A)", lambda: self.set_mode("smart")),
                                ("Text (T)", lambda: self.set_mode("text")),
                                ("Close", self.destroy)):
            button = Gtk.Button(label=label)
            button.connect("clicked", lambda _button, action=callback: action())
            toolbar.pack_start(button, False, False, 0)
        overlay.add_overlay(toolbar)

        self.status = Gtk.Label(label="Reading screen text…")
        self.status.set_halign(Gtk.Align.CENTER)
        self.status.set_valign(Gtk.Align.START)
        self.status.set_margin_top(70)
        self.status.get_style_context().add_class("sts-status")
        overlay.add_overlay(self.status)

        actions = Gtk.Box(spacing=6)
        actions.set_halign(Gtk.Align.CENTER)
        actions.set_valign(Gtk.Align.END)
        actions.set_margin_bottom(18)
        actions.get_style_context().add_class("sts-actions")
        self.action_buttons = {}
        for label, kind in (("Copy text", "copy_text"), ("Copy image", "copy_image"),
                            ("Search text", "search"), ("Translate", "translate")):
            button = Gtk.Button(label=label)
            button.connect("clicked", lambda _button, action=kind: self.execute(action))
            actions.pack_start(button, False, False, 0)
            self.action_buttons[kind] = button
        overlay.add_overlay(actions)
        self.actions = actions
        self.actions.hide()
        self.show_all()
        self.actions.hide()
        self.present()

    def transform(self):
        width = self.surface.get_allocated_width()
        height = self.surface.get_allocated_height()
        self.scale = min(width / self.pixbuf.get_width(), height / self.pixbuf.get_height())
        self.offset_x = (width - self.pixbuf.get_width() * self.scale) / 2
        self.offset_y = (height - self.pixbuf.get_height() * self.scale) / 2

    def image_point(self, x, y):
        self.transform()
        return (max(0, min(self.pixbuf.get_width(), (x - self.offset_x) / self.scale)),
                max(0, min(self.pixbuf.get_height(), (y - self.offset_y) / self.scale)))

    def draw(self, _surface, cr):
        self.transform()
        cr.set_source_rgb(0.08, 0.10, 0.14)
        cr.paint()
        cr.translate(self.offset_x, self.offset_y)
        cr.scale(self.scale, self.scale)
        Gdk.cairo_set_source_pixbuf(cr, self.pixbuf, 0, 0)
        cr.paint()
        cr.set_source_rgba(0, 0, 0, 0.38)
        cr.paint()
        bounds = self.rect or bounds_for_indices(self.words, self.selected)
        if bounds:
            cr.save()
            if self.polygon and len(self.polygon) >= 3:
                cr.move_to(*self.polygon[0])
                for point in self.polygon[1:]:
                    cr.line_to(*point)
                cr.close_path()
            else:
                x0, y0, x1, y1 = bounds
                cr.rectangle(x0, y0, x1 - x0, y1 - y0)
            cr.clip()
            Gdk.cairo_set_source_pixbuf(cr, self.pixbuf, 0, 0)
            cr.paint()
            cr.restore()
        cr.set_line_width(2 / self.scale)
        for index, word in enumerate(self.words):
            if index in self.selected or index == self.focused_word:
                cr.set_source_rgba(0.15, 0.55, 1.0, 0.38)
                cr.rectangle(word.x, word.y, word.width, word.height)
                cr.fill_preserve()
                cr.set_source_rgba(0.1, 0.4, 1.0, 0.9)
                cr.stroke()
        if self.rect:
            x0, y0, x1, y1 = self.rect
            cr.set_source_rgba(0.15, 0.55, 1.0, 0.23)
            cr.rectangle(x0, y0, x1 - x0, y1 - y0)
            cr.fill_preserve()
            cr.set_source_rgb(0.15, 0.55, 1.0)
            cr.stroke()
        if self.polygon and len(self.polygon) >= 2:
            cr.move_to(*self.polygon[0])
            for point in self.polygon[1:]:
                cr.line_to(*point)
            cr.set_source_rgb(1, 1, 1)
            cr.stroke()
        return False

    def word_at(self, x, y):
        return next((i for i, word in enumerate(self.words) if word.contains(x, y)), None)

    def on_press(self, _surface, event):
        if event.button != 1:
            return False
        x, y = self.image_point(event.x, event.y)
        self.before_drag = self.snapshot()
        self.start_word = self.word_at(x, y) if self.mode in ("smart", "text") else None
        self.start_point = (x, y)
        self.selected.clear()
        self.selection_text = ""
        self.rect = None
        self.polygon = [(x, y)] if self.mode == "lasso" else None
        self.actions.hide()
        if self.start_word is not None:
            self.selected.add(self.start_word)
        self.surface.queue_draw()
        return True

    def on_motion(self, _surface, event):
        if self.start_point is None or not (event.state & Gdk.ModifierType.BUTTON1_MASK):
            return False
        x, y = self.image_point(event.x, event.y)
        if self.mode == "lasso":
            self.polygon.append((x, y))
        elif self.start_word is not None:
            end = self.word_at(x, y)
            if end is not None:
                self.selected = set(range(min(self.start_word, end), max(self.start_word, end) + 1))
        else:
            x0, y0 = self.start_point
            self.rect = (min(x0, x), min(y0, y), max(x0, x), max(y0, y))
            if self.mode in ("smart", "text"):
                self.selected = {i for i, word in enumerate(self.words) if word.intersects(self.rect)}
        self.surface.queue_draw()
        return True

    def on_release(self, _surface, event):
        if event.button != 1 or self.start_point is None:
            return False
        x, y = self.image_point(event.x, event.y)
        start_x, start_y = self.start_point
        moved = (x - start_x) ** 2 + (y - start_y) ** 2 >= 64
        if self.mode == "lasso":
            self.polygon.append((x, y))
            if len(self.polygon) >= 3:
                xs, ys = zip(*self.polygon)
                self.rect = (min(xs), min(ys), max(xs), max(ys))
                if self.rect[2] - self.rect[0] >= 8 and self.rect[3] - self.rect[1] >= 8:
                    self.selected = {i for i, word in enumerate(self.words) if word.intersects(self.rect)}
                    self.selection_text = self.recognize_crop()
                    self.commit("Lasso selected. Choose an action.")
        elif self.mode == "rectangle":
            self.rect = (min(start_x, x), min(start_y, y), max(start_x, x), max(start_y, y))
            if self.rect[2] - self.rect[0] >= 8 and self.rect[3] - self.rect[1] >= 8:
                self.selection_text = self.recognize_crop()
                self.commit("Rectangle selected. Choose an action.")
        elif self.start_word is not None:
            end = self.word_at(x, y)
            if not moved:
                self.selected = {self.start_word}
            elif end is not None:
                self.selected = set(range(min(self.start_word, end), max(self.start_word, end) + 1))
            else:
                self.rect = (min(start_x, x), min(start_y, y), max(start_x, x), max(start_y, y))
                self.selected = {i for i, word in enumerate(self.words) if word.intersects(self.rect)}
            if self.selected:
                self.selection_text = text_for_indices(self.words, self.selected)
                self.commit("Text selected. Choose an action.")
            elif self.mode == "smart" and self.rect:
                self.selection_text = self.recognize_crop()
                self.commit("Area selected. Choose an action.")
        else:
            self.rect = (min(start_x, x), min(start_y, y), max(start_x, x), max(start_y, y))
            if not moved:
                self.rect = (max(0, x - 120), max(0, y - 55),
                             min(self.pixbuf.get_width(), x + 120),
                             min(self.pixbuf.get_height(), y + 55))
                found = self.recognize_point(x, y)
                if found:
                    self.selection_text, self.rect = found
                    self.commit("Small text selected. Choose an action.")
                else:
                    self.status.set_text("No text at that point. Drag around it to select the image.")
                    self.rect = None
            elif self.mode == "smart":
                self.selected = {i for i, word in enumerate(self.words) if word.intersects(self.rect)}
                self.selection_text = text_for_indices(self.words, self.selected) or self.recognize_crop()
                self.commit("Area selected. Choose an action.")
            elif self.mode == "text":
                self.status.set_text("No text found at the drag start. Try Smart or Rectangle.")
                self.rect = None
        self.start_word = None
        self.start_point = None
        self.surface.queue_draw()
        return True

    def on_key(self, _window, event):
        if event.keyval == Gdk.KEY_Escape:
            self.destroy()
            return True
        if event.state & Gdk.ModifierType.CONTROL_MASK and event.keyval in (Gdk.KEY_z, Gdk.KEY_Z):
            self.undo()
            return True
        if event.keyval in (Gdk.KEY_Delete, Gdk.KEY_BackSpace):
            self.clear()
            return True
        modes = {Gdk.KEY_a: "smart", Gdk.KEY_t: "text", Gdk.KEY_r: "rectangle", Gdk.KEY_l: "lasso"}
        if event.keyval in modes:
            self.set_mode(modes[event.keyval])
            return True
        if self.mode in ("smart", "text") and self.words:
            if event.keyval in (Gdk.KEY_Left, Gdk.KEY_Up, Gdk.KEY_Right, Gdk.KEY_Down):
                step = -1 if event.keyval in (Gdk.KEY_Left, Gdk.KEY_Up) else 1
                self.focused_word = max(0, min(len(self.words) - 1,
                                               self.focused_word + step if self.focused_word >= 0 else 0))
                if event.state & Gdk.ModifierType.SHIFT_MASK and self.selected:
                    anchor = min(self.selected)
                    self.before_drag = self.snapshot()
                    self.selected = set(range(min(anchor, self.focused_word), max(anchor, self.focused_word) + 1))
                    self.selection_text = text_for_indices(self.words, self.selected)
                    self.commit("Text selected. Choose an action.")
                self.status.set_text(f"Word {self.focused_word + 1} of {len(self.words)}: "
                                     f"{self.words[self.focused_word].text}. Press Enter to select.")
                self.surface.queue_draw()
                return True
            if event.keyval in (Gdk.KEY_Return, Gdk.KEY_space) and self.focused_word >= 0:
                self.before_drag = self.snapshot()
                self.selected = {self.focused_word}
                self.selection_text = self.words[self.focused_word].text
                self.rect = None
                self.commit("Text selected. Choose an action.")
                return True
        return False

    def snapshot(self):
        return (self.selected.copy(), self.rect, list(self.polygon) if self.polygon else None,
                self.selection_text)

    def commit(self, message):
        bounds = self.rect or bounds_for_indices(self.words, self.selected)
        if not bounds or (not self.selected and (bounds[2] - bounds[0] < 8 or bounds[3] - bounds[1] < 8)):
            self.status.set_text("Draw a larger selection.")
            return
        self.history.append(self.before_drag)
        self.history = self.history[-10:]
        self.update_actions()
        self.actions.show_all()
        self.status.set_text(message)
        self.surface.queue_draw()

    def set_words(self, words):
        if not self.get_visible():
            return False
        self.words = words
        self.status.set_text(f"{len(words)} words ready. Tap or sweep text; drag empty space for an image.")
        self.surface.queue_draw()
        return False

    def update_actions(self):
        for kind, button in self.action_buttons.items():
            button.set_sensitive(kind == "copy_image" or bool(self.selection_text.strip()))

    def set_mode(self, mode):
        self.mode = mode
        self.focused_word = -1
        self.status.set_text({"smart": "Smart: tap/sweep words or drag an area.",
                              "text": "Text: tap a word or drag across words.",
                              "rectangle": "Rectangle: drag around text or an image.",
                              "lasso": "Lasso: draw around text or an image."}[mode])
        self.surface.queue_draw()

    def undo(self):
        if not self.history:
            return
        self.selected, self.rect, self.polygon, self.selection_text = self.history.pop()
        self.update_actions()
        self.actions.set_visible(bool(self.rect or self.selected))
        self.status.set_text("Previous selection restored." if self.actions.get_visible() else "Selection undone.")
        self.surface.queue_draw()

    def clear(self):
        if self.rect is None and not self.selected:
            return
        self.history.append(self.snapshot())
        self.history = self.history[-10:]
        self.selected.clear()
        self.rect = None
        self.polygon = None
        self.selection_text = ""
        self.actions.hide()
        self.status.set_text("Selection cleared. Draw or tap again.")
        self.surface.queue_draw()

    def crop_png(self, white_outside=False):
        x, y, width, height = self.image_bounds()
        if self.polygon and len(self.polygon) >= 3:
            surface = cairo.ImageSurface(cairo.FORMAT_ARGB32, width, height)
            cr = cairo.Context(surface)
            cr.set_source_rgb(1, 1, 1) if white_outside else cr.set_source_rgba(0, 0, 0, 0)
            cr.paint()
            cr.move_to(self.polygon[0][0] - x, self.polygon[0][1] - y)
            for point in self.polygon[1:]:
                cr.line_to(point[0] - x, point[1] - y)
            cr.close_path()
            cr.clip()
            Gdk.cairo_set_source_pixbuf(cr, self.pixbuf, -x, -y)
            cr.paint()
            output = io.BytesIO()
            surface.write_to_png(output)
            return output.getvalue()
        crop = self.pixbuf.new_subpixbuf(x, y, width, height)
        success, data = crop.save_to_bufferv("png", [], [])
        if not success:
            raise RuntimeError("Could not encode the selected image.")
        return bytes(data)

    def recognize_crop(self):
        try:
            return recognize_text(self.crop_png(white_outside=True)).strip()
        except (RuntimeError, ValueError, OSError):
            return ""

    def recognize_point(self, x, y):
        try:
            x0, y0, _, _ = self.image_bounds()
            words = recognize_words(self.crop_png())
            if not words:
                return None
            local_x, local_y = x - x0, y - y0
            hit = next((word for word in words if word.contains(local_x, local_y)), None)
            if hit is None:
                hit = min(words, key=lambda word: (word.x + word.width / 2 - local_x) ** 2
                          + (word.y + word.height / 2 - local_y) ** 2)
                if not (hit.x - 12 <= local_x <= hit.x + hit.width + 12 and
                        hit.y - 12 <= local_y <= hit.y + hit.height + 12):
                    return None
            return hit.text, (x0 + hit.x, y0 + hit.y,
                              x0 + hit.x + hit.width, y0 + hit.y + hit.height)
        except (RuntimeError, ValueError, OSError):
            return None

    def image_bounds(self):
        bounds = self.rect or bounds_for_indices(self.words, self.selected)
        if bounds is None:
            raise ValueError("Select a word or draw a region first.")
        x0, y0, x1, y1 = bounds
        x0 = max(0, min(self.pixbuf.get_width() - 1, int(x0)))
        y0 = max(0, min(self.pixbuf.get_height() - 1, int(y0)))
        x1 = max(x0 + 1, min(self.pixbuf.get_width(), int(x1)))
        y1 = max(y0 + 1, min(self.pixbuf.get_height(), int(y1)))
        return x0, y0, x1 - x0, y1 - y0

    def execute(self, kind):
        try:
            if kind == "copy_image":
                copy_data(self.crop_png(), "image/png")
            else:
                value = self.selection_text
                if not value:
                    raise ValueError("No words were selected.")
                if kind == "copy_text":
                    copy_data(value.encode("utf-8"), "text/plain;charset=utf-8")
                else:
                    open_text(kind, value, self.host.settings)
            self.destroy()
        except (RuntimeError, ValueError, OSError) as exc:
            self.host.error(str(exc))


class WaylandHost:
    LANGUAGES = (("English", "en"), ("Spanish", "es"), ("French", "fr"),
                 ("German", "de"), ("Portuguese", "pt"), ("Italian", "it"),
                 ("Japanese", "ja"), ("Korean", "ko"), ("Chinese (Simplified)", "zh-CN"),
                 ("Arabic", "ar"), ("Hindi", "hi"), ("Russian", "ru"))

    def __init__(self, capture_now=False):
        self.settings = load_settings()
        self.settings["launch_at_sign_in"] = autostart_path().exists()
        self.settings.setdefault("hold_milliseconds", 350)
        self.portal = Portal()
        self.overlays = []
        self.restore_settings_after_overlay = False
        self.hold_timer = None
        self.shortcut = None
        self.indicator = None
        self._updating_startup = False
        self._updating_shortcut = False
        self.window = Gtk.Window(title="Screen to Search settings")
        self.window.set_default_size(520, 410)
        self.window.connect("delete-event", self.on_window_close)
        self.make_settings_window()
        self.make_indicator()
        if self.indicator is None:
            self.window.show_all()
        if self.settings["shortcut_enabled"]:
            self.start_shortcut()
        if capture_now:
            GLib.idle_add(self.capture)

    def make_settings_window(self):
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        box.set_border_width(16)
        self.window.add(box)
        box.pack_start(Gtk.Label(label=f"Hold the {wayland_desktop()} shortcut to capture, or click Capture."),
                       False, False, 0)
        capture = Gtk.Button(label="Capture now")
        capture.connect("clicked", lambda *_: self.capture())
        box.pack_start(capture, False, False, 0)
        grid = Gtk.Grid(column_spacing=10, row_spacing=8)
        box.pack_start(grid, False, False, 8)

        def field(row, title, widget):
            label = Gtk.Label(label=title)
            label.set_halign(Gtk.Align.START)
            grid.attach(label, 0, row, 1, 1)
            grid.attach(widget, 1, row, 1, 1)

        self.hold = Gtk.SpinButton.new_with_range(200, 1000, 50)
        self.hold.set_value(self.settings["hold_milliseconds"])
        field(0, "Hold duration (ms)", self.hold)
        field(1, "Text recognition", Gtk.Label(label="Automatic multilingual (PP-OCRv6)"))
        self.engine = Gtk.ComboBoxText()
        for name in SEARCH_URLS:
            self.engine.append_text(name)
        self.engine.set_active(list(SEARCH_URLS).index(self.settings["search_engine"]))
        field(2, "Search engine", self.engine)
        self.target = Gtk.ComboBoxText()
        for name, code in self.LANGUAGES:
            self.target.append(code, name)
        self.target.set_active_id(self.settings["translation_language"])
        field(3, "Translate to", self.target)
        self.shortcut_toggle = Gtk.CheckButton(label="Enable hold shortcut")
        self.shortcut_toggle.set_active(self.settings["shortcut_enabled"])
        self.shortcut_toggle.connect("toggled", self.toggle_shortcut)
        grid.attach(self.shortcut_toggle, 0, 4, 2, 1)
        configure = Gtk.Button(label="Change shortcut…")
        configure.connect("clicked", self.configure_shortcut)
        grid.attach(configure, 1, 5, 1, 1)
        self.startup_toggle = Gtk.CheckButton(label="Start when I sign in")
        self.startup_toggle.set_active(self.settings["launch_at_sign_in"])
        self.startup_toggle.connect("toggled", self.toggle_autostart)
        grid.attach(self.startup_toggle, 0, 6, 2, 1)
        save = Gtk.Button(label="Save settings")
        save.connect("clicked", self.save)
        box.pack_start(save, False, False, 0)

    def make_indicator(self):
        try:
            watcher = self.portal.bus.call_sync(
                "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus",
                "NameHasOwner", GLib.Variant("(s)", ("org.kde.StatusNotifierWatcher",)),
                GLib.VariantType("(b)"), Gio.DBusCallFlags.NONE, 5000, None).unpack()[0]
            if not watcher:
                return
        except GLib.Error:
            return
        module = None
        for name in ("AyatanaAppIndicator3", "AppIndicator3"):
            try:
                gi.require_version(name, "0.1")
                module = importlib.import_module("gi.repository." + name)
                break
            except (ImportError, ValueError):
                continue
        if module is None:
            return
        self.indicator = module.Indicator.new("screen-to-search", "edit-find-symbolic",
                                              module.IndicatorCategory.APPLICATION_STATUS)
        menu = Gtk.Menu()
        for label, callback in (("Capture", self.capture), ("Settings", self.show_settings),
                                ("Quit", self.quit)):
            item = Gtk.MenuItem(label=label)
            item.connect("activate", lambda _item, action=callback: action())
            menu.append(item)
        menu.show_all()
        self.indicator.set_menu(menu)
        self.indicator.set_status(module.IndicatorStatus.ACTIVE)

    def on_window_close(self, *_):
        if self.indicator is None:
            self.quit()
        else:
            self.window.hide()
        return True

    def save(self, *_):
        self.settings.update(hold_milliseconds=self.hold.get_value_as_int(),
                             search_engine=self.engine.get_active_text(),
                             translation_language=self.target.get_active_id() or "en")
        save_settings(self.settings)

    def start_shortcut(self):
        try:
            self.shortcut = Shortcut(self.portal, self.shortcut_pressed, self.shortcut_released)
            self.shortcut.start()
        except (RuntimeError, GLib.Error) as exc:
            if self.shortcut is not None:
                self.shortcut.stop()
            self.shortcut = None
            self.settings["shortcut_enabled"] = False
            save_settings(self.settings)
            self._updating_shortcut = True
            self.shortcut_toggle.set_active(False)
            self._updating_shortcut = False
            self.window.show_all()
            self.error("Global shortcut unavailable: " + str(exc))

    def toggle_shortcut(self, widget):
        if self._updating_shortcut:
            return
        enabled = widget.get_active()
        self.settings["shortcut_enabled"] = enabled
        save_settings(self.settings)
        if enabled and self.shortcut is None:
            self.start_shortcut()
        elif not enabled and self.shortcut is not None:
            self.shortcut.stop()
            self.shortcut = None

    def configure_shortcut(self, *_):
        try:
            if self.shortcut is None:
                raise RuntimeError("Enable the shortcut first.")
            self.shortcut.configure()
        except (RuntimeError, GLib.Error) as exc:
            self.error(str(exc))

    def toggle_autostart(self, widget):
        if self._updating_startup:
            return
        enabled = widget.get_active()
        try:
            set_autostart(enabled)
            self.settings["launch_at_sign_in"] = enabled
            save_settings(self.settings)
        except (RuntimeError, OSError) as exc:
            self._updating_startup = True
            widget.set_active(not enabled)
            self._updating_startup = False
            self.error(str(exc))

    def show_settings(self):
        self.window.show_all()
        self.window.present()

    def error(self, message):
        dialog = Gtk.MessageDialog(transient_for=self.window, flags=0,
                                   message_type=Gtk.MessageType.ERROR, buttons=Gtk.ButtonsType.OK,
                                   text="Screen to Search")
        dialog.format_secondary_text(message)
        dialog.run()
        dialog.destroy()

    def shortcut_pressed(self):
        if self.hold_timer is None:
            self.hold_timer = GLib.timeout_add(int(self.settings["hold_milliseconds"]), self.held_long_enough)

    def shortcut_released(self):
        if self.hold_timer is not None:
            GLib.source_remove(self.hold_timer)
            self.hold_timer = None

    def held_long_enough(self):
        self.hold_timer = None
        self.capture()
        return False

    def capture(self):
        if self.overlays:
            self.overlays[0].present()
            return False
        was_visible = self.window.get_visible()
        self.window.hide()
        while Gtk.events_pending():
            Gtk.main_iteration_do(False)
        try:
            path = self.portal.screenshot()
            pixbuf = GdkPixbuf.Pixbuf.new_from_file(str(path))
            self.restore_settings_after_overlay = was_visible
            screen = Gdk.Screen.get_default()
            geometries = []
            for index in range(screen.get_n_monitors()):
                area = screen.get_monitor_geometry(index)
                geometries.append((area.x, area.y, area.width, area.height))
            crops = monitor_crops(geometries, pixbuf.get_width(), pixbuf.get_height())
            virtual_width = max(x + w for x, _, w, _ in geometries) - min(x for x, _, _, _ in geometries)
            virtual_height = max(y + h for _, y, _, h in geometries) - min(y for _, y, _, _ in geometries)
            screenshot_ratio = pixbuf.get_width() / pixbuf.get_height()
            virtual_ratio = virtual_width / virtual_height
            if len(crops) > 1 and abs(screenshot_ratio / virtual_ratio - 1) < 0.1:
                for index, (x, y, width, height) in enumerate(crops):
                    segment = pixbuf.new_subpixbuf(x, y, width, height)
                    self.overlays.append(CaptureOverlay(self, segment, [], index))
            else:
                self.overlays.append(CaptureOverlay(self, pixbuf, [], None))
                crops = [(0, 0, pixbuf.get_width(), pixbuf.get_height())]
            overlays = tuple(self.overlays)
            def recognize_screen():
                try:
                    words = recognize_words(str(path))
                except (RuntimeError, ImportError, OSError):
                    words = []
                for overlay, (x, y, width, height) in zip(overlays, crops):
                    local = [Word(word.text, word.x - x, word.y - y, word.width, word.height, word.line)
                             for word in words if x <= word.x + word.width / 2 < x + width
                             and y <= word.y + word.height / 2 < y + height]
                    GLib.idle_add(overlay.set_words, local)

            threading.Thread(target=recognize_screen, daemon=True).start()
        except (RuntimeError, GLib.Error, OSError) as exc:
            self.error(str(exc))
            if was_visible:
                self.window.show_all()
        return False

    def overlay_closed(self, overlay):
        if overlay in self.overlays:
            others = [item for item in self.overlays if item is not overlay]
            self.overlays.clear()
            for item in others:
                item.destroy()
            if self.restore_settings_after_overlay:
                self.window.show_all()
                self.window.present()
            self.restore_settings_after_overlay = False

    def quit(self):
        if self.shortcut:
            self.shortcut.stop()
        Gtk.main_quit()


def run(capture_now=False):
    import fcntl

    runtime = Path(os.environ.get("XDG_RUNTIME_DIR") or tempfile.gettempdir())
    basename = f"screen-to-search-{os.getuid()}"
    lock_path = runtime / (basename + ".lock")
    socket_path = runtime / (basename + ".sock")
    with lock_path.open("a+b") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            for attempt in range(10):
                try:
                    with socket.socket(socket.AF_UNIX) as client:
                        client.connect(str(socket_path))
                        client.sendall(b"capture" if capture_now else b"settings")
                    break
                except (FileNotFoundError, ConnectionRefusedError):
                    if attempt == 9:
                        raise RuntimeError("The running Screen to Search app did not respond.")
                    time.sleep(0.1)
            return

        socket_path.unlink(missing_ok=True)
        with socket.socket(socket.AF_UNIX) as server:
            server.bind(str(socket_path))
            os.chmod(socket_path, 0o600)
            server.listen(2)
            server.settimeout(0.5)
            host = WaylandHost(capture_now)
            stopping = threading.Event()

            def accept_commands():
                while not stopping.is_set():
                    try:
                        connection, _ = server.accept()
                    except socket.timeout:
                        continue
                    except OSError:
                        break
                    with connection:
                        command = connection.recv(32)
                    GLib.idle_add(host.capture if command == b"capture" else host.show_settings)

            threading.Thread(target=accept_commands, daemon=True).start()
            try:
                Gtk.main()
            finally:
                stopping.set()
                socket_path.unlink(missing_ok=True)
