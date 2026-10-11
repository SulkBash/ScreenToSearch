# Screen to Search

![Screen to Search logo](src/ScreenToSearch/Assets/ScreenToSearch.svg)

Want to copy text from a screenshot or look up something you spotted in an app? Screen to Search lets you select what's on your display, even when the app you're looking at won't let you highlight it. Copy text or an image, search for selected text, or open it in Google Translate.

## Windows

### Get started

Download `ScreenToSearch-win-x64.zip` from the [Releases page](../../releases), extract the whole archive, and run `ScreenToSearch.exe`. Keep the `models` folder next to the app. The release includes the .NET runtime, so there is no separate runtime installation.

The app stays in the system tray. Double-click its icon to open Settings, or right-click it to open the capture screen, open Settings, or quit. Closing Settings doesn't quit the app. You can choose whether it starts when you sign in to Windows.

### Use it

1. Hold **Caps Lock** briefly to take a snapshot of your screen. You can change the shortcut and how long you need to hold it in Settings, or turn the shortcut off and open the app from the tray instead.
2. Click a word to select it, or drag across words to select a longer passage. This works with text in images and other places where normal text selection doesn't work. To select an area, start dragging from an empty part of the screen. The app will select any text it finds there, or the image itself if it finds none.
3. Choose what to do with your selection: **Copy text**, **Copy image**, **Search text**, or **Translate**.

The default **Smart** mode handles most selections. Use **Text** if you want to pick individual recognized words, **Rectangle** to draw a box, or **Lasso** to draw around an area. The toolbar shows Smart and Text; the keyboard shortcuts below give you all four modes.

| Key | What it does |
| --- | --- |
| `A`, `T`, `R`, `L` | Switch to Smart, Text, Rectangle, or Lasso |
| Arrow keys | Move between recognized words |
| `Enter` | Select the focused word |
| `Ctrl+Z` | Undo the last selection |
| `Delete` | Clear the selection |
| `Esc` | Close the capture screen |

### Build from source

You'll need Windows 10 or 11 (x64) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). From the repository root, run:

```powershell
dotnet restore "src/ScreenToSearch/ScreenToSearch.csproj"
dotnet run --project "src/ScreenToSearch/ScreenToSearch.csproj"
```

To build an executable folder:

```powershell
dotnet publish "src/ScreenToSearch/ScreenToSearch.csproj" -c Release --self-contained false -o publish
.\publish\ScreenToSearch.exe
```

That computer will need the .NET 10 Desktop Runtime. Keep the entire `publish` folder together, including `models`. If you're making a GitHub release, zip the **contents** of `publish`; the folder itself is generated and shouldn't be committed. Quit any older copy of the app from the tray before running a different build.

#### Checks for contributors

The app includes a couple of local checks. After publishing it, run these commands from the repository root. Each one writes a JSON report you can inspect:

```powershell
.\publish\ScreenToSearch.exe --stage2-selftest .\stage2-report.json
.\publish\ScreenToSearch.exe --stage0-selftest .\stage0-report.json
```

For a closer look at text recognition on an image, use `--ocr-image-report <image-path> <report-json-path>`. Windows uses one PP-OCRv6 detector and recognizer for all text recognition.

## Linux

The Linux entry point is [linux/screen_to_search.py](linux/screen_to_search.py). It uses the same bundled PP-OCRv6 detector, recognizer, and dictionary as Windows, through RapidOCR for Python. On GNOME and KDE Plasma Wayland it uses GTK 3 and the [Screenshot](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Screenshot.html) and [Global Shortcuts](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html) portals. Other Linux sessions use the region-selection window described below.

### GNOME 48 or newer or KDE Plasma on Wayland

Download `ScreenToSearch-linux.zip` from the [Releases page](../../releases) and extract it, or use this repository checkout. GNOME 48 or newer is required for the Global Shortcuts portal used by the hold shortcut. KDE Plasma must have `xdg-desktop-portal-kde` with both Screenshot and Global Shortcuts interfaces available.

Install Python 3, `python3-gi`, `python3-cairo`, `python3-gi-cairo`, GTK 3's Python introspection package, `wl-clipboard`, and Ayatana AppIndicator's introspection package. On KDE also install `xdg-desktop-portal-kde`. Install `rapidocr` and `onnxruntime` into the same Python environment as the launcher (`python3 -m pip install rapidocr onnxruntime`). Keep `linux/models/v6` from the release archive beside the scripts. On GNOME, enable [AppIndicator and KStatusNotifierItem Support](https://extensions.gnome.org/extension/615/appindicator-support/) so the app's indicator appears in the top bar; Plasma has a system tray. Package names vary by distribution; on Debian or Ubuntu the GTK and indicator introspection packages are commonly `gir1.2-gtk-3.0` and `gir1.2-ayatanaappindicator3-0.1`.

```sh
python3 linux/screen_to_search.py --check
python3 linux/screen_to_search.py --install-desktop
```

Launch **Screen to Search** from your desktop's app launcher. The install step copies the Linux files and v6 models into your local data directory and creates the desktop entry needed for [portal app identification](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.host.portal.Registry.html). Your desktop asks you to assign and approve the capture shortcut when the app starts. Its suggested key is **F8**; use **Change shortcut** in Settings to choose another key. Hold the assigned shortcut for the duration configured in Settings (350 ms by default), or choose **Capture** from the indicator. A fullscreen capture overlay opens on each monitor. Click a word, drag across words, use Rectangle or Lasso, then choose **Copy text**, **Copy image**, **Search text**, or **Translate**. Settings also controls search engine, translation language, shortcut enablement, and launch at sign-in. A second launch opens Settings in the running app.

The desktop owns the global shortcut. Its portal reports presses and releases, but does not offer the Windows app's short-tap key replay, so a quick tap of the assigned shortcut is consumed by the desktop. The desktop decides which shortcuts it will allow. Both platforms use the same v6 model files, though their OCR wrappers may produce different word boxes. Mixed-DPI monitor layouts need an on-device check because the portal screenshot and GTK monitor geometry can use different scales.

### Other Linux desktops

The fallback uses Python 3 and Tkinter for its window, the same PP-OCRv6 models for local OCR, a desktop screenshot tool for region selection, and `wl-copy` or `xclip` for the clipboard.

Install `python3-tk`, `rapidocr`, `onnxruntime`, and the appropriate packages for your desktop:

| Session | Region capture | Clipboard |
| --- | --- | --- |
| Wayland with wlroots | `grim` and `slurp` | `wl-clipboard` |
| KDE Plasma X11 | `spectacle` | `xclip` |
| GNOME | `gnome-screenshot` or `flameshot` where supported by the session | `wl-clipboard` on Wayland, `xclip` on X11 |
| Other X11 desktops | `maim`, `flameshot`, or ImageMagick `import` | `xclip` |

Keep the `linux/models/v6` folder from the release archive with the scripts, or run from the repository checkout. Check your setup and start the app from the repository root:

```sh
python3 linux/screen_to_search.py --check
python3 linux/screen_to_search.py
```

Click **Capture region**, select an area using your desktop's capture tool, then use **Copy image**, **Copy text**, **Search text**, or **Translate**. The OCR text is editable; highlight part of it to use only that passage. Settings are saved in `$XDG_CONFIG_HOME/screen-to-search/settings.json` (or `~/.config/screen-to-search/settings.json`).

On other desktops, bind `python3 /absolute/path/to/linux/screen_to_search.py --capture` as a custom keyboard shortcut in your desktop's settings to launch directly into region selection. The region-selection window currently lacks the Wayland resident indicator, hold shortcut, and word-level overlay.

## macOS

The native macOS menu bar app requires macOS 14 or newer. It captures each display with ScreenCaptureKit, uses the bundled PP-OCRv6 models for word selection, and offers Smart, Text, Rectangle, and Lasso modes. Copy text or an image, search, and translate from the fullscreen overlay. The hold shortcut defaults to **F8**; the menu bar icon lets you change it to F9, F10, or Caps Lock and adjust the hold duration.

To build a `.app` from source on a Mac, install Xcode command line tools and Python 3, then run from the repository root:

```sh
python3 -m pip install rapidocr onnxruntime pyinstaller
sh macos/build-app.sh
open 'macos/dist/Screen to Search.app'
```

The build script bundles the Python OCR helper and v6 models inside the app. macOS asks for **Input Monitoring** to use the global hold shortcut and **Screen & System Audio Recording** to capture the display. If you decline Input Monitoring, **Capture now** remains available from the menu bar icon. The menu also controls search engine, translation language, and start at login.

## Privacy and limitations

Screen capture and text recognition happen locally. The app doesn't upload screenshots. **Search text** and **Translate** open your browser with the selected text when you choose those actions.

You can choose a text recognition language in Settings or leave it on automatic. Recognition can miss small or low-contrast text, and Windows may show protected content as a blank area in a capture.

## License

The app code is [MIT licensed](LICENSE). The bundled PaddleOCR v6 models and the local RapidOcrNet-derived OCR source are under Apache 2.0, so their [Apache license text](APACHE-2.0-LICENSE.txt) is included with the app. Other dependency licenses are listed in the [third-party notices](THIRD_PARTY_LICENSES.txt). Screen to Search is independent of Google and Samsung.
