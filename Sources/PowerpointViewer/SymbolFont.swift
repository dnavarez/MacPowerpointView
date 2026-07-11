import Foundation

/// Maps symbol-font bullet characters (Wingdings, Symbol) to Unicode.
///
/// PowerPoint stores list bullets as a raw character plus a symbol font, e.g.
/// Wingdings `v`. Rendering that character in the symbol font on macOS produces
/// glyphs that differ from what PowerPoint/Keynote show (Wingdings `v` becomes a
/// cluster of diamonds rather than the expected ❖). We translate the common
/// bullet characters to their Unicode equivalents and render them in the normal
/// text font, so bullets match the source presentation.
enum SymbolFont {
    /// Wingdings character → Unicode bullet glyph (common bullet subset).
    private static let wingdings: [Character: String] = [
        "l": "\u{25CF}",   // ● black circle
        "n": "\u{25A0}",   // ■ black square
        "o": "\u{25A1}",   // □ white square
        "p": "\u{2751}",   // ❑ lower-right shadowed white square
        "q": "\u{2752}",   // ❒ upper-right shadowed white square
        "u": "\u{2666}",   // ◆ black diamond
        "v": "\u{2756}",   // ❖ black diamond minus white X
        "w": "\u{2726}",   // ✦ black four-pointed star
        "\u{00A7}": "\u{25AA}", // ▪ small black square
        "\u{00A8}": "\u{25AB}", // ▫ small white square
        "\u{00D8}": "\u{27A2}", // ➢ three-D top-lit arrowhead
        "\u{00FC}": "\u{2714}", // ✔ check mark
        "\u{00FE}": "\u{2717}"  // ✗ ballot X
    ]

    /// Symbol-font (math) character → Unicode.
    private static let symbol: [Character: String] = [
        "\u{00B7}": "\u{2022}", // · → • bullet
        "\u{00A7}": "\u{2666}"  // diamond
    ]

    /// Returns a Unicode bullet for a symbol-font character, or nil to keep the
    /// raw character in its original font.
    static func unicodeBullet(char: String, font: String?) -> String? {
        guard let first = char.first else { return nil }
        switch font?.lowercased() {
        case .some(let f) where f.hasPrefix("wingdings"):
            return wingdings[first]
        case "symbol":
            return symbol[first]
        default:
            return nil
        }
    }
}
