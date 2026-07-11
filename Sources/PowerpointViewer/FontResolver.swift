import SwiftUI
import AppKit

/// Resolves OOXML font names to usable macOS fonts.
///
/// Decks authored on Windows routinely reference fonts that aren't installed on
/// macOS (Calibri, Cambria, Bookman Old Style…). SwiftUI's `Font.custom` silently
/// falls back to the sans-serif system font for any missing family — which turns
/// serif body text into sans and loses the document's character. We instead map
/// known-missing families to the closest macOS equivalent, preserving serif vs.
/// sans, so the substitution matches what PowerPoint/Keynote would pick.
enum FontResolver {
    /// Curated substitutions for common Windows fonts absent on macOS.
    private static let substitutions: [String: String] = [
        // Serif → serif
        "cambria": "Georgia",
        "constantia": "Georgia",
        "bookman old style": "Georgia",
        "book antiqua": "Palatino",
        "palatino linotype": "Palatino",
        "garamond": "Times New Roman",
        "century": "Times New Roman",
        "century schoolbook": "Times New Roman",
        // Sans → sans
        "calibri": "Helvetica Neue",
        "calibri light": "Helvetica Neue",
        "corbel": "Helvetica Neue",
        "candara": "Optima",
        "segoe ui": "Helvetica Neue",
        "tahoma": "Geneva",
        "century gothic": "Futura",
        // Monospace
        "consolas": "Menlo",
        "courier new": "Courier New"
    ]

    /// Cache of resolved family names keyed by the requested name.
    private static var cache: [String: String?] = [:]

    /// Returns a SwiftUI font for `name` at `size`, applying substitution and
    /// falling back to the system font when nothing suitable is installed.
    static func font(name: String?, size: CGFloat) -> Font {
        guard let name, !name.isEmpty else { return .system(size: size) }
        if let resolved = resolvedFamily(for: name) {
            return .custom(resolved, size: size)
        }
        return .system(size: size)
    }

    /// The installed family name to use for `name`, or nil to use the system font.
    static func resolvedFamily(for name: String) -> String? {
        if let cached = cache[name] { return cached }
        let result = compute(for: name)
        cache[name] = result
        return result
    }

    private static func compute(for name: String) -> String? {
        // 1. Use the exact font if macOS has it.
        if isInstalled(name) { return name }
        // 2. Curated substitution, if the target is installed.
        if let sub = substitutions[name.lowercased()], isInstalled(sub) { return sub }
        // 3. Give up — caller falls back to the system font.
        return nil
    }

    private static func isInstalled(_ name: String) -> Bool {
        NSFont(name: name, size: 12) != nil
    }
}
