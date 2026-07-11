import SwiftUI

extension Color {
    /// Creates a color from a 6-digit hex string (e.g. `"4472C4"`), with an
    /// optional leading `#`. Falls back to gray on malformed input.
    init(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.hasPrefix("#") { s.removeFirst() }
        guard s.count == 6, let value = UInt32(s, radix: 16) else {
            self = .gray
            return
        }
        let r = Double((value >> 16) & 0xFF) / 255.0
        let g = Double((value >> 8) & 0xFF) / 255.0
        let b = Double(value & 0xFF) / 255.0
        self = Color(.sRGB, red: r, green: g, blue: b, opacity: 1.0)
    }

    /// Returns a copy of this color with the given opacity (used for OOXML alpha mods).
    func withAlpha(_ alpha: Double) -> Color {
        alpha >= 1.0 ? self : opacity(alpha)
    }
}
