import Foundation

/// Namespace-agnostic traversal helpers for `XMLElement`.
///
/// OOXML documents are heavily namespaced and different producers use different
/// prefixes, so every query matches on `local-name()` rather than a fixed prefix.
extension XMLElement {
    /// All descendants (any depth) with the given local name, in document order.
    func descendants(localName: String) -> [XMLElement] {
        let xpath = ".//*[local-name()='\(localName)']"
        return ((try? nodes(forXPath: xpath)) ?? []).compactMap { $0 as? XMLElement }
    }

    /// First descendant (any depth) with the given local name.
    func firstDescendant(localName: String) -> XMLElement? {
        descendants(localName: localName).first
    }

    /// First *direct child* with the given local name.
    func firstChild(localName: String) -> XMLElement? {
        for case let child as XMLElement in children ?? [] {
            if child.localName == localName { return child }
        }
        return nil
    }
}
