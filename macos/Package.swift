// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "ScreenToSearchMac",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "ScreenToSearchMac", targets: ["ScreenToSearchMac"])],
    targets: [.executableTarget(name: "ScreenToSearchMac")]
)
