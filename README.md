# Screen to Search

![Screen to Search logo](src/ScreenToSearch/Assets/ScreenToSearch.svg)

Want to copy text from a screenshot or look up something you spotted in an app? Screen to Search lets you select what's on your display, even when the app you're looking at won't let you highlight it. Copy text or an image, search for selected text, or open it in Google Translate.

It runs from the Windows system tray on Windows 10 and 11 (x64). You don't need an account, and text recognition happens on your computer.

## Get started

Download `ScreenToSearch-win-x64.zip` from the [Releases page](../../releases), extract the whole archive, and run `ScreenToSearch.exe`. Keep the `models` folder next to the app. The release includes the .NET runtime, so there is no separate runtime installation.

The app stays in the system tray. Double-click its icon to open Settings, or right-click it to open the capture screen, open Settings, or quit. Closing Settings doesn't quit the app. You can choose whether it starts when you sign in to Windows.

## Use it

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

## Privacy and limitations

Screen capture and text recognition happen locally. The app doesn't upload screenshots. **Search text** and **Translate** open your browser with the selected text when you choose those actions.

You can choose a text recognition language in Settings or leave it on automatic. Recognition can miss small or low-contrast text, and Windows may show protected content as a blank area in a capture.

## Build from source

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

### Checks for contributors

The app includes a couple of local checks. After publishing it, run these commands from the repository root. Each one writes a JSON report you can inspect:

```powershell
.\publish\ScreenToSearch.exe --stage2-selftest .\stage2-report.json
.\publish\ScreenToSearch.exe --stage0-selftest .\stage0-report.json
```

For a closer look at text recognition on an image, use `--ocr-image-report <image-path> <report-json-path>`. To compare the bundled recognition models, use `--ocr-model-compare <image-path> <report-json-path>`.

## License

The app code is [MIT licensed](LICENSE). The bundled PaddleOCR models and RapidOcrNet dependency are under Apache 2.0, so their [Apache license text](APACHE-2.0-LICENSE.txt) is included with the app. Other dependency licenses are listed in the [third-party notices](THIRD_PARTY_LICENSES.txt). Screen to Search is independent of Google and Samsung.
