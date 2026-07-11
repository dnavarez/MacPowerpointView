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
    private var themeMajorFont: String?
    private var themeMinorFont: String?

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
        guard let doc = try? XMLDocument(contentsOf: themeURL) else { return }

        if let scheme = doc.rootElement()?.firstDescendant(localName: "clrScheme") {
            for child in scheme.children?.compactMap({ $0 as? XMLElement }) ?? [] {
                let name = child.localName ?? ""
                if let clr = colorFromContainer(child) {
                    themeColors[name] = clr
                }
            }
        }

        // Theme fonts: runs reference them as "+mj-lt" (major) / "+mn-lt" (minor),
        // and runs without an explicit font default to the minor font.
        if let fontScheme = doc.rootElement()?.firstDescendant(localName: "fontScheme") {
            themeMajorFont = fontScheme.firstChild(localName: "majorFont")?
                .firstChild(localName: "latin").flatMap { attr($0, "typeface") }
            themeMinorFont = fontScheme.firstChild(localName: "minorFont")?
                .firstChild(localName: "latin").flatMap { attr($0, "typeface") }
        }
    }

    /// Resolves theme font placeholders; nil (no explicit font) → theme minor font.
    private func themeFontName(_ name: String?) -> String? {
        switch name {
        case nil, "+mn-lt": return themeMinorFont
        case "+mj-lt": return themeMajorFont
        default: return name
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
                // Skip layout pictures that duplicate a master picture exactly
                // (same media file, same frame) — common when a layout re-places
                // the master's watermark/overlay.
                let existing = elements.compactMap { el -> (URL, CGRect)? in
                    if case .image(let p) = el { return (p.imageURL, p.frame) }
                    return nil
                }
                for el in inheritedElements(fromPart: layoutPath) {
                    if case .image(let p) = el,
                       existing.contains(where: { $0.0 == p.imageURL && $0.1 == p.frame }) {
                        continue
                    }
                    elements.append(el)
                }
            }
        }
        if let spTree = sldRoot.firstDescendant(localName: "spTree") {
            for node in spTree.children?.compactMap({ $0 as? XMLElement }) ?? [] {
                switch node.localName {
                case "sp", "cxnSp":
                    if let el = parseShape(node, context: context, rels: rels, partDir: partDir) {
                        elements.append(el)
                    }
                case "pic":
                    if let el = parsePicture(node, rels: rels, partDir: partDir) {
                        elements.append(.image(el))
                    }
                case "graphicFrame":
                    if let el = parseTable(node) {
                        elements.append(.table(el))
                    }
                case "grpSp":
                    elements.append(contentsOf: parseGroup(node, context: context, rels: rels, partDir: partDir))
                default:
                    break
                }
            }
        }

        return Slide(index: index, background: background, elements: elements,
                     buildSteps: parseBuildSteps(sldRoot),
                     hasTransition: sldRoot.firstChild(localName: "transition") != nil
                        || sldRoot.firstDescendant(localName: "transition") != nil)
    }

    // MARK: - Animation timing

    /// Extracts click-triggered build steps from a slide's `p:timing` tree.
    ///
    /// The main sequence's top-level `par` nodes each correspond to one click.
    /// Within a click group, every effect node (`cTn` carrying a `presetClass`,
    /// or a `set`-visibility behavior) contributes its target shapes: entrance
    /// effects reveal, exit effects hide. Emphasis/motion effects are ignored —
    /// their shapes are visible throughout. A group containing only
    /// with/after-previous effects merges into the preceding click.
    private func parseBuildSteps(_ sldRoot: XMLElement) -> [BuildStep] {
        guard let timing = sldRoot.firstChild(localName: "timing")
                ?? sldRoot.firstDescendant(localName: "timing") else { return [] }

        // The main sequence (fall back to the first seq if unmarked).
        let seqs = timing.descendants(localName: "seq")
        let mainSeq = seqs.first(where: { seq in
            seq.firstChild(localName: "cTn").map { attr($0, "nodeType") == "mainSeq" } ?? false
        }) ?? seqs.first
        guard let mainSeq,
              let seqCTn = mainSeq.firstChild(localName: "cTn"),
              let clickList = seqCTn.firstChild(localName: "childTnLst") else { return [] }

        var steps: [BuildStep] = []
        for clickPar in clickList.children?.compactMap({ $0 as? XMLElement }).filter({ $0.localName == "par" }) ?? [] {
            var step = BuildStep()
            var hasClickTrigger = false

            for ctn in clickPar.descendants(localName: "cTn") {
                let nodeType = attr(ctn, "nodeType")
                if nodeType == "clickEffect" || nodeType == "clickPar" { hasClickTrigger = true }

                // Effect nodes: presetClass tells entrance vs exit. When absent,
                // fall back to the set-visibility behavior's target value.
                let presetClass = attr(ctn, "presetClass")
                guard presetClass != nil || nodeType == "clickEffect"
                        || nodeType == "withEffect" || nodeType == "afterEffect" else { continue }

                let targets = ctn.descendants(localName: "spTgt")
                guard !targets.isEmpty else { continue }

                // Entrance vs exit: presetClass when present, else the
                // set-visibility behavior's target value; default entrance.
                let isEntrance: Bool
                switch presetClass {
                case "entr": isEntrance = true
                case "exit": isEntrance = false
                case nil:
                    if let setEl = ctn.descendants(localName: "set").first,
                       let val = setEl.firstDescendant(localName: "strVal").flatMap({ attr($0, "val") }) {
                        isEntrance = (val != "hidden")
                    } else {
                        isEntrance = true
                    }
                default:
                    continue // emphasis / motion paths don't change visibility
                }

                for tgt in targets {
                    guard let spid = attr(tgt, "spid") else { continue }
                    // A pRg child means the effect targets a paragraph range of
                    // the shape ("build by paragraph"), not the whole shape.
                    if isEntrance, let pRg = tgt.firstDescendant(localName: "pRg"),
                       let st = Int(attr(pRg, "st") ?? ""), let end = Int(attr(pRg, "end") ?? ""), st <= end {
                        step.paragraphReveals[spid, default: []].formUnion(st...end)
                    } else if isEntrance {
                        step.reveals.insert(spid)
                    } else {
                        step.hides.insert(spid)
                    }
                }
            }

            guard !step.reveals.isEmpty || !step.hides.isEmpty || !step.paragraphReveals.isEmpty else { continue }
            if !hasClickTrigger, !steps.isEmpty {
                // With/after-previous only: runs together with the previous click.
                steps[steps.count - 1].reveals.formUnion(step.reveals)
                steps[steps.count - 1].hides.formUnion(step.hides)
                for (spid, paras) in step.paragraphReveals {
                    steps[steps.count - 1].paragraphReveals[spid, default: []].formUnion(paras)
                }
            } else {
                steps.append(step)
            }
        }
        return steps
    }

    // MARK: - Groups

    /// Parses a `grpSp`, mapping child coordinates from the group's child space
    /// (`chOff`/`chExt`) into slide space (`off`/`ext`). Nested groups recurse.
    /// Group-level rotation is not composed onto children (rare in practice).
    private func parseGroup(_ grp: XMLElement, context: SlideContext,
                            rels: [String: String], partDir: String) -> [SlideElement] {
        var children: [SlideElement] = []
        for node in grp.children?.compactMap({ $0 as? XMLElement }) ?? [] {
            switch node.localName {
            case "sp", "cxnSp":
                if let el = parseShape(node, context: context, rels: rels, partDir: partDir) {
                    children.append(el)
                }
            case "pic":
                if let el = parsePicture(node, rels: rels, partDir: partDir) {
                    children.append(.image(el))
                }
            case "graphicFrame":
                if let el = parseTable(node) { children.append(.table(el)) }
            case "grpSp":
                children.append(contentsOf: parseGroup(node, context: context, rels: rels, partDir: partDir))
            default:
                break
            }
        }

        // Child-space → slide-space transform from the group's own xfrm.
        guard let grpSpPr = grp.firstChild(localName: "grpSpPr"),
              let xfrm = grpSpPr.firstChild(localName: "xfrm"),
              let off = xfrm.firstChild(localName: "off"),
              let ext = xfrm.firstChild(localName: "ext"),
              let chOff = xfrm.firstChild(localName: "chOff"),
              let chExt = xfrm.firstChild(localName: "chExt") else { return children }

        let ox = Emu.toPoints(Double(attr(off, "x") ?? "0") ?? 0)
        let oy = Emu.toPoints(Double(attr(off, "y") ?? "0") ?? 0)
        let ew = Emu.toPoints(Double(attr(ext, "cx") ?? "0") ?? 0)
        let eh = Emu.toPoints(Double(attr(ext, "cy") ?? "0") ?? 0)
        let cx = Emu.toPoints(Double(attr(chOff, "x") ?? "0") ?? 0)
        let cy = Emu.toPoints(Double(attr(chOff, "y") ?? "0") ?? 0)
        let cw = Emu.toPoints(Double(attr(chExt, "cx") ?? "0") ?? 0)
        let ch = Emu.toPoints(Double(attr(chExt, "cy") ?? "0") ?? 0)
        let sx = cw > 0 ? ew / cw : 1
        let sy = ch > 0 ? eh / ch : 1

        func map(_ r: CGRect) -> CGRect {
            CGRect(x: (r.minX - cx) * sx + ox,
                   y: (r.minY - cy) * sy + oy,
                   width: r.width * sx,
                   height: r.height * sy)
        }

        return children.map { el in
            switch el {
            case .text(var t):
                t.frame = map(t.frame)
                return .text(t)
            case .image(var p):
                p.frame = map(p.frame)
                return .image(p)
            case .shape(var sh):
                sh.frame = map(sh.frame)
                sh.text?.frame = sh.frame
                return .shape(sh)
            case .table(var tb):
                tb.frame = map(tb.frame)
                return .table(tb)
            }
        }
    }

    // MARK: - Tables

    /// Parses an `a:tbl` inside a `p:graphicFrame`. The frame's `ext` is often
    /// stale in exported decks, so the table's size is computed from its grid
    /// column widths and row heights instead.
    private func parseTable(_ frameEl: XMLElement) -> TableElement? {
        guard let tbl = frameEl.firstDescendant(localName: "tbl") else { return nil }
        let origin = xfrmFrame(in: frameEl)?.origin ?? .zero

        var columnWidths: [Double] = []
        if let grid = tbl.firstChild(localName: "tblGrid") {
            for col in grid.children?.compactMap({ $0 as? XMLElement }).filter({ $0.localName == "gridCol" }) ?? [] {
                columnWidths.append(Emu.toPoints(Double(attr(col, "w") ?? "0") ?? 0))
            }
        }

        var rows: [TableRow] = []
        for tr in tbl.children?.compactMap({ $0 as? XMLElement }).filter({ $0.localName == "tr" }) ?? [] {
            let height = Emu.toPoints(Double(attr(tr, "h") ?? "0") ?? 0)
            var cells: [TableCell] = []
            for tc in tr.children?.compactMap({ $0 as? XMLElement }).filter({ $0.localName == "tc" }) ?? [] {
                // Merged-away continuation cells are skipped.
                if attr(tc, "hMerge") == "1" || attr(tc, "vMerge") == "1" { continue }
                var cell = TableCell(paragraphs: [], fill: nil)
                cell.gridSpan = max(1, Int(attr(tc, "gridSpan") ?? "1") ?? 1)
                if let txBody = tc.firstChild(localName: "txBody") {
                    let defaults = listStyleDefaults(txBody.firstChild(localName: "lstStyle"))
                    cell.paragraphs = parseParagraphs(txBody, defaultSize: 14, levelDefaults: defaults)
                }
                if let tcPr = tc.firstChild(localName: "tcPr") {
                    cell.fill = fillColor(in: tcPr)
                    cell.insets = textInsets(from: tcPr, prefix: true)
                }
                cells.append(cell)
            }
            rows.append(TableRow(height: height, cells: cells))
        }
        guard !rows.isEmpty, !columnWidths.isEmpty else { return nil }

        let size = CGSize(width: columnWidths.reduce(0, +),
                          height: rows.map(\.height).reduce(0, +))
        let shapeID = frameEl.firstDescendant(localName: "cNvPr").flatMap { attr($0, "id") }
        return TableElement(shapeID: shapeID, frame: CGRect(origin: origin, size: size),
                            columnWidths: columnWidths, rows: rows)
    }

    private func parseShape(_ sp: XMLElement, context: SlideContext,
                            rels: [String: String] = [:], partDir: String = "") -> SlideElement? {
        let spPr = sp.firstDescendant(localName: "spPr")
        let shapeID = sp.firstDescendant(localName: "cNvPr").flatMap { attr($0, "id") }
        let ph = placeholderInfo(sp)
        // Own geometry first, then inherit from layout, then master.
        let frame = xfrmFrame(in: spPr) ?? inheritedFrame(for: ph, context: context) ?? .zero
        let (rotation, flipH, flipV) = xfrmOrientation(in: spPr)
        let defaultSize = defaultFontSize(for: ph, context: context)

        // Text body (may be present on plain text boxes and on autoshapes).
        var textBox: TextBox?
        if let txBody = sp.firstDescendant(localName: "txBody") {
            let levelDefaults = listStyleDefaults(txBody.firstChild(localName: "lstStyle"))
            let paragraphs = parseParagraphs(txBody, defaultSize: defaultSize, levelDefaults: levelDefaults)
            if !paragraphs.isEmpty && paragraphs.contains(where: { !$0.runs.isEmpty }) {
                textBox = TextBox(shapeID: shapeID, frame: frame, paragraphs: paragraphs, fill: nil,
                                  verticalAnchor: verticalAnchor(in: txBody),
                                  insets: textInsets(from: txBody.firstChild(localName: "bodyPr"), prefix: false))
            }
        }

        var fill = spPr.flatMap { parseFill(in: $0, rels: rels, partDir: partDir) }
        let geom = geometry(in: spPr)
        var stroke = strokeStyle(in: spPr)

        // Themed shapes carry no explicit fill/line; a <p:style> element points
        // at theme colors instead (fillRef / lnRef).
        if let style = sp.firstChild(localName: "style") {
            if fill == nil, spPr?.firstChild(localName: "noFill") == nil,
               let fillRef = style.firstChild(localName: "fillRef"),
               (Int(attr(fillRef, "idx") ?? "0") ?? 0) > 0,
               let c = colorFromContainer(fillRef) {
                fill = .solid(c)
            }
            if stroke == nil, spPr?.firstChild(localName: "ln")?.firstChild(localName: "noFill") == nil,
               let lnRef = style.firstChild(localName: "lnRef"),
               (Int(attr(lnRef, "idx") ?? "0") ?? 0) > 0,
               let c = colorFromContainer(lnRef) {
                stroke = StrokeInfo(color: c, width: 1)
            }
        }

        // Connectors/lines with no visible style still need a stroke to exist.
        if geom == .line, stroke == nil {
            stroke = StrokeInfo(color: .black, width: 1)
        }

        // A shape with a preset geometry + fill/stroke is a drawn shape;
        // a placeholder/textbox with only text is a text element.
        let hasVisibleShape = (fill != nil) || (stroke != nil)

        if hasVisibleShape {
            var tb = textBox
            tb?.frame = frame
            let shape = ShapeElement(
                shapeID: shapeID,
                frame: frame,
                fill: fill,
                stroke: stroke,
                geometry: geom,
                text: tb,
                rotation: rotation,
                flipH: flipH,
                flipV: flipV
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

        // srcRect crop: attributes are in thousandths of a percent.
        var crop = EdgeInsetsFraction.zero
        if let blipFill = pic.firstDescendant(localName: "blipFill"),
           let src = blipFill.firstChild(localName: "srcRect") {
            func frac(_ name: String) -> Double { (Double(attr(src, name) ?? "0") ?? 0) / 100000.0 }
            crop = EdgeInsetsFraction(left: frac("l"), top: frac("t"), right: frac("r"), bottom: frac("b"))
        }
        let shapeID = pic.firstDescendant(localName: "cNvPr").flatMap { attr($0, "id") }
        return PictureElement(shapeID: shapeID, frame: frame, imageURL: url, crop: crop)
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
        var autoNumCounters: [Int: Int] = [:]   // level -> last value
        for p in txBody.children?.compactMap({ $0 as? XMLElement }).filter({ $0.localName == "p" }) ?? [] {
            var para = Paragraph(runs: [])
            let pPr = p.firstChild(localName: "pPr")
            if let pPr {
                switch attr(pPr, "algn") {
                case "ctr": para.alignment = .center
                case "r": para.alignment = .trailing
                // OOXML "just" (justified) has no SwiftUI equivalent; render leading.
                default: para.alignment = .leading
                }
                para.level = Int(attr(pPr, "lvl") ?? "0") ?? 0
                if let marL = Double(attr(pPr, "marL") ?? "") { para.marginLeft = Emu.toPoints(marL) }
                if let ind = Double(attr(pPr, "indent") ?? "") { para.indent = Emu.toPoints(ind) }
                para.spaceBefore = spacingPoints(pPr.firstChild(localName: "spcBef"))
                para.spaceAfter = spacingPoints(pPr.firstChild(localName: "spcAft"))
                if let lnSpc = pPr.firstChild(localName: "lnSpc"),
                   let pct = lnSpc.firstChild(localName: "spcPct"),
                   let v = Double(attr(pct, "val") ?? "") {
                    para.lineSpacing = v / 100000.0
                }
                para.bullet = parseBullet(pPr)

                // Fall back to a level-based indent when none is specified but a
                // bullet is present, so nested bullets still step in.
                if para.marginLeft == 0 && para.indent == 0 && para.bullet != nil {
                    para.marginLeft = Double(para.level + 1) * 24
                    para.indent = -18
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
            // Resolve auto-number bullets against per-level counters.
            if var bullet = para.bullet, let format = bullet.autoNumFormat {
                let value = (autoNumCounters[para.level] ?? (bullet.startAt - 1)) + 1
                autoNumCounters[para.level] = value
                // Leaving a deeper level restarts its numbering next time.
                for lvl in autoNumCounters.keys where lvl > para.level {
                    autoNumCounters.removeValue(forKey: lvl)
                }
                bullet.glyph = autoNumberText(format: format, value: value)
                para.bullet = bullet
            }

            result.append(para)
        }
        return result
    }

    /// Text insets from `bodyPr` (lIns/tIns/rIns/bIns) or `tcPr` (marL/…), EMU.
    private func textInsets(from element: XMLElement?, prefix: Bool) -> TextInsets {
        var insets = TextInsets()
        guard let element else { return insets }
        func emu(_ name: String) -> Double? { Double(attr(element, name) ?? "").map { Emu.toPoints($0) } }
        if prefix {   // tcPr marL/marT/marR/marB
            if let v = emu("marL") { insets.leading = v }
            if let v = emu("marT") { insets.top = v }
            if let v = emu("marR") { insets.trailing = v }
            if let v = emu("marB") { insets.bottom = v }
        } else {      // bodyPr lIns/tIns/rIns/bIns
            if let v = emu("lIns") { insets.leading = v }
            if let v = emu("tIns") { insets.top = v }
            if let v = emu("rIns") { insets.trailing = v }
            if let v = emu("bIns") { insets.bottom = v }
        }
        return insets
    }

    /// Vertical anchor from a text body's `bodyPr@anchor`.
    private func verticalAnchor(in txBody: XMLElement) -> VerticalAnchor {
        switch txBody.firstChild(localName: "bodyPr").flatMap({ attr($0, "anchor") }) {
        case "ctr": return .center
        case "b": return .bottom
        default: return .top
        }
    }

    /// Points from an `a:spcBef`/`a:spcAft` element (`spcPts` in hundredths of a
    /// point; `spcPct` percentages are ignored as they need the run size).
    private func spacingPoints(_ spc: XMLElement?) -> Double {
        guard let pts = spc?.firstDescendant(localName: "spcPts"),
              let v = Double(attr(pts, "val") ?? "") else { return 0 }
        return v / 100.0
    }

    /// Parses a paragraph's bullet definition. `buNone` yields no bullet; a
    /// `buChar` is rendered in its `buFont`; `buAutoNum` becomes a simple marker.
    private func parseBullet(_ pPr: XMLElement) -> Bullet? {
        if pPr.firstChild(localName: "buNone") != nil { return nil }

        let color = pPr.firstChild(localName: "buClr").flatMap { colorFromContainer($0) }
        var sizePercent = 1.0
        if let sz = pPr.firstChild(localName: "buSzPct"), let v = Double(attr(sz, "val") ?? "") {
            sizePercent = v / 100000.0
        }
        let fontName = themeFontName(pPr.firstChild(localName: "buFont").flatMap { attr($0, "typeface") })

        if let buChar = pPr.firstChild(localName: "buChar"), let ch = attr(buChar, "char") {
            // Symbol-font bullets (Wingdings etc.) render as unexpected glyphs in
            // their raw font on macOS; map them to Unicode equivalents rendered in
            // the normal text font, matching what PowerPoint/Keynote display.
            if let mapped = SymbolFont.unicodeBullet(char: ch, font: fontName) {
                return Bullet(glyph: mapped, fontName: nil, color: color, sizePercent: sizePercent)
            }
            return Bullet(glyph: ch, fontName: fontName, color: color, sizePercent: sizePercent)
        }
        if let auto = pPr.firstChild(localName: "buAutoNum") {
            // Actual number text is resolved in parseParagraphs, which tracks
            // per-level counters across sibling paragraphs.
            return Bullet(glyph: "", fontName: nil, color: color, sizePercent: sizePercent,
                          autoNumFormat: attr(auto, "type") ?? "arabicPeriod",
                          startAt: Int(attr(auto, "startAt") ?? "1") ?? 1)
        }
        return nil
    }

    /// Formats an auto-number bullet value per the OOXML `buAutoNum` scheme.
    private func autoNumberText(format: String, value: Int) -> String {
        func letters(_ n: Int) -> String {
            var n = n, out = ""
            while n > 0 {
                n -= 1
                out = String(UnicodeScalar(65 + n % 26)!) + out
                n /= 26
            }
            return out
        }
        func roman(_ n: Int) -> String {
            let table: [(Int, String)] = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
                                          (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
                                          (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")]
            var n = n, out = ""
            for (v, sym) in table { while n >= v { out += sym; n -= v } }
            return out
        }
        let core: String
        if format.hasPrefix("alphaLc") { core = letters(value).lowercased() }
        else if format.hasPrefix("alphaUc") { core = letters(value) }
        else if format.hasPrefix("romanLc") { core = roman(value).lowercased() }
        else if format.hasPrefix("romanUc") { core = roman(value) }
        else { core = String(value) }

        if format.hasSuffix("ParenR") { return core + ")" }
        if format.hasSuffix("ParenBoth") { return "(" + core + ")" }
        if format.hasSuffix("Plain") { return core }
        return core + "."   // *Period and default
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
        guard let rPr else {
            run.fontName = themeFontName(run.fontName)
            return run
        }
        if let szStr = attr(rPr, "sz"), let sz = Double(szStr) {
            run.fontSize = sz / 100.0
        }
        if let b = attr(rPr, "b") { run.bold = (b == "1") }
        if let i = attr(rPr, "i") { run.italic = (i == "1") }
        if let u = attr(rPr, "u"), u != "none" { run.underline = true }
        if let strike = attr(rPr, "strike"), strike != "noStrike" { run.strikethrough = true }
        if let c = fillColor(in: rPr) { run.color = c }
        if let latin = rPr.firstChild(localName: "latin"), let tf = attr(latin, "typeface") {
            run.fontName = tf
        }
        run.fontName = themeFontName(run.fontName)
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
                case "sp", "cxnSp":
                    guard node.firstDescendant(localName: "ph") == nil else { continue }
                    if let el = parseShape(node, context: SlideContext(), rels: rels, partDir: partDir) {
                        elements.append(el)
                    }
                case "pic":
                    if let el = parsePicture(node, rels: rels, partDir: partDir) {
                        elements.append(.image(el))
                    }
                case "grpSp":
                    elements.append(contentsOf: parseGroup(node, context: SlideContext(), rels: rels, partDir: partDir))
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

        let rels = loadRelationships(forPart: partPath)
        let dir = (partPath as NSString).deletingLastPathComponent
        switch parseFill(in: bgPr, rels: rels, partDir: dir) {
        case .solid(let color): return .color(color)
        case .gradient(let stops, let angle): return .gradient(stops: stops, angle: angle)
        case .image(let url): return .image(url)
        case nil: return nil
        }
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

    /// Rotation (degrees) and flips from a shape's `xfrm`.
    private func xfrmOrientation(in element: XMLElement?) -> (Double, Bool, Bool) {
        guard let xfrm = element?.firstDescendant(localName: "xfrm") else { return (0, false, false) }
        let rot = (Double(attr(xfrm, "rot") ?? "") ?? 0) / 60000.0
        return (rot, attr(xfrm, "flipH") == "1", attr(xfrm, "flipV") == "1")
    }

    /// Parses a full fill (solid, gradient, or picture) from direct children of
    /// a properties container.
    private func parseFill(in element: XMLElement, rels: [String: String], partDir: String) -> Fill? {
        if element.firstChild(localName: "noFill") != nil { return nil }
        if let solid = element.firstChild(localName: "solidFill"), let c = colorFromContainer(solid) {
            return .solid(c)
        }
        if let grad = element.firstChild(localName: "gradFill") {
            var stops: [GradientStop] = []
            for gs in grad.descendants(localName: "gs") {
                let pos = (Double(attr(gs, "pos") ?? "0") ?? 0) / 100000.0
                if let c = colorFromContainer(gs) {
                    stops.append(GradientStop(position: pos, color: c))
                }
            }
            stops.sort { $0.position < $1.position }
            var angle = 90.0
            if let lin = grad.firstChild(localName: "lin"), let ang = Double(attr(lin, "ang") ?? "") {
                angle = ang / 60000.0
            }
            if stops.count >= 2 { return .gradient(stops: stops, angle: angle) }
            if let only = stops.first { return .solid(only.color) }
        }
        if let blipFill = element.firstChild(localName: "blipFill"),
           let blip = blipFill.firstDescendant(localName: "blip"),
           let embed = relAttr(blip, "embed"),
           let target = rels[embed] {
            let url = root.appendingPathComponent(resolvePath(base: partDir, target: target))
            if FileManager.default.fileExists(atPath: url.path) { return .image(url) }
        }
        return nil
    }

    private func geometry(in spPr: XMLElement?) -> ShapeGeometry {
        guard let prst = spPr?.firstDescendant(localName: "prstGeom"),
              let name = attr(prst, "prst") else { return .rectangle }
        switch name {
        case "rect", "snip1Rect", "snip2SameRect", "snip2DiagRect": return .rectangle
        case "roundRect", "round1Rect", "round2SameRect": return .roundedRectangle
        case "ellipse", "circle": return .ellipse
        case "triangle": return .triangle
        case "rtTriangle": return .rightTriangle
        case "diamond": return .diamond
        case "parallelogram": return .parallelogram
        case "trapezoid": return .trapezoid
        case "pentagon": return .pentagon
        case "hexagon": return .hexagon
        case "chevron": return .chevron
        case "homePlate": return .homePlate
        case "rightArrow", "arrow", "notchedRightArrow": return .arrowRight
        case "leftArrow": return .arrowLeft
        case "upArrow": return .arrowUp
        case "downArrow": return .arrowDown
        case "star5", "star4", "star6": return .star5
        case "line", "straightConnector1", "bentConnector2", "bentConnector3",
             "curvedConnector2", "curvedConnector3": return .line
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

    /// Resolves a solid fill's color from a container element (rPr, ln, bgPr,
    /// tcPr, defRPr…). Only direct children are considered so a nested line or
    /// effect fill can't masquerade as the container's own fill.
    private func fillColor(in element: XMLElement) -> Color? {
        if element.firstChild(localName: "noFill") != nil { return nil }
        guard let solid = element.firstChild(localName: "solidFill") else { return nil }
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
            let base = themeColors[val] ?? themeColors[schemeAlias(val)] ?? .black
            return base.withAlpha(alphaMod(scheme))
        }
        if let sys = container.firstDescendant(localName: "sysClr") {
            if let last = attr(sys, "lastClr") { return Color(hex: last) }
            return .black
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
