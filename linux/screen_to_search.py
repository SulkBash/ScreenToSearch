#!/usr/bin/env python3
"""Linux capture, local OCR, and selection actions for Screen to Search."""

import argparse
import importlib
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
from urllib.parse import quote
import webbrowser


from v6_ocr import available as ocr_available, recognize_text


DEFAULTS = {"search_engine": "google", "translation_language": "en",
            "hold_milliseconds": 350, "shortcut_enabled": True, "launch_at_sign_in": False}
APP_ID = "org.screentosearch.ScreenToSearch"


def wayland_desktop():
    if not os.environ.get("WAYLAND_DISPLAY"):
        return None
    desktops = os.environ.get("XDG_CURRENT_DESKTOP", "").upper().split(":")
    if "KDE" in desktops or "PLASMA" in desktops:
        return "KDE Plasma"
    if "GNOME" in desktops:
        return "GNOME"
    return None
SEARCH_URLS = {
    "google": "https://www.google.com/search?q=",
    "bing": "https://www.bing.com/search?q=",
    "duckduckgo": "https://duckduckgo.com/?q=",
}


def config_path():
    base = Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config")
    return base / "screen-to-search" / "settings.json"


def load_settings():
    try:
        saved = json.loads(config_path().read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return DEFAULTS.copy()
    settings = DEFAULTS.copy()
    if isinstance(saved, dict):
        for key in settings:
            if key == "hold_milliseconds":
                if isinstance(saved.get(key), int) and 200 <= saved[key] <= 1000:
                    settings[key] = saved[key]
            elif key in ("shortcut_enabled", "launch_at_sign_in"):
                if isinstance(saved.get(key), bool):
                    settings[key] = saved[key]
            elif isinstance(saved.get(key), str) and saved[key].strip():
                settings[key] = saved[key].strip()
    if settings["search_engine"] not in SEARCH_URLS:
        settings["search_engine"] = DEFAULTS["search_engine"]
    return settings


def save_settings(settings):
    path = config_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(settings, indent=2) + "\n", encoding="utf-8")


def install_desktop():
    base = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share")
    app_dir = base / "screen-to-search"
    app_dir.mkdir(parents=True, exist_ok=True)
    source = Path(__file__).resolve().parent
    for name in ("screen_to_search.py", "wayland_app.py", "word_selection.py", "v6_ocr.py"):
        if (source / name).resolve() != (app_dir / name).resolve():
            shutil.copy2(source / name, app_dir / name)
    from v6_ocr import model_dir
    model_target = app_dir / "models" / "v6"
    model_target.mkdir(parents=True, exist_ok=True)
    for name in ("PP-OCRv6_det_small.onnx", "PP-OCRv6_rec_small.onnx", "ppocrv6_small_dict.txt"):
        if (model_dir() / name).resolve() != (model_target / name).resolve():
            shutil.copy2(model_dir() / name, model_target / name)
    icon = app_dir / "icon.svg"
    icon_source = source / "icon.svg"
    if not icon_source.exists():
        icon_source = source.parent / "src" / "ScreenToSearch" / "Assets" / "ScreenToSearch.svg"
    if icon_source.resolve() != icon.resolve():
        shutil.copy2(icon_source, icon)
    launcher = app_dir / "launch"
    launcher.write_text("#!/bin/sh\nexec python3 " + shlex.quote(str(app_dir / "screen_to_search.py")) + " \"$@\"\n",
                        encoding="utf-8")
    launcher.chmod(0o755)
    applications = base / "applications"
    applications.mkdir(parents=True, exist_ok=True)
    # Exec quoting follows the Desktop Entry specification for paths with spaces.
    quoted_launcher = '"' + str(launcher).replace('\\', '\\\\').replace('"', '\\"').replace('$', '\\$').replace('`', '\\`') + '"'
    desktop = applications / (APP_ID + ".desktop")
    desktop.write_text("[Desktop Entry]\nType=Application\nName=Screen to Search\n"
                       + "Exec=" + quoted_launcher + "\nIcon=" + str(icon) + "\nTerminal=false\n"
                       + "Categories=Utility;Graphics;\n", encoding="utf-8")
    print(f"Installed desktop launcher: {desktop}")
    print("Launch Screen to Search from your desktop's app launcher to approve its global shortcut.")


def autostart_path():
    return Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config") / "autostart" / (APP_ID + ".desktop")


def set_autostart(enabled):
    destination = autostart_path()
    if not enabled:
        destination.unlink(missing_ok=True)
        return
    base = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share")
    desktop = base / "applications" / (APP_ID + ".desktop")
    if not desktop.exists():
        raise RuntimeError("Install the desktop launcher before enabling start at sign-in.")
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(desktop, destination)


def available_capture_methods():
    methods = []
    wayland = bool(os.environ.get("WAYLAND_DISPLAY"))
    if wayland and shutil.which("grim") and shutil.which("slurp"):
        methods.append("grim")
    if wayland and shutil.which("spectacle"):
        methods.append("spectacle")
    if wayland and shutil.which("gnome-screenshot"):
        methods.append("gnome-screenshot")
    if not wayland and shutil.which("maim"):
        methods.append("maim")
    if shutil.which("flameshot"):
        methods.append("flameshot")
    if not wayland and shutil.which("import"):
        methods.append("import")
    if not wayland and shutil.which("gnome-screenshot"):
        methods.append("gnome-screenshot")
    if not wayland and shutil.which("spectacle"):
        methods.append("spectacle")
    return methods


def capture_region(path):
    methods = available_capture_methods()
    if not methods:
        raise RuntimeError("No region capture tool found. See the Linux dependencies in README.md.")
    errors = []
    for method in methods:
        try:
            if method == "grim":
                selection = subprocess.run(["slurp"], capture_output=True, text=True, check=True)
                geometry = selection.stdout.strip()
                if not geometry:
                    return False
                subprocess.run(["grim", "-g", geometry, str(path)], capture_output=True, check=True)
            elif method == "maim":
                subprocess.run(["maim", "-s", str(path)], capture_output=True, check=True)
            elif method == "flameshot":
                result = subprocess.run(["flameshot", "gui", "--raw"], capture_output=True, check=True)
                if not result.stdout:
                    return False
                path.write_bytes(result.stdout)
            elif method == "import":
                subprocess.run(["import", "-silent", str(path)], capture_output=True, check=True)
            elif method == "gnome-screenshot":
                subprocess.run(["gnome-screenshot", "-a", "-f", str(path)], capture_output=True, check=True)
            else:
                subprocess.run(["spectacle", "-r", "-b", "-o", str(path)], capture_output=True, check=True)
            if path.exists() and path.stat().st_size:
                return True
        except subprocess.CalledProcessError as exc:
            if (method == "grim" and exc.cmd and exc.cmd[0] == "slurp" and not exc.stderr) or (
                method == "flameshot" and exc.returncode == 2
            ):
                return False
            # An empty selection is a normal cancellation. Other backends may still work.
            detail = exc.stderr.decode(errors="replace") if isinstance(exc.stderr, bytes) else exc.stderr
            errors.append(f"{method}: {(detail or 'capture failed').strip()}")
        path.unlink(missing_ok=True)
    raise RuntimeError("Could not capture a region. " + " ".join(errors))


def recognize(path):
    try:
        return recognize_text(str(path)).strip(), ""
    except (RuntimeError, ImportError, OSError) as exc:
        return "", str(exc)


def clipboard_command():
    if os.environ.get("WAYLAND_DISPLAY") and shutil.which("wl-copy"):
        return ["wl-copy"]
    if shutil.which("xclip"):
        return ["xclip", "-selection", "clipboard"]
    raise RuntimeError("Install wl-clipboard (Wayland) or xclip (X11) to copy selections.")


def copy_data(data, mime):
    command = clipboard_command()
    if command[0] == "wl-copy":
        command += ["--type", mime]
    else:
        command += ["-t", mime, "-i"]
    result = subprocess.run(command, input=data, capture_output=True)
    if result.returncode:
        raise RuntimeError(result.stderr.decode(errors="replace").strip() or "Clipboard copy failed.")


def open_text(kind, text, settings):
    text = text.strip()
    if not text:
        raise ValueError("No text was recognized. Select or enter some text first.")
    if kind == "search":
        url = SEARCH_URLS[settings["search_engine"]] + quote(text)
    else:
        url = ("https://translate.google.com/?sl=auto&tl="
               + quote(settings["translation_language"]) + "&text=" + quote(text) + "&op=translate")
    if not webbrowser.open(url):
        raise RuntimeError("Could not open a web browser.")


def check_dependencies():
    desktop = wayland_desktop()
    if desktop:
        ready = ocr_available()
        print(f"Display: {desktop} Wayland")
        print("Screenshot and shortcut: XDG desktop portals")
        print("OCR: PP-OCRv6" if ready else "OCR: missing rapidocr, onnxruntime, or v6 models")
        try:
            print("Clipboard:", clipboard_command()[0])
        except RuntimeError:
            print("Clipboard: missing")
            ready = False
        try:
            import cairo  # noqa: F401
            import gi
            gi.require_foreign("cairo")
            gi.require_version("Gtk", "3.0")
            from gi.repository import Gio, GLib, Gtk  # noqa: F401
            print("GUI: GTK 3 / PyGObject / Cairo")
            bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
            introspection = bus.call_sync("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
                                          "org.freedesktop.DBus.Introspectable", "Introspect", None,
                                          GLib.VariantType("(s)"), Gio.DBusCallFlags.NONE, 5000, None).unpack()[0]
            for name in ("Screenshot", "GlobalShortcuts"):
                present = "org.freedesktop.portal." + name in introspection
                print(f"{name} portal:", "available" if present else "missing")
                ready = ready and present
            indicator = False
            for name in ("AyatanaAppIndicator3", "AppIndicator3"):
                try:
                    gi.require_version(name, "0.1")
                    importlib.import_module("gi.repository." + name)
                    indicator = True
                    break
                except (ImportError, ValueError):
                    pass
            print("Panel indicator library:", "available" if indicator else "missing")
            ready = ready and indicator
        except (ImportError, ValueError):
            print("GUI: missing python3-gi, python3-cairo, or GTK 3")
            ready = False
        except GLib.Error as exc:
            print("Desktop portal unavailable:", exc.message)
            ready = False
        return 0 if ready else 1
    methods = available_capture_methods()
    ready = bool(methods)
    print("Display:", "Wayland" if os.environ.get("WAYLAND_DISPLAY") else "X11")
    print("Region capture:", ", ".join(methods) or "missing")
    ocr = ocr_available()
    print("OCR: PP-OCRv6" if ocr else "OCR: missing rapidocr, onnxruntime, or v6 models")
    ready = ready and ocr
    try:
        print("Clipboard:", clipboard_command()[0])
    except RuntimeError:
        print("Clipboard: missing")
        ready = False
    try:
        import tkinter  # noqa: F401
        print("GUI: tkinter")
    except ImportError:
        print("GUI: missing python3-tk")
        return 1
    return 0 if ready else 1


def run_gui(capture_now):
    try:
        import tkinter as tk
        from tkinter import messagebox, ttk
    except ImportError as exc:
        raise RuntimeError("Install python3-tk for the Linux interface.") from exc

    settings = load_settings()
    try:
        root = tk.Tk()
    except tk.TclError as exc:
        raise RuntimeError("Could not open the Linux desktop display.") from exc
    root.title("Screen to Search")
    root.geometry("620x520")
    root.minsize(430, 380)
    image = None

    outer = ttk.Frame(root, padding=14)
    outer.pack(fill="both", expand=True)
    ttk.Label(outer, text="Screen to Search", font=("TkDefaultFont", 16, "bold")).pack(anchor="w")
    ttk.Label(outer, text="Select a screen region, then use its image or recognized text.").pack(anchor="w", pady=(2, 10))
    status = tk.StringVar(value="Ready")
    text_box = tk.Text(outer, wrap="word", height=12)
    text_box.pack(fill="both", expand=True)

    def selected_text():
        try:
            return text_box.get("sel.first", "sel.last").strip()
        except tk.TclError:
            return text_box.get("1.0", "end-1c").strip()

    def action(callback):
        try:
            callback()
            status.set("Done")
        except (RuntimeError, ValueError, OSError) as exc:
            status.set(str(exc))
            messagebox.showerror("Screen to Search", str(exc), parent=root)

    def copy_text():
        value = selected_text()
        if not value:
            raise ValueError("No text to copy.")
        copy_data(value.encode("utf-8"), "text/plain;charset=utf-8")

    def copy_image():
        if image is None:
            raise ValueError("Capture a region first.")
        copy_data(image, "image/png")

    def capture():
        nonlocal image
        root.withdraw()
        root.update()
        time.sleep(0.2)
        capture_error = None
        try:
            with tempfile.TemporaryDirectory(prefix="screen-to-search-") as temp:
                path = Path(temp) / "selection.png"
                if not capture_region(path):
                    status.set("Capture cancelled")
                    return
                image = path.read_bytes()
                recognized, error = recognize(path)
                text_box.delete("1.0", "end")
                text_box.insert("1.0", recognized)
                status.set(error or "Region captured. Select part of the text to use only that part.")
        except (RuntimeError, OSError) as exc:
            status.set(str(exc))
            capture_error = str(exc)
        finally:
            root.deiconify()
            root.lift()
        if capture_error:
            messagebox.showerror("Capture unavailable", capture_error, parent=root)

    buttons = ttk.Frame(outer)
    buttons.pack(fill="x", pady=(10, 4))
    ttk.Button(buttons, text="Capture region", command=capture).pack(side="left")
    ttk.Button(buttons, text="Copy text", command=lambda: action(copy_text)).pack(side="left", padx=4)
    ttk.Button(buttons, text="Copy image", command=lambda: action(copy_image)).pack(side="left", padx=4)
    buttons2 = ttk.Frame(outer)
    buttons2.pack(fill="x", pady=(0, 10))
    ttk.Button(buttons2, text="Search text", command=lambda: action(
        lambda: open_text("search", selected_text(), settings))).pack(side="left")
    ttk.Button(buttons2, text="Translate", command=lambda: action(
        lambda: open_text("translate", selected_text(), settings))).pack(side="left", padx=4)

    options = ttk.LabelFrame(outer, text="Settings", padding=8)
    options.pack(fill="x")
    engine = tk.StringVar(value=settings["search_engine"])
    target = tk.StringVar(value=settings["translation_language"])
    ttk.Label(options, text="Search").grid(row=0, column=0, sticky="w")
    ttk.Combobox(options, textvariable=engine, values=list(SEARCH_URLS), state="readonly", width=14).grid(row=0, column=1, padx=6)
    ttk.Label(options, text="OCR: Automatic multilingual (PP-OCRv6)").grid(row=0, column=2, columnspan=2, sticky="w")
    ttk.Label(options, text="Translate to").grid(row=1, column=0, sticky="w", pady=5)
    ttk.Entry(options, textvariable=target, width=10).grid(row=1, column=1, sticky="w", padx=6)

    def save():
        settings.update(search_engine=engine.get(),
                        translation_language=target.get().strip() or "en")
        action(lambda: save_settings(settings))

    ttk.Button(options, text="Save settings", command=save).grid(row=1, column=3, sticky="e")
    ttk.Label(outer, textvariable=status, wraplength=580).pack(anchor="w", pady=(8, 0))
    if capture_now:
        root.after(50, capture)
    root.mainloop()


def main(argv=None):
    parser = argparse.ArgumentParser(description="Screen to Search for Linux")
    parser.add_argument("--capture", action="store_true", help="capture a region immediately")
    parser.add_argument("--check", action="store_true", help="show installed Linux dependencies")
    parser.add_argument("--install-desktop", "--install-gnome", action="store_true",
                        help="install a GNOME or KDE Plasma launcher for portal permissions")
    args = parser.parse_args(argv)
    if args.install_desktop:
        install_desktop()
        return 0
    if args.check:
        return check_dependencies()
    try:
        if wayland_desktop():
            from wayland_app import run
            run(args.capture)
        else:
            run_gui(args.capture)
    except (RuntimeError, ImportError) as exc:
        print(f"Screen to Search: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
