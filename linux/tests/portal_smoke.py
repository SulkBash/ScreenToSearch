"""Check the app's real GNOME portal connection without requesting user consent."""

from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from gnome_app import Portal, PORTAL_NAME, PORTAL_PATH
from gi.repository import Gio, GLib


portal = Portal()
xml = portal.bus.call_sync(
    PORTAL_NAME, PORTAL_PATH, "org.freedesktop.DBus.Introspectable", "Introspect",
    None, GLib.VariantType("(s)"), Gio.DBusCallFlags.NONE, 15000, None,
).unpack()[0]
assert "org.freedesktop.portal.Screenshot" in xml, "Screenshot portal unavailable"
assert "org.freedesktop.portal.GlobalShortcuts" in xml, "Global Shortcuts portal unavailable"

created = portal.request("org.freedesktop.portal.GlobalShortcuts", "CreateSession",
                         "(a{sv})", (), 20)
session = created.get("session_handle")
assert session and session.startswith("/org/freedesktop/portal/desktop/session/"), created
portal.bus.call_sync(PORTAL_NAME, session, "org.freedesktop.portal.Session", "Close",
                     None, None, Gio.DBusCallFlags.NONE, 5000, None)
print("GNOME Screenshot and Global Shortcuts portals are available; shortcut session opened")
