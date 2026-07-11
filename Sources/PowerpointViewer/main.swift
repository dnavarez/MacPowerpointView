import Foundation

// Headless diagnostic mode: `PowerpointViewer --dump <file.pptx>` parses a deck
// and prints a structural summary without launching the GUI. Useful for testing.
let args = CommandLine.arguments
if let idx = args.firstIndex(of: "--dump"), args.indices.contains(idx + 1) {
    DebugDump.run(path: args[idx + 1])
} else if let idx = args.firstIndex(of: "--render"), args.indices.contains(idx + 3) {
    MainActor.assumeIsolated {
        RenderTool.run(path: args[idx + 1], index: Int(args[idx + 2]) ?? 0, outPath: args[idx + 3])
    }
} else {
    PowerpointViewerApp.main()
}
