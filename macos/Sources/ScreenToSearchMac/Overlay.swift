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

struct TextPosition: Comparable {
    let word: Int
    let character: Int

    static func < (left: TextPosition, right: TextPosition) -> Bool {
        left.word < right.word || (left.word == right.word && left.character < right.character)
    }
}

struct CharacterRange {
    let first: TextPosition
    let last: TextPosition

    init(_ first: TextPosition, _ last: TextPosition) {
        self.first = min(first, last)
        self.last = max(first, last)
    }
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
    private struct GridCell: Hashable { let x: Int; let y: Int }
    private static let gridSize: CGFloat = 96
    let image: CGImage
    var words: [OCRWord] = [] {
        didSet {
            focused = min(focused, max(0, words.count - 1))
            focusedCharacter = words.indices.contains(focused)
                ? min(focusedCharacter, max(1, words[focused].characters?.count ?? 0) - 1) : 0
            rebuildWordIndex()
            needsDisplay = true
        }
    }
    var mode: SelectionMode = .smart { didSet { needsDisplay = true } }
    var selected = Set<Int>() { didSet { needsDisplay = true } }
    var characterRange: CharacterRange? { didSet { needsDisplay = true } }
    var selectionRect: CGRect? { didSet { needsDisplay = true } }
    var lasso: [CGPoint] = [] { didSet { needsDisplay = true } }
    var action: ((OverlayAction, CaptureSurface) -> Void)?
    var close: (() -> Void)?
    private var start: CGPoint?
    private var focused = 0
    private var focusedCharacter = 0
    private var keyboardAnchor: TextPosition?
    private var wordCells: [GridCell: [Int]] = [:]
    private var undoSelection: (Set<Int>, CGRect?, [CGPoint], CharacterRange?)?
    private var toolbar: NSStackView!

    init(frame: CGRect, image: CGImage) {
        self.image = image
        super.init(frame: frame)
        buildToolbar()
    }

    required init?(coder: NSCoder) { fatalError("Use init(frame:image:)") }
    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { false }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        rebuildWordIndex()
    }

    private var imageSize: CGSize { CGSize(width: image.width, height: image.height) }

    private func rebuildWordIndex() {
        wordCells.removeAll(keepingCapacity: true)
        guard bounds.width > 0 && bounds.height > 0 else { return }
        for (index, word) in words.enumerated() {
            let rect = word.rect(in: bounds.size, imageSize: imageSize)
            let left = Int(floor((rect.minX - 30) / Self.gridSize))
            let right = Int(floor((rect.maxX + 30) / Self.gridSize))
            let bottom = Int(floor((rect.minY - 3) / Self.gridSize))
            let top = Int(floor((rect.maxY + 3) / Self.gridSize))
            for y in bottom...top {
                for x in left...right { wordCells[GridCell(x: x, y: y), default: []].append(index) }
            }
        }
    }

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
        characterRange = nil
        keyboardAnchor = nil
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
        for index in selected where words.indices.contains(index) {
            let word = words[index]
            NSColor.systemBlue.withAlphaComponent(0.45).setFill()
            selectedFragment(index, word: word).rect.fill()
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
        undoSelection = (selected, selectionRect, lasso, characterRange)
        start = point
        characterRange = nil
        keyboardAnchor = nil
        selectionRect = nil
        lasso = mode == .lasso ? [point] : []
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start else { return }
        let point = convert(event.locationInWindow, from: nil)
        if mode == .lasso {
            lasso.append(point)
        } else if (mode == .smart || mode == .text) && selectText(from: start, to: point, moved: true) {
            selectionRect = nil
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
            selected = Set(words.indices.filter { rect.contains(center(of: words[$0])) })
        } else if selectText(from: start, to: end, moved: rect.width > 3 || rect.height > 3) {
            selectionRect = nil
        } else if rect.width <= 3 && rect.height <= 3 {
            selected.removeAll()
            selectionRect = nil
        } else {
            characterRange = nil
            selected = Set(words.indices.filter { rect.contains(center(of: words[$0])) })
            if mode == .text || !selected.isEmpty { selectionRect = nil }
        }
    }

    // Smart mode preserves whole-word drags; Text mode can select inside a recognized word.
    @discardableResult
    func selectText(from start: CGPoint, to end: CGPoint, moved: Bool) -> Bool {
        guard let firstWord = word(at: start) else { return false }
        let lastWord: Int
        if moved {
            guard let found = word(at: end, extended: true) else { return false }
            lastWord = found
        } else {
            lastWord = firstWord
        }
        selected = Set(min(firstWord, lastWord)...max(firstWord, lastWord))
        focused = lastWord
        if mode == .text {
            let first = TextPosition(word: firstWord, character: character(at: start, in: firstWord))
            let last = TextPosition(word: lastWord, character: character(at: moved ? end : start, in: lastWord))
            characterRange = CharacterRange(first, last)
            focusedCharacter = last.character
            keyboardAnchor = first
        } else {
            characterRange = nil
            focusedCharacter = 0
            keyboardAnchor = nil
        }
        return true
    }

    private func word(at point: CGPoint, extended: Bool = false) -> Int? {
        var best: (index: Int, distance: CGFloat, center: CGFloat)?
        let cell = GridCell(x: Int(floor(point.x / Self.gridSize)),
                            y: Int(floor(point.y / Self.gridSize)))
        for index in wordCells[cell] ?? [] {
            let word = words[index]
            let rect = word.rect(in: bounds.size, imageSize: imageSize)
            let dx = max(0, max(rect.minX - point.x, point.x - rect.maxX))
            let dy = max(0, max(rect.minY - point.y, point.y - rect.maxY))
            let padding = extended ? max(8, min(30, rect.height * 0.75)) : 3
            guard dx <= padding && dy <= 3 else { continue }
            let distance = dx * dx + dy * dy
            let cx = point.x - rect.midX
            let cy = point.y - rect.midY
            let center = cx * cx + cy * cy
            if let old = best, old.distance < distance || (old.distance == distance && old.center <= center) {
                continue
            }
            best = (index, distance, center)
        }
        return best?.index
    }

    private func character(at point: CGPoint, in word: Int) -> Int {
        guard let characters = words[word].characters, !characters.isEmpty else { return 0 }
        return characters.indices.min { first, last in
            let a = characters[first].rect(in: bounds.size, imageSize: imageSize)
            let b = characters[last].rect(in: bounds.size, imageSize: imageSize)
            let ax = point.x - a.midX, ay = point.y - a.midY
            let bx = point.x - b.midX, by = point.y - b.midY
            return ax * ax + ay * ay < bx * bx + by * by
        } ?? 0
    }

    private func selectedFragment(_ index: Int, word: OCRWord) -> (text: String, rect: CGRect) {
        let full = (word.text, word.rect(in: bounds.size, imageSize: imageSize))
        guard let range = characterRange, index >= range.first.word, index <= range.last.word,
              let characters = word.characters, !characters.isEmpty else { return full }
        let first = index == range.first.word ? range.first.character : 0
        let last = index == range.last.word ? range.last.character : characters.count - 1
        guard first >= 0, last < characters.count, first <= last else { return full }
        let parts = characters[first...last]
        return (parts.map(\.text).joined(), parts.reduce(CGRect.null) {
            $0.union($1.rect(in: bounds.size, imageSize: imageSize))
        })
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
        var previousText: String?
        for index in selected.sorted() {
            let word = words[index]
            let text = selectedFragment(index, word: word).text
            if previous == word.line, !lines.isEmpty {
                let space = word.spaceBefore ?? !(previousText.map { endsInCJK($0) } == true && startsWithCJK(text))
                lines[lines.count - 1] += (space ? " " : "") + text
            } else {
                lines.append(text)
            }
            previous = word.line
            previousText = text
        }
        return lines.joined(separator: "\n")
    }

    private func isCJK(_ scalar: Unicode.Scalar) -> Bool {
        let value = scalar.value
        return (0x2E80...0x9FFF).contains(value) || (0xAC00...0xD7AF).contains(value)
            || (0xF900...0xFAFF).contains(value) || (0x3040...0x30FF).contains(value)
    }

    private func endsInCJK(_ text: String) -> Bool {
        text.unicodeScalars.last.map(isCJK) ?? false
    }

    private func startsWithCJK(_ text: String) -> Bool {
        text.unicodeScalars.first.map(isCJK) ?? false
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
                partial.union(selectedFragment(index, word: words[index]).rect)
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
        let cropped = NSImage(cgImage: crop, size: pixelRect.size)
        guard mode == .lasso && lasso.count > 2 else { return cropped }
        let masked = NSImage(size: pixelRect.size)
        masked.lockFocus()
        let path = NSBezierPath()
        for (index, point) in lasso.enumerated() {
            let local = CGPoint(x: (point.x - rect.minX) * pixelRect.width / rect.width,
                                y: (point.y - rect.minY) * pixelRect.height / rect.height)
            if index == 0 { path.move(to: local) } else { path.line(to: local) }
        }
        path.close()
        path.addClip()
        cropped.draw(in: CGRect(origin: .zero, size: pixelRect.size))
        masked.unlockFocus()
        return masked
    }

    private func moveCharacter(_ position: TextPosition, step: Int) -> TextPosition {
        var word = position.word
        var character = position.character
        if step < 0 {
            if character > 0 { character -= 1 }
            else if word > 0 {
                word -= 1
                character = max(1, words[word].characters?.count ?? 0) - 1
            }
        } else if character + 1 < max(1, words[word].characters?.count ?? 0) {
            character += 1
        } else if word + 1 < words.count {
            word += 1
            character = 0
        }
        return TextPosition(word: word, character: character)
    }

    override func keyDown(with event: NSEvent) {
        if event.modifierFlags.contains(.command), event.charactersIgnoringModifiers?.lowercased() == "z" {
            if let previous = undoSelection {
                selected = previous.0
                selectionRect = previous.1
                lasso = previous.2
                characterRange = previous.3
                keyboardAnchor = nil
            }
            return
        }
        switch event.charactersIgnoringModifiers?.lowercased() {
        case "a": mode = .smart; keyboardAnchor = nil
        case "t": mode = .text; keyboardAnchor = nil
        case "r": mode = .rectangle
        case "l": mode = .lasso
        case "\u{1b}": close?()
        case "\u{7f}":
            selected.removeAll(); selectionRect = nil; lasso.removeAll(); characterRange = nil; keyboardAnchor = nil
        case "\r":
            if words.indices.contains(focused) {
                selected = [focused]
                if mode == .text {
                    let position = TextPosition(word: focused, character: focusedCharacter)
                    characterRange = CharacterRange(position, position)
                    keyboardAnchor = position
                } else { characterRange = nil }
            }
        default:
            if [123, 124, 125, 126].contains(event.keyCode), !words.isEmpty {
                let step = event.keyCode == 123 || event.keyCode == 126 ? -1 : 1
                let old = TextPosition(word: focused, character: focusedCharacter)
                let next: TextPosition
                if mode == .text && (event.keyCode == 123 || event.keyCode == 124) {
                    next = moveCharacter(old, step: step)
                } else {
                    let index = min(max(0, focused + step), words.count - 1)
                    next = TextPosition(word: index, character: min(focusedCharacter,
                        max(1, words[index].characters?.count ?? 0) - 1))
                }
                undoSelection = (selected, selectionRect, lasso, characterRange)
                focused = next.word
                focusedCharacter = next.character
                if mode == .text {
                    if event.modifierFlags.contains(.shift) {
                        if keyboardAnchor == nil { keyboardAnchor = old }
                    } else { keyboardAnchor = nil }
                    let range = CharacterRange(keyboardAnchor ?? next, next)
                    characterRange = range
                    selected = Set(range.first.word...range.last.word)
                } else {
                    characterRange = nil
                    keyboardAnchor = nil
                    selected = [focused]
                }
                selectionRect = nil
            } else {
                super.keyDown(with: event)
            }
        }
    }
}
