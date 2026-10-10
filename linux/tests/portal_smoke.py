"""Check the app's real desktop portal connection without requesting user consent."""

from pathlib import Path
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from wayland_app import Portal, PORTAL_NAME, PORTAL_PATH
from gi.repository import Gio, GLib


portal = Portal()
assert portal.registration_error is None, f"App ID registration failed: {portal.registration_error}"
xml = portal.bus.call_sync(
    PORTAL_NAME, PORTAL_PATH, "org.freedesktop.DBus.Introspectable", "Introspect",
    None, GLib.VariantType("(s)"), Gio.DBusCallFlags.NONE, 15000, None,
).unpack()[0]
assert "org.freedesktop.portal.Screenshot" in xml, "Screenshot portal unavailable"
assert "org.freedesktop.portal.GlobalShortcuts" in xml, "Global Shortcuts portal unavailable"

created = portal.request("org.freedesktop.portal.GlobalShortcuts", "CreateSession",
                         "(a{sv})", (), 20, {
                             "session_handle_token": GLib.Variant("s", "sts" + uuid.uuid4().hex),
                         })
session = created.get("session_handle")
assert session and session.startswith("/org/freedesktop/portal/desktop/session/"), created
portal.bus.call_sync(PORTAL_NAME, session, "org.freedesktop.portal.Session", "Close",
                     None, None, Gio.DBusCallFlags.NONE, 5000, None)
print("Screenshot and Global Shortcuts portals are available; shortcut session opened")
