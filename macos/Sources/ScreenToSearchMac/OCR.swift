import AppKit
import Foundation

struct OCRWord: Decodable {
    let text: String
    let x: Double
    let y: Double
    let width: Double
    let height: Double
    let line: [Int]

    func rect(in size: CGSize, imageSize: CGSize) -> CGRect {
        CGRect(x: x * size.width / imageSize.width,
               y: size.height - (y + height) * size.height / imageSize.height,
               width: width * size.width / imageSize.width,
               height: height * size.height / imageSize.height)
    }
}

enum OCRBridge {
    static var resources: URL {
        if let path = ProcessInfo.processInfo.environment["SCREEN_TO_SEARCH_RESOURCE_DIR"] {
            return URL(fileURLWithPath: path, isDirectory: true)
        }
        return Bundle.main.resourceURL ?? URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
    }

    static func recognize(_ image: CGImage) throws -> [OCRWord] {
        let temp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".png")
        defer { try? FileManager.default.removeItem(at: temp) }
        let bitmap = NSBitmapImageRep(cgImage: image)
        guard let png = bitmap.representation(using: .png, properties: [:]) else {
            throw NSError(domain: "ScreenToSearch", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "Could not encode screenshot."])
        }
        try png.write(to: temp)
        let process = Process()
        let packaged = resources.appendingPathComponent("OcrHelper/OcrHelper")
        if FileManager.default.isExecutableFile(atPath: packaged.path) {
            process.executableURL = packaged
            process.arguments = [temp.path]
        } else {
            process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
            let python = ProcessInfo.processInfo.environment["SCREEN_TO_SEARCH_PYTHON"] ?? "python3"
            process.arguments = [python, resources.appendingPathComponent("ocr_helper.py").path, temp.path]
        }
        var environment = ProcessInfo.processInfo.environment
        environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:" + (environment["PATH"] ?? "/usr/bin:/bin")
        environment["SCREEN_TO_SEARCH_MODEL_DIR"] = resources.appendingPathComponent("models/v6").path
        process.environment = environment
        let output = Pipe()
        let errors = Pipe()
        process.standardOutput = output
        process.standardError = errors
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        let errorData = errors.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            let detail = String(data: errorData, encoding: .utf8) ?? "OCR failed."
            throw NSError(domain: "ScreenToSearch", code: Int(process.terminationStatus),
                          userInfo: [NSLocalizedDescriptionKey: detail])
        }
        return try JSONDecoder().decode([OCRWord].self, from: data)
    }
}
