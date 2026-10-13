# Screen to Search

![Screen to Search logo](src/ScreenToSearch/Assets/ScreenToSearch.svg)

Want to copy text from a screenshot or look up something you spotted in an app? Screen to Search lets you select what's on your display, even when the app you're looking at won't let you highlight it. Copy text or an image, search for selected text, or open it in Google Translate.

## Windows

### Get started

Download `ScreenToSearch-win-x64.zip` from the [Releases page](../../releases), extract the whole archive, and run `ScreenToSearch.exe`. Keep the `models` folder next to the app. The release includes the .NET runtime, so there is no separate runtime installation.

The app stays in the system tray. Double-click its icon to open Settings, or right-click it to open the capture screen, open Settings, or quit. Closing Settings doesn't quit the app. You can choose whether it starts when you sign in to Windows.

To start automatically, enable **Launch at sign-in** in Settings. Keep the extracted app in a permanent folder before enabling it; Windows starts that copy in the tray when you sign in.

While waiting in the tray, the app uses keyboard events rather than polling and takes no screenshots. Settings is created when first opened. OCR models load on the first capture and are released after 60 seconds without recognition; the next capture reloads them. Inference workers sleep between tasks, and large temporary inference buffers are not kept in a CPU memory arena. This reduces background memory use at the cost of some extra recognition latency after an idle period. The hold timer runs only while the shortcut is pressed.

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

That build needs the .NET 10 Desktop Runtime. To build the compact, self-contained Windows release package instead, run:

```powershell
dotnet restore "src/ScreenToSearch/ScreenToSearch.csproj" -r win-x64
dotnet publish "src/ScreenToSearch/ScreenToSearch.csproj" -c Release -r win-x64 --self-contained true --no-restore -o publish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false
Remove-Item publish/*.lib -ErrorAction SilentlyContinue
```

Keep the entire `publish` folder together, including `models`. For a GitHub release, zip the **contents** of `publish`; the folder itself is generated and shouldn't be committed. Quit any older copy of the app from the tray before running a different build.

#### Checks for contributors

The app includes a couple of local checks. After publishing it, run these commands from the repository root. Each one writes a JSON report you can inspect:

```powershell
.\publish\ScreenToSearch.exe --stage2-selftest .\stage2-report.json
.\publish\ScreenToSearch.exe --stage0-selftest .\stage0-report.json
.\publish\ScreenToSearch.exe --ocr-lifecycle-selftest .\ocr-lifecycle-report.json
```

For a closer look at text recognition on an image, use `--ocr-image-report <image-path> <report-json-path>`. Windows uses one PP-OCRv6 detector and recognizer for all text recognition.

The OCR lifecycle check takes just over a minute. It verifies loading on demand, warm reuse, cancellation, automatic release after inactivity, and recognition after reloading, and reports timings and private memory use.

## Linux

The Linux entry point is [linux/screen_to_search.py](linux/screen_to_search.py). The GTK app uses the same fullscreen, darkened OCR overlay on GNOME, KDE Plasma, X11 desktops, and other Wayland compositors. It uses the bundled PP-OCRv6 models through RapidOCR for Python. Install Python 3, GTK 3's Python introspection package, `python3-gi`, `python3-cairo`, `python3-gi-cairo`, RapidOCR 3.10 or newer, `onnxruntime`, and `wl-clipboard` on Wayland or `xclip` on X11. An AppIndicator library provides a panel icon where the desktop supports one; otherwise, launch the app again to reopen Settings. Keep `linux/models/v6` beside the scripts.

Download `ScreenToSearch-linux.zip` from the [Releases page](../../releases) and extract it, or use this repository checkout. Install Python packages into the environment used by the launcher (`python3 -m pip install "rapidocr>=3.10.0" onnxruntime`). Package names vary by distribution; on Debian or Ubuntu the GTK and indicator introspection packages are commonly `gir1.2-gtk-3.0` and `gir1.2-ayatanaappindicator3-0.1`.

Linux loads OCR on demand and releases the shared engine after 60 seconds without recognition. Requests are serialized so recognition and unloading cannot overlap. OCR workers sleep between tasks, and temporary inference buffers are not retained in a CPU memory arena. When a tray icon is available, Settings widgets are created only when first opened.

```sh
python3 linux/screen_to_search.py --check
python3 linux/screen_to_search.py --install-desktop
```

Launch **Screen to Search** from your desktop's app launcher. The install step copies the scripts and v6 models into your local data directory and creates a desktop identity for portal permissions. Hold the assigned shortcut for the duration configured in Settings (350 ms by default), or choose **Capture** from the indicator or Settings. A darkened fullscreen overlay opens on each monitor. Click or drag across words, use Rectangle or Lasso, then choose **Copy text**, **Copy image**, **Search text**, or **Translate**. Settings also controls search engine, translation language, shortcut enablement, and launch at sign-in.

| Session | Fullscreen capture | Hold shortcut |
| --- | --- | --- |
| GNOME/KDE Wayland with both portals | [Screenshot portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Screenshot.html) | [Global Shortcuts portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html), with F8 suggested |
| Other Wayland with both portals | Screenshot portal | Global Shortcuts portal |
| Wayland without a shortcut portal | Screenshot portal, `grim`, or `spectacle` | Compositor press/release bindings below |
| X11 desktops | `maim`, ImageMagick `import`, `gnome-screenshot`, or `spectacle` | X11 key grab via `python3-xlib`; choose F8, F9, or F10 in Settings |

On GNOME, enable [AppIndicator and KStatusNotifierItem Support](https://extensions.gnome.org/extension/615/appindicator-support/) for a top-bar icon. GNOME 48 or newer provides the Global Shortcuts portal used for the hold shortcut. On Wayland, the desktop chooses which shortcuts it allows. On X11, free F8, F9, or F10 from any existing desktop binding before choosing it in Settings. The Linux shortcut consumes a quick tap; it does not replay that tap to the focused app.

For a Wayland compositor without a Global Shortcuts portal, start the app from its launcher and bind both press and release to its command interface. For [Sway](https://github.com/swaywm/sway/blob/master/sway/sway.5.scd), add this to `~/.config/sway/config` and reload Sway:

```text
bindsym --no-repeat F8 exec $HOME/.local/share/screen-to-search/launch --shortcut-pressed
bindsym --release F8 exec $HOME/.local/share/screen-to-search/launch --shortcut-released
```

Other compositors with press and release bindings can call the same two commands. A compositor offering only an activation command can bind `launch --capture` for immediate fullscreen selection, but cannot report the length of a hold. Settings are saved in `$XDG_CONFIG_HOME/screen-to-search/settings.json` (or `~/.config/screen-to-search/settings.json`). When GTK is unavailable, the older Tkinter region-capture window remains as a fallback.

## macOS

The native macOS menu bar app requires macOS 14 or newer. It captures each display with ScreenCaptureKit, uses the bundled PP-OCRv6 models for word selection, and offers Smart, Text, Rectangle, and Lasso modes. Copy text or an image, search, and translate from the fullscreen overlay. The hold shortcut defaults to **Control+Option+Space**; the menu bar icon lets you change it to F8, F9, F10, or Caps Lock and adjust the hold duration.

To build a `.app` from source on a Mac, install Xcode command line tools and Python 3, then run from the repository root:

```sh
python3 -m pip install "rapidocr>=3.10.0" onnxruntime pyinstaller
sh macos/build-app.sh
open 'macos/dist/Screen to Search.app'
```

The build script bundles the Python OCR helper and v6 models inside the app. macOS asks for **Input Monitoring** to use the global hold shortcut and **Screen & System Audio Recording** to capture the display. If you decline Input Monitoring, **Capture now** remains available from the menu bar icon. The menu also controls search engine, translation language, and start at login.

The macOS OCR helper runs only for a recognition request and exits when finished, releasing its models and native buffers immediately. It uses the shared Linux/macOS OCR engine with sleeping inference workers and no retained CPU memory arena. The menu bar host keeps no OCR engine running while idle.

## Privacy and limitations

Screen capture and text recognition happen locally. The app doesn't upload screenshots. **Search text** and **Translate** open your browser with the selected text when you choose those actions.

You can choose a text recognition language in Settings or leave it on automatic. Recognition can miss small or low-contrast text, and Windows may show protected content as a blank area in a capture.

## License

The app code is [MIT licensed](LICENSE). The bundled PaddleOCR v6 models and the local RapidOcrNet-derived OCR source are under Apache 2.0, so their [Apache license text](APACHE-2.0-LICENSE.txt) is included with the app. Other dependency licenses are listed in the [third-party notices](THIRD_PARTY_LICENSES.txt). Screen to Search is independent of Google and Samsung.
