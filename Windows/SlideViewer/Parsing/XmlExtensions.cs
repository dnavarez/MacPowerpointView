using System.Xml.Linq;

namespace SlideViewer.Parsing;

/// <summary>
/// Namespace-agnostic traversal helpers. OOXML uses many namespace prefixes
/// (p:, a:, r:) that vary by producer, so everything matches on local name.
/// Traversal is explicit (never XPath) — the macOS build hit a Foundation XPath
/// bug where ".//" escaped the context node and corrupted color resolution.
/// </summary>
public static class XmlExtensions
{
    /// <summary>First direct child with the given local name.</summary>
    public static XElement? Child(this XElement? element, string localName) =>
        element?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    /// <summary>Direct children with the given local name, in document order.</summary>
    public static IEnumerable<XElement> Children(this XElement? element, string localName) =>
        element?.Elements().Where(e => e.Name.LocalName == localName) ?? Enumerable.Empty<XElement>();

    /// <summary>First descendant (any depth, document order) with the local name.</summary>
    public static XElement? Descendant(this XElement? element, string localName)
    {
        if (element == null) return null;
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == localName) return child;
            var found = child.Descendant(localName);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>All descendants with the given local name, in document order.</summary>
    public static IEnumerable<XElement> DescendantsNamed(this XElement? element, string localName)
    {
        if (element == null) yield break;
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == localName) yield return child;
            foreach (var nested in child.DescendantsNamed(localName)) yield return nested;
        }
    }

    /// <summary>Attribute by local name, preferring the unprefixed one so a plain
    /// `id` wins over `r:id`.</summary>
    public static string? Attr(this XElement? element, string localName)
    {
        if (element == null) return null;
        var direct = element.Attribute(localName);
        if (direct != null) return direct.Value;
        return element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == localName)?.Value;
    }

    /// <summary>Relationship attribute from the r: namespace (r:id, r:embed).</summary>
    public static string? RelAttr(this XElement? element, string localName) =>
        element?.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == localName &&
                                 a.Name.NamespaceName.Contains("relationships"))?.Value;

    public static double? AttrDouble(this XElement? element, string localName)
    {
        var s = element.Attr(localName);
        return double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static int? AttrInt(this XElement? element, string localName)
    {
        var s = element.Attr(localName);
        return int.TryParse(s, out var v) ? v : null;
    }
}
