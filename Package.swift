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
        ),
        .testTarget(
            name: "PowerpointViewerTests",
            dependencies: ["PowerpointViewer"],
            path: "Tests/PowerpointViewerTests",
            resources: [.copy("Fixtures")]
        )
    ]
)
