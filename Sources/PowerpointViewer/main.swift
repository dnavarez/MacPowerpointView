import Foundation

// Headless diagnostic mode: `PowerpointViewer --dump <file.pptx>` parses a deck
// and prints a structural summary without launching the GUI. Useful for testing.
if let idx = CommandLine.arguments.firstIndex(of: "--dump"),
   CommandLine.arguments.indices.contains(idx + 1) {
    DebugDump.run(path: CommandLine.arguments[idx + 1])
} else {
    PowerpointViewerApp.main()
}
