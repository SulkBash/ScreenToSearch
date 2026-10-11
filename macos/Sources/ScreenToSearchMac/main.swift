import AppKit
import CoreGraphics
import ScreenCaptureKit
import ServiceManagement

private let shortcutKeys: [(String, Int64)] = [("F8", 100), ("F9", 101), ("F10", 109), ("Caps Lock", 57)]
private let languages: [(String, String)] = [("English", "en"), ("Spanish", "es"),
    ("French", "fr"), ("German", "de"), ("Portuguese", "pt"), ("Italian", "it"),
    ("Japanese", "ja"), ("Korean", "ko"), ("Chinese (Simplified)", "zh-CN"),
    ("Arabic", "ar"), ("Hindi", "hi"), ("Russian", "ru")]

@MainActor final class MacHost: NSObject, NSApplicationDelegate {
    private let status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private var eventTap: CFMachPort?
    private var eventSource: CFRunLoopSource?
    private var holdTask: DispatchWorkItem?
    private var overlays: [CaptureOverlay] = []
    private var capturing = false

    private var shortcutCode: Int64 {
        let name = UserDefaults.standard.string(forKey: "shortcut") ?? "F8"
        return shortcutKeys.first(where: { $0.0 == name })?.1 ?? 100
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        status.button?.title = "◎"
        buildMenu()
        installKeyboardTap()
    }

    private func buildMenu() {
        let menu = NSMenu()
        let capture = NSMenuItem(title: "Capture now", action: #selector(captureNow), keyEquivalent: "")
        capture.target = self
        menu.addItem(capture)

        let shortcut = NSMenuItem(title: "Hold shortcut", action: nil, keyEquivalent: "")
        let shortcutMenu = NSMenu()
        for (name, _) in shortcutKeys {
            let item = NSMenuItem(title: name, action: #selector(selectShortcut(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = name
            item.state = (UserDefaults.standard.string(forKey: "shortcut") ?? "F8") == name ? .on : .off
            shortcutMenu.addItem(item)
        }
        shortcut.submenu = shortcutMenu
        menu.addItem(shortcut)

        let hold = NSMenuItem(title: "Hold duration", action: nil, keyEquivalent: "")
        let holdMenu = NSMenu()
        for milliseconds in [200, 350, 500, 750, 1000] {
            let item = NSMenuItem(title: "\(milliseconds) ms", action: #selector(selectDuration(_:)), keyEquivalent: "")
            item.target = self
            item.tag = milliseconds
            item.state = (UserDefaults.standard.integer(forKey: "holdMilliseconds") == 0 ? 350 :
                          UserDefaults.standard.integer(forKey: "holdMilliseconds")) == milliseconds ? .on : .off
            holdMenu.addItem(item)
        }
        hold.submenu = holdMenu
        menu.addItem(hold)

        let engine = NSMenuItem(title: "Search engine", action: nil, keyEquivalent: "")
        let engineMenu = NSMenu()
        for name in ["Google", "Bing", "DuckDuckGo"] {
            let item = NSMenuItem(title: name, action: #selector(selectEngine(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = name
            item.state = (UserDefaults.standard.string(forKey: "searchEngine") ?? "Google") == name ? .on : .off
            engineMenu.addItem(item)
        }
        engine.submenu = engineMenu
        menu.addItem(engine)
        let translation = NSMenuItem(title: "Translate to", action: nil, keyEquivalent: "")
        let translationMenu = NSMenu()
        for (name, code) in languages {
            let item = NSMenuItem(title: name, action: #selector(selectLanguage(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = code
            item.state = (UserDefaults.standard.string(forKey: "translationLanguage") ?? "en") == code ? .on : .off
            translationMenu.addItem(item)
        }
        translation.submenu = translationMenu
        menu.addItem(translation)
        let startup = NSMenuItem(title: "Start at login", action: #selector(toggleStartup(_:)), keyEquivalent: "")
        startup.target = self
        startup.state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(startup)
        menu.addItem(.separator())
        let quit = NSMenuItem(title: "Quit", action: #selector(quitApp), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
        status.menu = menu
    }

    @objc private func selectShortcut(_ sender: NSMenuItem) {
        UserDefaults.standard.set(sender.representedObject as? String, forKey: "shortcut")
        buildMenu()
    }

    @objc private func selectDuration(_ sender: NSMenuItem) {
        UserDefaults.standard.set(sender.tag, forKey: "holdMilliseconds")
        buildMenu()
    }

    @objc private func selectEngine(_ sender: NSMenuItem) {
        UserDefaults.standard.set(sender.representedObject as? String, forKey: "searchEngine")
        buildMenu()
    }

    @objc private func selectLanguage(_ sender: NSMenuItem) {
        UserDefaults.standard.set(sender.representedObject as? String, forKey: "translationLanguage")
        buildMenu()
    }

    @objc private func toggleStartup(_ sender: NSMenuItem) {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            showError("Start at login: \(error.localizedDescription)")
        }
        buildMenu()
    }

    @objc private func quitApp() {
        dismissOverlays()
        NSApp.terminate(nil)
    }

    private func installKeyboardTap() {
        let mask = (CGEventMask(1) << CGEventType.keyDown.rawValue) |
                   (CGEventMask(1) << CGEventType.keyUp.rawValue) |
                   (CGEventMask(1) << CGEventType.flagsChanged.rawValue)
        eventTap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap,
                                     options: .listenOnly, eventsOfInterest: mask,
                                     callback: { _, type, event, info in
            guard let info else { return Unmanaged.passUnretained(event) }
            let host = Unmanaged<MacHost>.fromOpaque(info).takeUnretainedValue()
            let code = event.getIntegerValueField(.keyboardEventKeycode)
            let pressed = type == .keyDown ||
                (type == .flagsChanged && event.flags.contains(.maskAlphaShift))
            DispatchQueue.main.async { host.keyboardEvent(type: type, code: code, pressed: pressed) }
            return Unmanaged.passUnretained(event)
        }, userInfo: Unmanaged.passUnretained(self).toOpaque())
        guard let eventTap else {
            _ = CGRequestListenEventAccess()
            showError("Allow Screen to Search in System Settings → Privacy & Security → Input Monitoring, then restart the app. You can use Capture now from the menu meanwhile.")
            return
        }
        eventSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, eventTap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), eventSource, .commonModes)
        CGEvent.tapEnable(tap: eventTap, enable: true)
    }

    private func keyboardEvent(type: CGEventType, code: Int64, pressed: Bool) {
        if type == .tapDisabledByTimeout, let eventTap {
            CGEvent.tapEnable(tap: eventTap, enable: true)
            return
        }
        guard code == shortcutCode, type == .keyDown || type == .keyUp || type == .flagsChanged else { return }
        if pressed {
            guard holdTask == nil else { return }
            let task = DispatchWorkItem { [weak self] in
                self?.holdTask = nil
                self?.captureNow()
            }
            holdTask = task
            let duration = UserDefaults.standard.integer(forKey: "holdMilliseconds")
            DispatchQueue.main.asyncAfter(deadline: .now() + .milliseconds(duration == 0 ? 350 : duration), execute: task)
        } else {
            holdTask?.cancel()
            holdTask = nil
        }
    }

    @objc private func captureNow() {
        guard !capturing else { return }
        if !overlays.isEmpty { overlays.first?.makeKeyAndOrderFront(nil); return }
        capturing = true
        Task {
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                var captures: [(NSScreen, CGImage)] = []
                for screen in NSScreen.screens {
                    guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber,
                          let display = content.displays.first(where: { $0.displayID == number.uint32Value }) else { continue }
                    let filter = SCContentFilter(display: display, excludingWindows: [])
                    let config = SCStreamConfiguration()
                    config.width = display.width
                    config.height = display.height
                    config.showsCursor = false
                    let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
                    captures.append((screen, image))
                }
                if captures.isEmpty { throw NSError(domain: "ScreenToSearch", code: 2,
                    userInfo: [NSLocalizedDescriptionKey: "No displays were available for capture."]) }
                for (screen, image) in captures {
                    let overlay = CaptureOverlay(screen: screen, image: image,
                                                 action: { [weak self] choice, surface in self?.perform(choice, on: surface) },
                                                 close: { [weak self] in self?.dismissOverlays() })
                    overlays.append(overlay)
                    DispatchQueue.global(qos: .userInitiated).async {
                        do {
                            let words = try OCRBridge.recognize(image)
                            Task { @MainActor in overlay.surface.words = words }
                        } catch {
                            Task { @MainActor in self.showError("OCR: \(error.localizedDescription)") }
                        }
                    }
                }
            } catch {
                showError("Screen capture: \(error.localizedDescription). Allow Screen to Search in System Settings → Privacy & Security → Screen & System Audio Recording.")
            }
            capturing = false
        }
    }

    private func perform(_ action: OverlayAction, on surface: CaptureSurface) {
        switch action {
        case .copyText:
            guard !surface.selectedText.isEmpty else { showError("Select text first."); return }
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(surface.selectedText, forType: .string)
        case .copyImage:
            guard let image = surface.selectedImage else { showError("Select an image area first."); return }
            NSPasteboard.general.clearContents()
            NSPasteboard.general.writeObjects([image])
        case .search, .translate:
            let text = surface.selectedText
            guard !text.isEmpty else { showError("Select text first."); return }
            var components: URLComponents
            if action == .translate {
                let language = UserDefaults.standard.string(forKey: "translationLanguage") ?? "en"
                components = URLComponents(string: "https://translate.google.com/")!
                components.queryItems = [URLQueryItem(name: "sl", value: "auto"),
                                         URLQueryItem(name: "tl", value: language),
                                         URLQueryItem(name: "text", value: text),
                                         URLQueryItem(name: "op", value: "translate")]
            } else {
                switch UserDefaults.standard.string(forKey: "searchEngine") ?? "Google" {
                case "Bing": components = URLComponents(string: "https://www.bing.com/search")!
                case "DuckDuckGo": components = URLComponents(string: "https://duckduckgo.com/")!
                default: components = URLComponents(string: "https://www.google.com/search")!
                }
                components.queryItems = [URLQueryItem(name: "q", value: text)]
            }
            if let url = components.url { NSWorkspace.shared.open(url) }
        }
        dismissOverlays()
    }

    private func dismissOverlays() {
        for overlay in overlays { overlay.orderOut(nil) }
        overlays.removeAll()
    }

    private func showError(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "Screen to Search"
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.runModal()
    }
}

if CommandLine.arguments.contains("--overlay-selftest") {
    MainActor.assumeIsolated {
        _ = NSApplication.shared
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 320, pixelsHigh: 200,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
            let image = bitmap.cgImage else { fatalError("Could not make test image") }
        let surface = CaptureSurface(frame: CGRect(x: 0, y: 0, width: 320, height: 200), image: image)
        surface.words = [OCRWord(text: "Hello", x: 10, y: 20, width: 50, height: 20, line: [0, 0, 0])]
        surface.selected = [0]
        precondition(surface.selectedText == "Hello", "Word selection failed")
        precondition(surface.selectedImage != nil, "Image crop failed")
        print("macOS overlay word selection and image crop passed")
    }
} else if CommandLine.arguments.contains("--check") {
    let resources = OCRBridge.resources
    let required = ["ocr_helper.py", "v6_ocr.py", "word_selection.py",
                    "models/v6/PP-OCRv6_det_small.onnx", "models/v6/PP-OCRv6_rec_small.onnx",
                    "models/v6/ppocrv6_small_dict.txt"]
    let missing = required.filter { !FileManager.default.fileExists(atPath: resources.appendingPathComponent($0).path) }
    if !missing.isEmpty {
        fputs("Missing macOS resources: \(missing.joined(separator: ", "))\n", stderr)
        exit(1)
    }
    print("Screen to Search macOS resources ready (PP-OCRv6)")
} else {
    MainActor.assumeIsolated {
        let app = NSApplication.shared
        let host = MacHost()
        app.delegate = host
        app.run()
    }
}
