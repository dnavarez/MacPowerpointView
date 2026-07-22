import SwiftUI
import AppKit

/// Sizes the main window at launch to a fraction of the screen's height,
/// centred horizontally and anchored just below the menu bar — so the desktop
/// and the window's own title bar stay visible instead of the window filling
/// the display.
///
/// SwiftUI's `Window` scene has no API for a proportional default size, so this
/// reaches the `NSWindow` through a zero-size backing view. It applies once per
/// launch; resizing afterwards is left alone.
struct WindowSizer: NSViewRepresentable {
    /// Fraction of the visible screen height the window occupies at launch.
    var heightFraction: CGFloat = 0.75
    /// Window aspect used to derive width from height (16:10 suits a 16:9 slide
    /// plus the thumbnail sidebar and controls).
    var widthToHeight: CGFloat = 1.6
    /// Never shrink below the layout's comfortable minimum.
    var minimumWidth: CGFloat = 900

    final class Coordinator {
        var hasSized = false
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        let coordinator = context.coordinator
        // The window isn't attached yet while the view is being made.
        DispatchQueue.main.async { [weak view] in
            guard let window = view?.window, !coordinator.hasSized else { return }
            coordinator.hasSized = true
            resize(window)
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}

    private func resize(_ window: NSWindow) {
        guard let screen = window.screen ?? NSScreen.main else { return }

        // visibleFrame excludes the menu bar and the Dock.
        let available = screen.visibleFrame
        let height = (available.height * heightFraction).rounded()
        let width = min(max(height * widthToHeight, minimumWidth), available.width)

        window.setFrame(
            CGRect(x: (available.midX - width / 2).rounded(),
                   y: available.maxY - height,     // top-anchored, under the menu bar
                   width: width,
                   height: height),
            display: true)
    }
}
