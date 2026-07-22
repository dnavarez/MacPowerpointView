using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SlideViewer.Parsing;

/// <summary>
/// Reads parts out of a .pptx (a ZIP of XML). Unlike the macOS build — which
/// shelled out to `unzip` and had to repair hostile stored permissions — .NET
/// reads entries straight from the archive, so that class of bug can't occur.
/// </summary>
public sealed class PptxPackage : IDisposable
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;
    private readonly string _mediaDir;
    private readonly Dictionary<string, string> _extractedMedia = new();

    public PptxPackage(string path)
    {
        _archive = ZipFile.OpenRead(path);
        _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _archive.Entries)
            _entries[e.FullName.Replace('\\', '/')] = e;

        _mediaDir = Path.Combine(Path.GetTempPath(), "SlideViewer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_mediaDir);
    }

    public bool Has(string partPath) => _entries.ContainsKey(partPath);

    public Stream? Open(string partPath) =>
        _entries.TryGetValue(partPath, out var e) ? e.Open() : null;

    /// <summary>Sentinel standing in for whitespace-only run text, mirroring the
    /// macOS build. .NET preserves whitespace nodes with PreserveWhitespace, but
    /// the guard is kept so both implementations behave identically.</summary>
    public const string SpaceSentinel = "";

    private static readonly Regex WhitespaceRun =
        new(@"(<[A-Za-z0-9]*:?t(?:\s[^>]*)?>)(\s+)(</[A-Za-z0-9]*:?t>)", RegexOptions.Compiled);

    /// <summary>Loads a part as XML, protecting whitespace-only &lt;a:t&gt; runs.</summary>
    public XDocument? LoadXml(string partPath, bool protectWhitespace = false)
    {
        using var stream = Open(partPath);
        if (stream == null) return null;
        try
        {
            if (!protectWhitespace)
                return XDocument.Load(stream, LoadOptions.PreserveWhitespace);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            text = WhitespaceRun.Replace(text, m =>
                m.Groups[1].Value +
                string.Concat(Enumerable.Repeat(SpaceSentinel, m.Groups[2].Value.Length)) +
                m.Groups[3].Value);
            return XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        }
        catch { return null; }
    }

    /// <summary>Extracts a media part to a temp file (once) and returns its path.</summary>
    public string? ExtractMedia(string partPath)
    {
        if (_extractedMedia.TryGetValue(partPath, out var cached)) return cached;
        if (!_entries.TryGetValue(partPath, out var entry)) return null;

        var dest = Path.Combine(_mediaDir, Path.GetFileName(partPath));
        // Distinct parts can share a file name; disambiguate.
        if (File.Exists(dest))
            dest = Path.Combine(_mediaDir, Guid.NewGuid().ToString("N") + Path.GetExtension(partPath));
        try
        {
            using var src = entry.Open();
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
        catch { return null; }

        _extractedMedia[partPath] = dest;
        return dest;
    }

    /// <summary>Parses a part's .rels file → relationship id → target path.</summary>
    public Dictionary<string, string> Relationships(string partPath)
    {
        var dir = PathUtil.Directory(partPath);
        var file = PathUtil.FileName(partPath);
        var relsPath = string.IsNullOrEmpty(dir) ? $"_rels/{file}.rels" : $"{dir}/_rels/{file}.rels";
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var doc = LoadXml(relsPath);
        if (doc?.Root == null) return map;
        foreach (var rel in doc.Root.Elements())
        {
            var id = rel.Attribute("Id")?.Value;
            var target = rel.Attribute("Target")?.Value;
            if (id != null && target != null) map[id] = target;
        }
        return map;
    }

    public void Dispose()
    {
        _archive.Dispose();
        try { if (Directory.Exists(_mediaDir)) Directory.Delete(_mediaDir, true); } catch { }
    }
}

public static class PathUtil
{
    public static string Directory(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    public static string FileName(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>Resolves a possibly-relative relationship target against a base dir.</summary>
    public static string Resolve(string baseDir, string target)
    {
        if (target.StartsWith('/')) return target[1..];
        var parts = new List<string>(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in target.Split('/'))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg is "." or "") { }
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }
}
