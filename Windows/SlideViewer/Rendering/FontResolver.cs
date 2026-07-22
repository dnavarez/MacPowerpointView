using Avalonia.Media;

namespace SlideViewer.Rendering;

/// <summary>
/// Resolves OOXML font names to installed fonts.
///
/// On Windows the fonts decks reference (Calibri, Cambria, Arial, Bookman Old
/// Style…) are normally present, so this is usually a pass-through. The
/// substitution table matters when a deck names a font the machine lacks — and
/// when running this same code on macOS for verification, where Windows-only
/// fonts are missing. Substitutes preserve serif-vs-sans so body text doesn't
/// silently change character.
/// </summary>
public static class FontResolver
{
    private static readonly Dictionary<string, string> Substitutions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Serif → serif
        ["Cambria"] = "Georgia",
        ["Constantia"] = "Georgia",
        ["Bookman Old Style"] = "Georgia",
        ["Book Antiqua"] = "Palatino Linotype",
        ["Palatino Linotype"] = "Georgia",
        ["Garamond"] = "Times New Roman",
        ["Century"] = "Times New Roman",
        ["Century Schoolbook"] = "Times New Roman",
        // Sans → sans
        ["Calibri"] = "Segoe UI",
        ["Calibri Light"] = "Segoe UI",
        ["Corbel"] = "Segoe UI",
        ["Candara"] = "Segoe UI",
        ["Segoe UI"] = "Arial",
        ["Tahoma"] = "Arial",
        ["Century Gothic"] = "Futura",
        // Monospace
        ["Consolas"] = "Courier New",
    };

    private static readonly Dictionary<string, FontFamily?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string>? _installed;

    private static HashSet<string> Installed
    {
        get
        {
            if (_installed != null) return _installed;
            _installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in FontManager.Current.SystemFonts)
                    _installed.Add(f.Name);
            }
            catch { /* fall back to "everything resolves" */ }
            return _installed;
        }
    }

    /// <summary>Font family for a requested name, or the UI default when nothing
    /// suitable is installed.</summary>
    public static FontFamily Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return FontFamily.Default;
        if (Cache.TryGetValue(name, out var cached)) return cached ?? FontFamily.Default;

        FontFamily? result = null;
        if (IsInstalled(name)) result = new FontFamily(name);
        else if (Substitutions.TryGetValue(name, out var sub) && IsInstalled(sub))
            result = new FontFamily(sub);

        Cache[name] = result;
        return result ?? FontFamily.Default;
    }

    private static bool IsInstalled(string name) =>
        Installed.Count == 0 || Installed.Contains(name);
}
