import AppKit

enum SelectionMode: String, CaseIterable {
    case smart = "Smart"
    case text = "Text"
    case rectangle = "Rectangle"
    case lasso = "Lasso"
}

enum OverlayAction: String {
    case copyText = "Copy text"
    case copyImage = "Copy image"
    case search = "Search text"
    case translate = "Translate"
}

final class CaptureOverlay: NSWindow {
    let surface: CaptureSurface

    init(screen: NSScreen, image: CGImage,
         action: @escaping (OverlayAction, CaptureSurface) -> Void,
         close: @escaping () -> Void) {
        surface = CaptureSurface(frame: CGRect(origin: .zero, size: screen.frame.size), image: image)
        super.init(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false,
                   screen: screen)
        level = .screenSaver
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        isOpaque = true
        backgroundColor = .black
        hasShadow = false
        contentView = surface
        surface.action = action
        surface.close = close
        makeKeyAndOrderFront(nil)
        makeFirstResponder(surface)
    }

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

final class CaptureSurface: NSView {
    let image: CGImage
    var words: [OCRWord] = [] { didSet { needsDisplay = true } }
    var mode: SelectionMode = .smart { didSet { needsDisplay = true } }
    var selected = Set<Int>() { didSet { needsDisplay = true } }
    var selectionRect: CGRect? { didSet { needsDisplay = true } }
    var lasso: [CGPoint] = [] { didSet { needsDisplay = true } }
    var action: ((OverlayAction, CaptureSurface) -> Void)?
    var close: (() -> Void)?
    private var start: CGPoint?
    private var focused = 0
    private var toolbar: NSStackView!

    init(frame: CGRect, image: CGImage) {
        self.image = image
        super.init(frame: frame)
        buildToolbar()
    }

    required init?(coder: NSCoder) { fatalError("Use init(frame:image:)") }
    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { false }

    private var imageSize: CGSize { CGSize(width: image.width, height: image.height) }

    private func buildToolbar() {
        toolbar = NSStackView()
        toolbar.orientation = .horizontal
        toolbar.spacing = 6
        toolbar.distribution = .gravityAreas
        toolbar.translatesAutoresizingMaskIntoConstraints = false
        toolbar.wantsLayer = true
        toolbar.layer?.backgroundColor = NSColor.windowBackgroundColor.withAlphaComponent(0.95).cgColor
        toolbar.layer?.cornerRadius = 8
        addSubview(toolbar)
        NSLayoutConstraint.activate([
            toolbar.topAnchor.constraint(equalTo: topAnchor, constant: 14),
            toolbar.centerXAnchor.constraint(equalTo: centerXAnchor),
            toolbar.heightAnchor.constraint(equalToConstant: 42),
        ])
        let modes: [(String, SelectionMode)] = [("Smart (A)", .smart), ("Text (T)", .text),
                                                ("Rectangle (R)", .rectangle), ("Lasso (L)", .lasso)]
        for (title, choice) in modes {
            let button = NSButton(title: title, target: self, action: #selector(changeMode(_:)))
            button.bezelStyle = .rounded
            button.tag = SelectionMode.allCases.firstIndex(of: choice) ?? 0
            toolbar.addArrangedSubview(button)
        }
        for choice in [OverlayAction.copyText, .copyImage, .search, .translate] {
            let button = NSButton(title: choice.rawValue, target: self, action: #selector(performAction(_:)))
            button.bezelStyle = .rounded
            button.tag = [OverlayAction.copyText, .copyImage, .search, .translate].firstIndex(of: choice) ?? 0
            toolbar.addArrangedSubview(button)
        }
        let closeButton = NSButton(title: "Close (Esc)", target: self, action: #selector(closeOverlay))
        toolbar.addArrangedSubview(closeButton)
    }

    @objc private func changeMode(_ sender: NSButton) {
        mode = SelectionMode.allCases[sender.tag]
        selected.removeAll()
        selectionRect = nil
        lasso.removeAll()
    }

    @objc private func performAction(_ sender: NSButton) {
        let choices: [OverlayAction] = [.copyText, .copyImage, .search, .translate]
        action?(choices[sender.tag], self)
    }

    @objc private func closeOverlay() { close?() }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        NSImage(cgImage: image, size: bounds.size).draw(in: bounds)
        NSColor.black.withAlphaComponent(0.12).setFill()
        bounds.fill()
        for (index, word) in words.enumerated() where selected.contains(index) {
            NSColor.systemBlue.withAlphaComponent(0.45).setFill()
            word.rect(in: bounds.size, imageSize: imageSize).fill()
        }
        if let rect = selectionRect {
            NSColor.systemBlue.withAlphaComponent(0.18).setFill()
            rect.fill()
            NSColor.systemBlue.setStroke()
            let path = NSBezierPath(rect: rect)
            path.lineWidth = 2
            path.stroke()
        }
        if lasso.count > 1 {
            let path = NSBezierPath()
            path.move(to: lasso[0])
            for point in lasso.dropFirst() { path.line(to: point) }
            NSColor.systemBlue.setStroke()
            path.lineWidth = 2
            path.stroke()
        }
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        if toolbar.frame.contains(point) { return }
        start = point
        selectionRect = nil
        lasso = mode == .lasso ? [point] : []
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start else { return }
        let point = convert(event.locationInWindow, from: nil)
        if mode == .lasso {
            lasso.append(point)
        } else {
            selectionRect = CGRect(x: min(start.x, point.x), y: min(start.y, point.y),
                                   width: abs(start.x - point.x), height: abs(start.y - point.y))
        }
    }

    override func mouseUp(with event: NSEvent) {
        guard let start else { return }
        self.start = nil
        let end = convert(event.locationInWindow, from: nil)
        if mode == .lasso {
            lasso.append(end)
            selected = Set(words.indices.filter { pointInLasso(center(of: words[$0])) })
            return
        }
        let rect = CGRect(x: min(start.x, end.x), y: min(start.y, end.y),
                          width: abs(start.x - end.x), height: abs(start.y - end.y))
        if mode == .rectangle {
            selectionRect = rect.width > 3 && rect.height > 3 ? rect : nil
            selected.removeAll()
        } else if rect.width <= 3 && rect.height <= 3 {
            if let index = words.indices.first(where: {
                words[$0].rect(in: bounds.size, imageSize: imageSize).contains(end)
            }) {
                selected = [index]
                focused = index
            } else {
                selected.removeAll()
            }
            selectionRect = nil
        } else {
            selected = Set(words.indices.filter { rect.contains(center(of: words[$0])) })
            if mode == .text || !selected.isEmpty { selectionRect = nil }
        }
    }

    private func center(of word: OCRWord) -> CGPoint {
        let rect = word.rect(in: bounds.size, imageSize: imageSize)
        return CGPoint(x: rect.midX, y: rect.midY)
    }

    private func pointInLasso(_ point: CGPoint) -> Bool {
        guard lasso.count > 2 else { return false }
        var inside = false
        var previous = lasso.last!
        for current in lasso {
            if (current.y > point.y) != (previous.y > point.y) &&
                point.x < (previous.x - current.x) * (point.y - current.y) /
                    (previous.y - current.y) + current.x {
                inside.toggle()
            }
            previous = current
        }
        return inside
    }

    var selectedText: String {
        var lines: [String] = []
        var previous: [Int]?
        for index in selected.sorted() {
            let word = words[index]
            if previous == word.line, !lines.isEmpty {
                lines[lines.count - 1] += " " + word.text
            } else {
                lines.append(word.text)
            }
            previous = word.line
        }
        return lines.joined(separator: "\n")
    }

    var selectedImage: NSImage? {
        let rect: CGRect
        if mode == .lasso && !lasso.isEmpty {
            let xs = lasso.map(\.x), ys = lasso.map(\.y)
            rect = CGRect(x: xs.min()!, y: ys.min()!, width: xs.max()! - xs.min()!,
                          height: ys.max()! - ys.min()!)
        } else if let selectionRect {
            rect = selectionRect
        } else if !selected.isEmpty {
            rect = selected.reduce(CGRect.null) { partial, index in
                partial.union(words[index].rect(in: bounds.size, imageSize: imageSize))
            }
        } else {
            return nil
        }
        guard rect.width > 1, rect.height > 1 else { return nil }
        let pixelRect = CGRect(x: rect.minX * imageSize.width / bounds.width,
                               y: (bounds.height - rect.maxY) * imageSize.height / bounds.height,
                               width: rect.width * imageSize.width / bounds.width,
                               height: rect.height * imageSize.height / bounds.height).integral
        guard let crop = image.cropping(to: pixelRect) else { return nil }
        return NSImage(cgImage: crop, size: pixelRect.size)
    }

    override func keyDown(with event: NSEvent) {
        switch event.charactersIgnoringModifiers?.lowercased() {
        case "a": mode = .smart
        case "t": mode = .text
        case "r": mode = .rectangle
        case "l": mode = .lasso
        case "\u{1b}": close?()
        case "\u{7f}": selected.removeAll(); selectionRect = nil; lasso.removeAll()
        default:
            if event.keyCode == 123 || event.keyCode == 126 {
                focused = max(0, focused - 1)
                selected = [focused]
            } else if event.keyCode == 124 || event.keyCode == 125 {
                focused = min(max(0, words.count - 1), focused + 1)
                selected = [focused]
            } else {
                super.keyDown(with: event)
            }
        }
    }
}
