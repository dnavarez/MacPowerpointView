namespace SlideViewer.Parsing;

/// <summary>
/// Maps symbol-font bullet characters (Wingdings, Symbol) to Unicode.
///
/// PowerPoint stores bullets as a raw character plus a symbol font, e.g.
/// Wingdings 'v'. Rendering that character in the symbol font produces glyphs
/// that differ from what PowerPoint shows (Wingdings 'v' becomes a cluster of
/// diamonds instead of the expected ❖). Translating to Unicode and rendering in
/// the normal text font matches the source presentation on any platform.
/// </summary>
public static class SymbolFont
{
    private static readonly Dictionary<char, string> Wingdings = new()
    {
        ['l'] = "●",   // ● black circle
        ['n'] = "■",   // ■ black square
        ['o'] = "□",   // □ white square
        ['p'] = "❑",   // ❑ shadowed square
        ['q'] = "❒",   // ❒ shadowed square
        ['u'] = "♦",   // ◆ black diamond
        ['v'] = "❖",   // ❖ black diamond minus white X
        ['w'] = "✦",   // ✦ black four-pointed star
        ['§'] = "▪",  // ▪ small black square
        ['¨'] = "▫",  // ▫ small white square
        ['Ø'] = "➢",  // ➢ arrowhead
        ['ü'] = "✔",  // ✔ check mark
        ['þ'] = "✗",  // ✗ ballot X
    };

    private static readonly Dictionary<char, string> Symbol = new()
    {
        ['·'] = "•",  // • bullet
        ['§'] = "♦",  // ◆ diamond
    };

    /// <summary>Unicode bullet for a symbol-font character, or null to keep the
    /// raw character in its original font.</summary>
    public static string? UnicodeBullet(string ch, string? font)
    {
        if (string.IsNullOrEmpty(ch) || font == null) return null;
        var f = font.ToLowerInvariant();
        var c = ch[0];
        if (f.StartsWith("wingdings")) return Wingdings.TryGetValue(c, out var w) ? w : null;
        if (f == "symbol") return Symbol.TryGetValue(c, out var s) ? s : null;
        return null;
    }
}
