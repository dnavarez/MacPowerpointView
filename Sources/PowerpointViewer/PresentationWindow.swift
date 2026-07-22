import AppKit
import SwiftUI

/// Manages the dedicated full-screen presentation window.
///
/// The window is borderless, black, and sized to cover an entire display.
/// When more than one display is attached, the presentation goes to a display
/// *other than* the one hosting the main window (the presenter keeps the
/// console in front of them, the audience sees the slides). With a single
/// display it covers that one.
///
/// Keyboard control works app-wide while presenting via a local event
/// monitor: →/↓/space/return advance, ←/↑ go back, esc ends the show.
@MainActor
final class PresentationWindowController {
    private var window: NSWindow?
    private var keyMonitor: Any?
    private weak var store: PresentationStore?

    /// Called when the user ends the presentation (esc or window closed).
    var onEnd: (() -> Void)?

    var isShowing: Bool { window != nil }

    func show(store: PresentationStore) {
        guard window == nil else { return }
        self.store = store

        // Prefer a screen that is NOT the one the main window sits on.
        let consoleScreen = NSApp.keyWindow?.screen ?? NSApp.mainWindow?.screen ?? NSScreen.main
        let screen = NSScreen.screens.first(where: { $0 != consoleScreen })
            ?? consoleScreen
            ?? NSScreen.screens.first
        guard let targetScreen = screen else { return }

        let win = NSWindow(contentRect: targetScreen.frame,
                           styleMask: [.borderless],
                           backing: .buffered,
                           defer: false)
        win.setFrame(targetScreen.frame, display: true)
        win.isReleasedWhenClosed = false
        win.backgroundColor = .black
        win.level = .screenSaver                       // above menu bar & dock
        win.collectionBehavior = [.fullScreenAuxiliary, .canJoinAllSpaces]
        win.contentView = NSHostingView(rootView: PresentationScreenView(store: store))
        win.makeKeyAndOrderFront(nil)
        NSCursor.setHiddenUntilMouseMoves(true)
        window = win

        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, let store = self.store else { return event }
            switch event.keyCode {
            case 53:                  // esc
                self.onEnd?()
                return nil
            case 123, 126:            // ← ↑
                store.goPrevious()
                return nil
            case 124, 125, 49, 36:    // → ↓ space return
                store.goNext()
                return nil
            default:
                return event
            }
        }
    }

    /// While the end-presentation confirmation is up, drop the show window below
    /// the console and bring the app forward — at `.screenSaver` level it would
    /// otherwise cover the dialog on a single-display setup, and the app would
    /// look frozen.
    func setConfirming(_ confirming: Bool) {
        guard let window else { return }
        if confirming {
            window.level = .normal
            NSApp.activate(ignoringOtherApps: true)
        } else {
            window.level = .screenSaver
            window.makeKeyAndOrderFront(nil)
        }
    }

    func close() {
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
        window?.orderOut(nil)
        window?.close()
        window = nil
        store = nil
    }
}

/// The audience-facing view: the current slide on black, click to advance.
struct PresentationScreenView: View {
    @ObservedObject var store: PresentationStore

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if let slide = store.currentSlide, let pres = store.presentation {
                SlideView(slide: slide, slideSize: pres.size, showShadow: false,
                          hiddenShapeIDs: store.hiddenShapeIDs,
                          hiddenParagraphs: store.hiddenParagraphs)
                    .padding(1)
                    .id(slide.id)
                    .transition(slide.hasTransition ? .opacity : .identity)
            }
        }
        // Animate build reveals within a slide, and crossfade between slides
        // that define a transition.
        .animation(.easeInOut(duration: 0.35), value: store.buildIndex)
        .animation(.easeInOut(duration: 0.5), value: store.currentIndex)
        .contentShape(Rectangle())
        .onTapGesture { store.goNext() }
    }
}
