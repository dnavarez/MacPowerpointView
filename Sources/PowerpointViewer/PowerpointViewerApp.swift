import SwiftUI
import UniformTypeIdentifiers
import AppKit

struct PowerpointViewerApp: App {
    @StateObject private var store = PresentationStore()

    var body: some Scene {
        Window("PowerPoint Viewer", id: "main") {
            ContentView(store: store)
        }
        .commands {
            // File menu: Open…
            CommandGroup(replacing: .newItem) {
                Button("Open…") { openPanel() }
                    .keyboardShortcut("o", modifiers: .command)
            }

            // Navigation menu.
            CommandMenu("Slide") {
                Button("Next Slide") { store.goNext() }
                    .keyboardShortcut(.rightArrow, modifiers: [])
                    .disabled(store.presentation == nil)
                Button("Previous Slide") { store.goPrevious() }
                    .keyboardShortcut(.leftArrow, modifiers: [])
                    .disabled(store.presentation == nil)
                Divider()
                Button(store.isPresenting ? "Exit Presentation" : "Start Presentation") {
                    store.isPresenting.toggle()
                }
                .keyboardShortcut("p", modifiers: [.command, .shift])
                .disabled(store.presentation == nil)
            }
        }
    }

    private func openPanel() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        if let pptx = UTType(filenameExtension: "pptx") {
            panel.allowedContentTypes = [pptx]
        }
        if panel.runModal() == .OK, let url = panel.url {
            store.open(url: url)
        }
    }
}
