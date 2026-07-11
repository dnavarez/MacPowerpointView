import Foundation
import SwiftUI

/// Parses a `.pptx` package into a `Presentation` model.
///
/// The parser is deliberately namespace-agnostic: OOXML uses many namespace
/// prefixes (`p:`, `a:`, `r:` …) that vary between producers, so every lookup is
/// done by *local name* via XPath `local-name()` predicates and attribute scans.
/// It handles the common shape vocabulary — text boxes, pictures, and autoshapes
/// with solid fills — which covers the vast majority of real-world decks.
final class PPTXParser {
    enum ParseError: Error, LocalizedError {
        case missing(String)
        var errorDescription: String? {
            switch self {
            case .missing(let what): return "The presentation is missing \(what)."
            }
        }
    }

    private let root: URL          // extracted archive root
    private var themeColors: [String: Color] = [:]

    /// Placeholder geometry inherited from a slide layout or master, keyed both
    /// by placeholder `idx` and by placeholder `type`.
    private struct PlaceholderGeom {
        var byIdx: [String: CGRect] = [:]
        var byType: [String: CGRect] = [:]
    }

    /// Default run sizes (points) defined by a master's text styles.
    private struct MasterStyles {
        var titleSize: Double?
        var bodySizes: [Int: Double] = [:]
    }

    /// Default run formatting for one outline level, typically read from a
    /// text body's `lstStyle` (`a:lvlNpPr/a:defRPr`). Any nil field falls back
    /// to the enclosing default.
    private struct RunDefaults {
        var size: Double?
        var bold: Bool?
        var italic: Bool?
        var color: Color?
        var fontName: String?
        var shadow: Bool = false
    }

    /// Resolution context for one slide: placeholder geometry from its layout and
    /// master, plus the master's default text sizes.
    private struct SlideContext {
        var layout = PlaceholderGeom()
        var master = PlaceholderGeom()
        var styles = MasterStyles()
        var layoutPath: String?
        var masterPath: String?
    }

    private var layoutContextCache: [String: (PlaceholderGeom, String)] = [:]  // layoutPath → (geom, masterPath)
    private var masterCache: [String: (PlaceholderGeom, MasterStyles)] = [:]
    /// Non-placeholder shapes/pictures a layout or master contributes to every slide.
    private var inheritedElementsCache: [String: [SlideElement]] = [:]

    init(extractedRoot: URL) {
        self.root = extractedRoot
    }

    // MARK: - Entry point

    func parse() throws -> Presentation {
        let presURL = root.appendingPathComponent("ppt/presentation.xml")
        guard let presDoc = try? XMLDocument(contentsOf: presURL, options: [.nodePreserveWhitespace]) else {
            throw ParseError.missing("presentation.xml")
        }
        let presRoot = presDoc.rootElement()

        // Slide canvas size.
        var size = CGSize(width: 960, height: 540) // 10" x 5.63" default
        if let sldSz = presRoot?.firstDescendant(localName: "sldSz") {
            let cx = Double(attr(sldSz, "cx") ?? "") ?? 0
            let cy = Double(attr(sldSz, "cy") ?? "") ?? 0
            if cx > 0 && cy > 0 {
                size = CGSize(width: Emu.toPoints(cx), height: Emu.toPoints(cy))
            }
        }

        loadTheme()

        // Ordered slide relationship ids (the r:id, not the numeric slide id).
        let relIds: [String] = (presRoot?.descendants(localName: "sldId") ?? [])
            .compactMap { relAttr($0, "id") }

        // presentation.xml relationships → rId → slide part path.
        let presRels = loadRelationships(forPart: "ppt/presentation.xml")

        var slides: [Slide] = []
        for (i, rId) in relIds.enumerated() {
            guard let target = presRels[rId] else { continue }
            let slidePath = resolvePath(base: "ppt", target: target)
            if let slide = try? parseSlide(atPartPath: slidePath, index: i) {
                slides.append(slide)
            }
        }

        return Presentation(size: size, slides: slides)
    }

    // MARK: - Theme

    private func loadTheme() {
        // Default Office palette as a fallback.
        themeColors = [
            "dk1": .black, "lt1": .white,
            "dk2": Color(hex: "44546A"), "lt2": Color(hex: "E7E6E6"),
            "tx1": .black, "bg1": .white,
            "tx2": Color(hex: "44546A"), "bg2": Color(hex: "E7E6E6"),
            "accent1": Color(hex: "4472C4"), "accent2": Color(hex: "ED7D31"),
            "accent3": Color(hex: "A5A5A5"), "accent4": Color(hex: "FFC000"),
            "accent5": Color(hex: "5B9BD5"), "accent6": Color(hex: "70AD47"),
            "hlink": Color(hex: "0563C1"), "folHlink": Color(hex: "954F72")
        ]

        let themeURL = root.appendingPathComponent("ppt/theme/theme1.xml")
        guard let doc = try? XMLDocument(contentsOf: themeURL),
              let scheme = doc.rootElement()?.firstDescendant(localName: "clrScheme") else { return }

        for child in scheme.children?.compactMap({ $0 as? XMLElement }) ?? [] {
            let name = child.localName ?? ""
            if let clr = colorFromContainer(child) {
                themeColors[name] = clr
            }
        }
    }

    // MARK: - Slide parsing

    /// Sentinel standing in for whitespace-only run text. libxml2 discards
    /// whitespace-only text nodes even with `.nodePreserveWhitespace`, which
    /// silently deletes the spaces between words when a producer emits them as
    /// separate `<a:t> </a:t>` runs. We swap such content for a private-use
    /// character before parsing and restore it to a space on extraction.
    private static let spaceSentinel = "\u{E000}"

    /// Loads a slide part, protecting whitespace-only `<a:t>` content first.
    private func loadSlideDocument(_ url: URL) throws -> XMLDocument {
        let data = try Data(contentsOf: url)
        guard var text = String(data: data, encoding: .utf8) else {
            return try XMLDocument(data: data, options: [.nodePreserveWhitespace])
        }
        let pattern = #/(<[A-Za-z0-9]*:?t(?:\s[^>]*)?>)(\s+)(</[A-Za-z0-9]*:?t>)/#
        text = text.replacing(pattern) { m in
            m.1 + String(repeating: Self.spaceSentinel, count: m.2.count) + m.3
        }
        return try XMLDocument(xmlString: text, options: [.nodePreserveWhitespace])
    }

    private func parseSlide(atPartPath partPath: String, index: Int) throws -> Slide {
        let url = root.appendingPathComponent(partPath)
        let doc = try loadSlideDocument(url)
        guard let sldRoot = doc.rootElement() else {
            throw ParseError.missing("slide \(index + 1)")
        }

        let rels = loadRelationships(forPart: partPath)
        let partDir = (partPath as NSString).deletingLastPathComponent
        let context = slideContext(slideRels: rels, slideDir: partDir)

        // Background resolves slide → layout → master (matching PowerPoint).
        let background = resolveBackground(slidePart: partPath, slideRoot: sldRoot, context: context)

        var elements: [SlideElement] = []

        // Master and layout contribute their non-placeholder shapes to every
        // slide (e.g. logos, watermarks, copyright strips), drawn beneath the
        // slide's own content — unless the slide opts out via showMasterSp="0".
        if attr(sldRoot, "showMasterSp") != "0" {
            if let masterPath = context.masterPath {
                elements.append(contentsOf: inheritedElements(fromPart: masterPath))
            }
            if let layoutPath = context.layoutPath {
                elements.append(contentsOf: inheritedElements(fromPart: layoutPath))
            }
        }
        if let spTree = sldRoot.firstDescendant(localName: "spTree") {
            for node in spTree.children?.compactMap({ $0 as? XMLElement }) ?? [] {
                switch node.localName {
                case "sp":
                    if let el = parseShape(node, context: context) { elements.append(el) }
                case "pic":
                    if let el = parsePicture(node, rels: rels, partDir: partDir) {
                        elements.append(.image(el))
                    }
                default:
                    break
                }
            }
        }

        return Slide(index: index, background: background, elements: elements)
    }

    private func parseShape(_ sp: XMLElement, context: SlideContext) -> SlideElement? {
        let spPr = sp.firstDescendant(localName: "spPr")
        let ph = placeholderInfo(sp)
        // Own geometry first, then inherit from layout, then master.
        let frame = xfrmFrame(in: spPr) ?? inheritedFrame(for: ph, context: context) ?? .zero
        let defaultSize = defaultFontSize(for: ph, context: context)

        // Text body (may be present on plain text boxes and on autoshapes).
        var textBox: TextBox?
        if let txBody = sp.firstDescendant(localName: "txBody") {
            let levelDefaults = listStyleDefaults(txBody.firstChild(localName: "lstStyle"))
            let paragraphs = parseParagraphs(txBody, defaultSize: defaultSize, levelDefaults: levelDefaults)
            if !paragraphs.isEmpty && paragraphs.contains(where: { !$0.runs.isEmpty }) {
                textBox = TextBox(frame: frame, paragraphs: paragraphs, fill: nil)
            }
        }

        let fillC = spPr.flatMap { fillColor(in: $0) }
        let geom = geometry(in: spPr)
        let stroke = strokeStyle(in: spPr)

        // A shape with a preset geometry + fill/stroke is a drawn shape;
        // a placeholder/textbox with only text is a text element.
        let hasVisibleShape = (fillC != nil) || (stroke != nil)

        if hasVisibleShape {
            var tb = textBox
            tb?.frame = frame
            let shape = ShapeElement(
                frame: frame,
                fill: fillC.map { FillStyle(color: $0) },
                stroke: stroke,
                geometry: geom,
                text: tb
            )
            return .shape(shape)
        } else if var tb = textBox {
            tb.frame = frame
            return .text(tb)
        }
        return nil
    }

    private func parsePicture(_ pic: XMLElement, rels: [String: String], partDir: String) -> PictureElement? {
        let spPr = pic.firstDescendant(localName: "spPr")
        guard let frame = xfrmFrame(in: spPr) else { return nil }
        guard let blip = pic.firstDescendant(localName: "blip"),
              let embed = relAttr(blip, "embed"),
              let target = rels[embed] else { return nil }
        let mediaPath = resolvePath(base: partDir, target: target)
        let url = root.appendingPathComponent(mediaPath)
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        return PictureElement(frame: frame, imageURL: url)
    }

    // MARK: - Text

    /// Reads one `a:defRPr` into run defaults.
    private func runDefaults(from defRPr: XMLElement) -> RunDefaults {
        var d = RunDefaults()
        if let sz = Double(attr(defRPr, "sz") ?? "") { d.size = sz / 100.0 }
        if let b = attr(defRPr, "b") { d.bold = (b == "1") }
        if let i = attr(defRPr, "i") { d.italic = (i == "1") }
        if let c = fillColor(in: defRPr) { d.color = c }
        if let latin = defRPr.firstChild(localName: "latin"), let tf = attr(latin, "typeface") {
            d.fontName = tf
        }
        if defRPr.firstDescendant(localName: "outerShdw") != nil { d.shadow = true }
        return d
    }

    /// Reads a text body's `lstStyle` into per-level run defaults (1-based level).
    private func listStyleDefaults(_ lstStyle: XMLElement?) -> [Int: RunDefaults] {
        guard let lstStyle else { return [:] }
        var result: [Int: RunDefaults] = [:]
        for lvl in 1...9 {
            guard let pPr = lstStyle.firstChild(localName: "lvl\(lvl)pPr"),
                  let defRPr = pPr.firstChild(localName: "defRPr") else { continue }
            result[lvl] = runDefaults(from: defRPr)
        }
        return result
    }

    private func parseParagraphs(_ txBody: XMLElement, defaultSize: Double,
                                 levelDefaults: [Int: RunDefaults]) -> [Paragraph] {
        var result: [Paragraph] = []
        for p in txBody.children?.compactMap({ $0 as? XMLElement }).filter({ $0.localName == "p" }) ?? [] {
            var para = Paragraph(runs: [])
            let pPr = p.firstChild(localName: "pPr")
            if let pPr {
                switch attr(pPr, "algn") {
                case "ctr": para.alignment = .center
                case "r": para.alignment = .trailing
                default: para.alignment = .leading
                }
                para.level = Int(attr(pPr, "lvl") ?? "0") ?? 0
                if pPr.firstChild(localName: "buChar") != nil || pPr.firstChild(localName: "buAutoNum") != nil {
                    para.hasBullet = true
                }
            }

            // lstStyle levels are 1-based; paragraph lvl is 0-based. A defRPr
            // directly inside this paragraph's pPr overrides the list style.
            var defaults = levelDefaults[para.level + 1] ?? RunDefaults()
            if let pPr, let defRPr = pPr.firstChild(localName: "defRPr") {
                let paraDefaults = runDefaults(from: defRPr)
                if let v = paraDefaults.size { defaults.size = v }
                if let v = paraDefaults.bold { defaults.bold = v }
                if let v = paraDefaults.italic { defaults.italic = v }
                if let v = paraDefaults.color { defaults.color = v }
                if let v = paraDefaults.fontName { defaults.fontName = v }
                if paraDefaults.shadow { defaults.shadow = true }
            }
            if defaults.size == nil { defaults.size = defaultSize }

            for child in p.children?.compactMap({ $0 as? XMLElement }) ?? [] {
                switch child.localName {
                case "r":
                    if let run = parseRun(child, defaults: defaults) { para.runs.append(run) }
                case "br":
                    para.runs.append(TextRun(text: "\n"))
                case "fld":
                    if let t = child.firstChild(localName: "t")?.stringValue, !t.isEmpty {
                        para.runs.append(parseRunProps(child.firstChild(localName: "rPr"), text: t, defaults: defaults))
                    }
                default:
                    break
                }
            }
            result.append(para)
        }
        return result
    }

    private func parseRun(_ r: XMLElement, defaults: RunDefaults) -> TextRun? {
        guard let text = r.firstChild(localName: "t")?.stringValue else { return nil }
        return parseRunProps(r.firstChild(localName: "rPr"), text: text, defaults: defaults)
    }

    private func parseRunProps(_ rPr: XMLElement?, text: String, defaults: RunDefaults) -> TextRun {
        var run = TextRun(text: text.replacingOccurrences(of: Self.spaceSentinel, with: " "))
        run.fontSize = defaults.size ?? 18
        run.bold = defaults.bold ?? false
        run.italic = defaults.italic ?? false
        if let c = defaults.color { run.color = c }
        run.fontName = defaults.fontName
        run.shadow = defaults.shadow
        guard let rPr else { return run }
        if let szStr = attr(rPr, "sz"), let sz = Double(szStr) {
            run.fontSize = sz / 100.0
        }
        if let b = attr(rPr, "b") { run.bold = (b == "1") }
        if let i = attr(rPr, "i") { run.italic = (i == "1") }
        if let u = attr(rPr, "u"), u != "none" { run.underline = true }
        if let c = fillColor(in: rPr) { run.color = c }
        if let latin = rPr.firstChild(localName: "latin"), let tf = attr(latin, "typeface") {
            run.fontName = tf
        }
        return run
    }

    // MARK: - Placeholder inheritance

    private struct PHInfo {
        var type: String?   // normalized (ctrTitle → title)
        var idx: String?
    }

    private func placeholderInfo(_ sp: XMLElement) -> PHInfo {
        guard let ph = sp.firstDescendant(localName: "ph") else { return PHInfo() }
        var type = attr(ph, "type")
        if type == "ctrTitle" { type = "title" }
        return PHInfo(type: type, idx: attr(ph, "idx"))
    }

    /// Resolves a placeholder's frame from the layout, then the master.
    private func inheritedFrame(for ph: PHInfo, context: SlideContext) -> CGRect? {
        for geom in [context.layout, context.master] {
            if let idx = ph.idx, let f = geom.byIdx[idx] { return f }
            if let type = ph.type, let f = geom.byType[type] { return f }
        }
        return nil
    }

    private func defaultFontSize(for ph: PHInfo, context: SlideContext) -> Double {
        if ph.type == "title" { return context.styles.titleSize ?? 44 }
        // Body / subtitle / other placeholders fall back to level-1 body size.
        return context.styles.bodySizes[1] ?? 18
    }

    /// Builds (and caches) the layout + master resolution context for a slide.
    private func slideContext(slideRels: [String: String], slideDir: String) -> SlideContext {
        var ctx = SlideContext()
        guard let layoutTarget = relationshipTarget(in: slideRels, endingWith: "slideLayout") else {
            return ctx
        }
        let layoutPath = resolvePath(base: slideDir, target: layoutTarget)
        ctx.layoutPath = layoutPath

        let (layoutGeom, masterPath): (PlaceholderGeom, String)
        if let cached = layoutContextCache[layoutPath] {
            (layoutGeom, masterPath) = cached
        } else {
            let geom = placeholderGeom(fromPart: layoutPath)
            let layoutDir = (layoutPath as NSString).deletingLastPathComponent
            let layoutRels = loadRelationships(forPart: layoutPath)
            let mPath = relationshipTarget(in: layoutRels, endingWith: "slideMaster")
                .map { resolvePath(base: layoutDir, target: $0) } ?? ""
            layoutContextCache[layoutPath] = (geom, mPath)
            (layoutGeom, masterPath) = (geom, mPath)
        }
        ctx.layout = layoutGeom
        ctx.masterPath = masterPath.isEmpty ? nil : masterPath

        if !masterPath.isEmpty {
            let (masterGeom, styles): (PlaceholderGeom, MasterStyles)
            if let cached = masterCache[masterPath] {
                (masterGeom, styles) = cached
            } else {
                let geom = placeholderGeom(fromPart: masterPath)
                let st = masterStyles(fromPart: masterPath)
                masterCache[masterPath] = (geom, st)
                (masterGeom, styles) = (geom, st)
            }
            ctx.master = masterGeom
            ctx.styles = styles
        }
        return ctx
    }

    /// Extracts placeholder geometry from a layout or master part.
    private func placeholderGeom(fromPart partPath: String) -> PlaceholderGeom {
        var geom = PlaceholderGeom()
        let url = root.appendingPathComponent(partPath)
        guard let doc = try? XMLDocument(contentsOf: url, options: [.nodePreserveWhitespace]),
              let spTree = doc.rootElement()?.firstDescendant(localName: "spTree") else { return geom }
        for sp in spTree.children?.compactMap({ $0 as? XMLElement }).filter({ $0.localName == "sp" }) ?? [] {
            guard let frame = xfrmFrame(in: sp.firstDescendant(localName: "spPr")) else { continue }
            let ph = placeholderInfo(sp)
            if let idx = ph.idx { geom.byIdx[idx] = frame }
            if let type = ph.type { geom.byType[type] = frame }
        }
        return geom
    }

    /// Reads default title/body run sizes from a master's `p:txStyles`.
    private func masterStyles(fromPart partPath: String) -> MasterStyles {
        var styles = MasterStyles()
        let url = root.appendingPathComponent(partPath)
        guard let doc = try? XMLDocument(contentsOf: url),
              let txStyles = doc.rootElement()?.firstDescendant(localName: "txStyles") else { return styles }

        if let title = txStyles.firstDescendant(localName: "titleStyle"),
           let lvl1 = title.firstChild(localName: "lvl1pPr"),
           let defRPr = lvl1.firstChild(localName: "defRPr"),
           let sz = Double(attr(defRPr, "sz") ?? "") {
            styles.titleSize = sz / 100.0
        }
        if let body = txStyles.firstDescendant(localName: "bodyStyle") {
            for lvl in 1...9 {
                if let pPr = body.firstChild(localName: "lvl\(lvl)pPr"),
                   let defRPr = pPr.firstChild(localName: "defRPr"),
                   let sz = Double(attr(defRPr, "sz") ?? "") {
                    styles.bodySizes[lvl] = sz / 100.0
                }
            }
        }
        return styles
    }

    private func relationshipTarget(in rels: [String: String], endingWith suffix: String) -> String? {
        // rels maps rId → target; we need the target whose *type* ends with suffix.
        // loadRelationships loses the type, so re-scan is unnecessary: layout/master
        // targets are recognizable by path. Match on the target path instead.
        for target in rels.values where target.contains(suffix) {
            return target
        }
        return nil
    }

    // MARK: - Inherited master/layout shapes

    /// Parses the non-placeholder shapes and pictures of a layout or master part.
    /// Placeholders are excluded — they are geometry templates (with prompt text
    /// like "Title Text"), not content.
    private func inheritedElements(fromPart partPath: String) -> [SlideElement] {
        if let cached = inheritedElementsCache[partPath] { return cached }

        var elements: [SlideElement] = []
        let url = root.appendingPathComponent(partPath)
        if let doc = try? loadSlideDocument(url),
           let spTree = doc.rootElement()?.firstDescendant(localName: "spTree") {
            let rels = loadRelationships(forPart: partPath)
            let partDir = (partPath as NSString).deletingLastPathComponent
            for node in spTree.children?.compactMap({ $0 as? XMLElement }) ?? [] {
                switch node.localName {
                case "sp":
                    guard node.firstDescendant(localName: "ph") == nil else { continue }
                    if let el = parseShape(node, context: SlideContext()) { elements.append(el) }
                case "pic":
                    if let el = parsePicture(node, rels: rels, partDir: partDir) {
                        elements.append(.image(el))
                    }
                default:
                    break
                }
            }
        }
        inheritedElementsCache[partPath] = elements
        return elements
    }

    // MARK: - Background inheritance

    /// Resolves a slide's background, inheriting from the layout then master when
    /// the slide (or layout) defines none — the same fallback order PowerPoint uses.
    private func resolveBackground(slidePart: String, slideRoot: XMLElement, context: SlideContext) -> SlideBackground? {
        if let bg = backgroundFill(inPart: slidePart, root: slideRoot) { return bg }
        if let layoutPath = context.layoutPath, let bg = backgroundFill(inPart: layoutPath, root: nil) { return bg }
        if let masterPath = context.masterPath, let bg = backgroundFill(inPart: masterPath, root: nil) { return bg }
        return nil
    }

    /// Reads a `p:bg` from a part: a solid color, or a full-bleed image (`blipFill`).
    /// Pass `root` if the document is already loaded to avoid re-parsing.
    private func backgroundFill(inPart partPath: String, root providedRoot: XMLElement?) -> SlideBackground? {
        let element: XMLElement?
        if let providedRoot {
            element = providedRoot
        } else {
            let url = root.appendingPathComponent(partPath)
            element = (try? XMLDocument(contentsOf: url, options: [.nodePreserveWhitespace]))?.rootElement()
        }
        guard let bg = element?.firstDescendant(localName: "bg"),
              let bgPr = bg.firstDescendant(localName: "bgPr") else { return nil }

        // Solid color background.
        if let color = fillColor(in: bgPr) { return .color(color) }

        // Image (picture) background.
        if let blip = bgPr.firstDescendant(localName: "blip"), let embed = relAttr(blip, "embed") {
            let rels = loadRelationships(forPart: partPath)
            if let target = rels[embed] {
                let dir = (partPath as NSString).deletingLastPathComponent
                let mediaURL = root.appendingPathComponent(resolvePath(base: dir, target: target))
                if FileManager.default.fileExists(atPath: mediaURL.path) {
                    return .image(mediaURL)
                }
            }
        }
        return nil
    }

    // MARK: - Geometry

    private func xfrmFrame(in element: XMLElement?) -> CGRect? {
        guard let element,
              let xfrm = element.firstDescendant(localName: "xfrm") else { return nil }
        guard let off = xfrm.firstChild(localName: "off"),
              let ext = xfrm.firstChild(localName: "ext") else { return nil }
        let x = Double(attr(off, "x") ?? "0") ?? 0
        let y = Double(attr(off, "y") ?? "0") ?? 0
        let cx = Double(attr(ext, "cx") ?? "0") ?? 0
        let cy = Double(attr(ext, "cy") ?? "0") ?? 0
        return CGRect(x: Emu.toPoints(x), y: Emu.toPoints(y),
                      width: Emu.toPoints(cx), height: Emu.toPoints(cy))
    }

    private func geometry(in spPr: XMLElement?) -> ShapeGeometry {
        guard let prst = spPr?.firstDescendant(localName: "prstGeom"),
              let name = attr(prst, "prst") else { return .rectangle }
        switch name {
        case "rect": return .rectangle
        case "roundRect": return .roundedRectangle
        case "ellipse", "circle": return .ellipse
        default: return .other
        }
    }

    private func strokeStyle(in spPr: XMLElement?) -> StrokeInfo? {
        guard let ln = spPr?.firstChild(localName: "ln") else { return nil }
        if ln.firstChild(localName: "noFill") != nil { return nil }
        guard let color = fillColor(in: ln) else { return nil }
        let width = Double(attr(ln, "w") ?? "").map { Emu.toPoints($0) } ?? 1
        return StrokeInfo(color: color, width: max(0.5, width))
    }

    // MARK: - Color

    /// Resolves a solid fill's color from a container element (spPr, rPr, ln, bgPr…).
    private func fillColor(in element: XMLElement) -> Color? {
        if element.firstChild(localName: "noFill") != nil { return nil }
        guard let solid = element.firstDescendant(localName: "solidFill") else { return nil }
        return colorFromContainer(solid)
    }

    /// Reads an `a:srgbClr` / `a:schemeClr` / `a:sysClr` child and applies any alpha.
    private func colorFromContainer(_ container: XMLElement) -> Color? {
        if let srgb = container.firstDescendant(localName: "srgbClr"),
           let val = attr(srgb, "val") {
            return Color(hex: val).withAlpha(alphaMod(srgb))
        }
        if let scheme = container.firstDescendant(localName: "schemeClr"),
           let val = attr(scheme, "val") {
            let base = themeColors[val] ?? themeColors[schemeAlias(val)] ?? .primary
            return base.withAlpha(alphaMod(scheme))
        }
        if let sys = container.firstDescendant(localName: "sysClr") {
            if let last = attr(sys, "lastClr") { return Color(hex: last) }
            return .primary
        }
        return nil
    }

    private func schemeAlias(_ val: String) -> String {
        switch val {
        case "tx1": return "dk1"
        case "bg1": return "lt1"
        case "tx2": return "dk2"
        case "bg2": return "lt2"
        default: return val
        }
    }

    private func alphaMod(_ clr: XMLElement) -> Double {
        if let alpha = clr.firstChild(localName: "alpha"), let v = attr(alpha, "val"), let d = Double(v) {
            return d / 100000.0
        }
        return 1.0
    }

    // MARK: - Relationships

    /// Parses a part's `.rels` file → [relationshipId: target].
    private func loadRelationships(forPart partPath: String) -> [String: String] {
        let dir = (partPath as NSString).deletingLastPathComponent
        let file = (partPath as NSString).lastPathComponent
        let relsPath = dir.isEmpty ? "_rels/\(file).rels" : "\(dir)/_rels/\(file).rels"
        let url = root.appendingPathComponent(relsPath)
        guard let doc = try? XMLDocument(contentsOf: url) else { return [:] }
        var map: [String: String] = [:]
        for rel in doc.rootElement()?.descendants(localName: "Relationship") ?? [] {
            if let id = attr(rel, "Id"), let target = attr(rel, "Target") {
                map[id] = target
            }
        }
        return map
    }

    /// Resolves a relationship target (which may be relative, e.g. `../media/x.png`)
    /// against a base part directory, returning an archive-relative path.
    private func resolvePath(base: String, target: String) -> String {
        if target.hasPrefix("/") { return String(target.dropFirst()) }
        var components = base.split(separator: "/").map(String.init)
        for part in target.split(separator: "/") {
            if part == ".." { if !components.isEmpty { components.removeLast() } }
            else if part == "." { continue }
            else { components.append(String(part)) }
        }
        return components.joined(separator: "/")
    }

    // MARK: - Attribute helper

    /// Fetches an attribute by *local* name, ignoring namespace prefix.
    /// Prefers an unprefixed attribute so plain `id` wins over `r:id`.
    private func attr(_ element: XMLElement, _ localName: String) -> String? {
        if let direct = element.attribute(forName: localName)?.stringValue { return direct }
        for a in element.attributes ?? [] {
            if a.localName == localName { return a.stringValue }
        }
        return nil
    }

    /// Fetches a relationship attribute from the `r:` namespace (e.g. `r:id`, `r:embed`).
    private func relAttr(_ element: XMLElement, _ localName: String) -> String? {
        if let q = element.attribute(forName: "r:\(localName)")?.stringValue { return q }
        for a in element.attributes ?? [] {
            if a.localName == localName, a.name?.hasPrefix("r:") == true {
                return a.stringValue
            }
        }
        return nil
    }
}
