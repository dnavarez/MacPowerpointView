// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "PowerpointViewer",
    platforms: [
        .macOS(.v14)
    ],
    targets: [
        .executableTarget(
            name: "PowerpointViewer",
            path: "Sources/PowerpointViewer"
        )
    ]
)
