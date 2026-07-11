import Foundation

/// Namespace-agnostic traversal helpers for `XMLElement`.
///
/// OOXML documents are heavily namespaced and different producers use different
/// prefixes, so every query matches on the element's *local* name.
///
/// Implementation note: these walk the tree manually rather than using
/// `nodes(forXPath:)`. Foundation's XPath evaluation has been observed escaping
/// the context node on some documents (a `.//*` query returning matches from the
/// whole document), which silently corrupted color resolution — a background's
/// `solidFill` would pick up an unrelated `srgbClr` from elsewhere in the slide.
extension XMLElement {
    /// All descendants (any depth) with the given local name, in document order.
    func descendants(localName: String) -> [XMLElement] {
        var result: [XMLElement] = []
        func walk(_ element: XMLElement) {
            for case let child as XMLElement in element.children ?? [] {
                if child.localName == localName { result.append(child) }
                walk(child)
            }
        }
        walk(self)
        return result
    }

    /// First descendant (any depth, document order) with the given local name.
    func firstDescendant(localName: String) -> XMLElement? {
        for case let child as XMLElement in children ?? [] {
            if child.localName == localName { return child }
            if let found = child.firstDescendant(localName: localName) { return found }
        }
        return nil
    }

    /// First *direct child* with the given local name.
    func firstChild(localName: String) -> XMLElement? {
        for case let child as XMLElement in children ?? [] {
            if child.localName == localName { return child }
        }
        return nil
    }
}
