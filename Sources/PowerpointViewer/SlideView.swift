import SwiftUI

/// Renders a single slide, scaled to fit the available space while preserving
/// the slide's aspect ratio.
///
/// Geometry and font sizes from the model (in slide points) are multiplied by
/// the fit scale and laid out at final size — rather than rendering at native
/// size and transforming with `scaleEffect` — so text stays crisp at any zoom
/// (thumbnails and full-screen alike).
struct SlideView: View {
    let slide: Slide
    let slideSize: CGSize
    var showShadow: Bool = true
    /// Shape ids currently hidden by animation build state (presentation mode).
    var hiddenShapeIDs: Set<String> = []
    /// Paragraph indices hidden per shape id (paragraph-level builds).
    var hiddenParagraphs: [String: Set<Int>] = [:]

    var body: some View {
        GeometryReader { geo in
            let scale = min(geo.size.width / max(1, slideSize.width),
                            geo.size.height / max(1, slideSize.height))
            let w = slideSize.width * scale
            let h = slideSize.height * scale

            ZStack(alignment: .topLeading) {
                backgroundView
                    .frame(width: w, height: h)

                ForEach(slide.elements) { element in
                    let hidden = element.shapeID.map { hiddenShapeIDs.contains($0) } ?? false
                    elementView(element, scale: scale)
                        .frame(width: max(1, element.frame.width * scale),
                               height: max(1, element.frame.height * scale),
                               alignment: .topLeading)
                        .offset(x: element.frame.minX * scale,
                                y: element.frame.minY * scale)
                        .opacity(hidden ? 0 : 1)
                }
            }
            .frame(width: w, height: h, alignment: .topLeading)
            .clipped()
            .shadow(color: showShadow ? .black.opacity(0.15) : .clear, radius: 8, y: 2)
            .frame(width: geo.size.width, height: geo.size.height, alignment: .center)
        }
    }

    @ViewBuilder
    private var backgroundView: some View {
        switch slide.background {
        case .color(let color):
            color
        case .gradient(let stops, let angle):
            LinearGradient(gradient: Gradient(stops: stops.map {
                .init(color: $0.color, location: $0.position)
            }), startPoint: FillPainter.startPoint(angle: angle),
               endPoint: FillPainter.endPoint(angle: angle))
        case .image(let url):
            if let nsImage = NSImage(contentsOf: url) {
                Image(nsImage: nsImage)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
            } else {
                Color.white
            }
        case nil:
            Color.white
        }
    }

    @ViewBuilder
    private func elementView(_ element: SlideElement, scale: CGFloat) -> some View {
        let hiddenParas = element.shapeID.flatMap { hiddenParagraphs[$0] } ?? []
        switch element {
        case .text(let box):
            TextBoxView(box: box, scale: scale, hiddenParagraphIndices: hiddenParas)
        case .image(let pic):
            PictureView(picture: pic)
        case .shape(let shape):
            ShapeView(shape: shape, scale: scale, hiddenParagraphIndices: hiddenParas)
        case .table(let table):
            TableView(table: table, scale: scale)
        }
    }
}

/// Renders a table: rows of cells with grid-defined column widths.
struct TableView: View {
    let table: TableElement
    let scale: CGFloat

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(Array(table.rows.enumerated()), id: \.offset) { _, row in
                HStack(alignment: .top, spacing: 0) {
                    ForEach(Array(row.cells.enumerated()), id: \.offset) { index, cell in
                        cellView(cell, row: row, cellIndex: index)
                    }
                }
            }
        }
        .overlay(Rectangle().strokeBorder(Color.gray.opacity(0.5), lineWidth: max(0.5, 0.5 * scale)))
    }

    @ViewBuilder
    private func cellView(_ cell: TableCell, row: TableRow, cellIndex: Int) -> some View {
        let width = cellWidth(row: row, cellIndex: cellIndex)
        TextBoxView(box: TextBox(frame: .zero, paragraphs: cell.paragraphs, fill: nil,
                                 autoShrink: false, insets: cell.insets),
                    scale: scale)
            .frame(width: width * scale, alignment: .topLeading)
            .frame(minHeight: row.height * scale, alignment: .topLeading)
            .background(cell.fill ?? Color.clear)
            .overlay(Rectangle().strokeBorder(Color.gray.opacity(0.35), lineWidth: max(0.5, 0.5 * scale)))
    }

    /// Sums the grid columns this cell spans, accounting for earlier spans.
    private func cellWidth(row: TableRow, cellIndex: Int) -> Double {
        var gridStart = 0
        for i in 0..<cellIndex { gridStart += row.cells[i].gridSpan }
        let span = row.cells[cellIndex].gridSpan
        let end = min(gridStart + span, table.columnWidths.count)
        guard gridStart < end else { return table.columnWidths.last ?? 60 }
        return table.columnWidths[gridStart..<end].reduce(0, +)
    }
}

/// Converts an OOXML gradient angle (degrees; 0° = left→right, clockwise
/// positive, y-down) into SwiftUI unit points.
enum FillPainter {
    static func startPoint(angle: Double) -> UnitPoint {
        let rad = angle * .pi / 180
        return UnitPoint(x: 0.5 - cos(rad) / 2, y: 0.5 - sin(rad) / 2)
    }
    static func endPoint(angle: Double) -> UnitPoint {
        let rad = angle * .pi / 180
        return UnitPoint(x: 0.5 + cos(rad) / 2, y: 0.5 + sin(rad) / 2)
    }

    static func style(for fill: Fill?) -> AnyShapeStyle {
        switch fill {
        case .solid(let c):
            return AnyShapeStyle(c)
        case .gradient(let stops, let angle):
            return AnyShapeStyle(LinearGradient(
                gradient: Gradient(stops: stops.map { .init(color: $0.color, location: $0.position) }),
                startPoint: startPoint(angle: angle),
                endPoint: endPoint(angle: angle)))
        case .image, nil:
            return AnyShapeStyle(Color.clear)
        }
    }
}

/// A polygon defined by unit-square points, scaled to its frame.
struct UnitPolygon: Shape {
    let points: [(CGFloat, CGFloat)]
    func path(in rect: CGRect) -> Path {
        var p = Path()
        guard let first = points.first else { return p }
        p.move(to: CGPoint(x: rect.minX + first.0 * rect.width, y: rect.minY + first.1 * rect.height))
        for pt in points.dropFirst() {
            p.addLine(to: CGPoint(x: rect.minX + pt.0 * rect.width, y: rect.minY + pt.1 * rect.height))
        }
        p.closeSubpath()
        return p
    }
}

/// A five-pointed star inscribed in the frame.
struct StarShape: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path()
        let c = CGPoint(x: rect.midX, y: rect.midY)
        let rOuter = min(rect.width, rect.height) / 2
        let rInner = rOuter * 0.382
        for i in 0..<10 {
            let angle = (Double(i) * 36.0 - 90.0) * .pi / 180
            let r = i.isMultiple(of: 2) ? rOuter : rInner
            let pt = CGPoint(x: c.x + cos(angle) * r, y: c.y + sin(angle) * r)
            if i == 0 { p.move(to: pt) } else { p.addLine(to: pt) }
        }
        p.closeSubpath()
        return p
    }
}

/// A straight line from the frame's top-leading to bottom-trailing corner
/// (flips are applied by the parent).
struct LineShape: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path()
        p.move(to: CGPoint(x: rect.minX, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY))
        return p
    }
}

/// Draws an autoshape (fill + stroke) with optional overlaid text.
struct ShapeView: View {
    let shape: ShapeElement
    let scale: CGFloat
    var hiddenParagraphIndices: Set<Int> = []

    var body: some View {
        ZStack {
            filledShape
            if let text = shape.text {
                TextBoxView(box: text, scale: scale, hiddenParagraphIndices: hiddenParagraphIndices)
            }
        }
        .scaleEffect(x: shape.flipH ? -1 : 1, y: shape.flipV ? -1 : 1)
        .rotationEffect(.degrees(shape.rotation))
    }

    @ViewBuilder
    private var filledShape: some View {
        let strokeWidth = (shape.stroke?.width ?? 0) * scale
        if case .line = shape.geometry {
            LineShape()
                .stroke(shape.stroke?.color ?? .black, lineWidth: max(strokeWidth, 0.75 * scale))
        } else if case .image(let url) = shape.fill, let nsImage = NSImage(contentsOf: url) {
            Image(nsImage: nsImage)
                .resizable()
                .aspectRatio(contentMode: .fill)
                .clipShape(shapePath)
                .overlay(shapePath.stroke(shape.stroke?.color ?? .clear, lineWidth: strokeWidth))
        } else {
            shapePath
                .fill(FillPainter.style(for: shape.fill))
                .overlay(shapePath.stroke(shape.stroke?.color ?? .clear, lineWidth: strokeWidth))
        }
    }

    private var shapePath: AnyShape {
        switch shape.geometry {
        case .ellipse:
            return AnyShape(Ellipse())
        case .roundedRectangle:
            let radius = min(shape.frame.width, shape.frame.height) * 0.12 * scale
            return AnyShape(RoundedRectangle(cornerRadius: radius))
        case .triangle:
            return AnyShape(UnitPolygon(points: [(0.5, 0), (1, 1), (0, 1)]))
        case .rightTriangle:
            return AnyShape(UnitPolygon(points: [(0, 0), (0, 1), (1, 1)]))
        case .diamond:
            return AnyShape(UnitPolygon(points: [(0.5, 0), (1, 0.5), (0.5, 1), (0, 0.5)]))
        case .parallelogram:
            return AnyShape(UnitPolygon(points: [(0.25, 0), (1, 0), (0.75, 1), (0, 1)]))
        case .trapezoid:
            return AnyShape(UnitPolygon(points: [(0.25, 0), (0.75, 0), (1, 1), (0, 1)]))
        case .pentagon:
            return AnyShape(UnitPolygon(points: [(0.5, 0), (1, 0.38), (0.81, 1), (0.19, 1), (0, 0.38)]))
        case .hexagon:
            return AnyShape(UnitPolygon(points: [(0.25, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0.25, 1), (0, 0.5)]))
        case .chevron:
            return AnyShape(UnitPolygon(points: [(0, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0, 1), (0.25, 0.5)]))
        case .homePlate:
            return AnyShape(UnitPolygon(points: [(0, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0, 1)]))
        case .arrowRight:
            return AnyShape(UnitPolygon(points: [(0, 0.3), (0.6, 0.3), (0.6, 0), (1, 0.5),
                                                 (0.6, 1), (0.6, 0.7), (0, 0.7)]))
        case .arrowLeft:
            return AnyShape(UnitPolygon(points: [(1, 0.3), (0.4, 0.3), (0.4, 0), (0, 0.5),
                                                 (0.4, 1), (0.4, 0.7), (1, 0.7)]))
        case .arrowUp:
            return AnyShape(UnitPolygon(points: [(0.3, 1), (0.3, 0.4), (0, 0.4), (0.5, 0),
                                                 (1, 0.4), (0.7, 0.4), (0.7, 1)]))
        case .arrowDown:
            return AnyShape(UnitPolygon(points: [(0.3, 0), (0.3, 0.6), (0, 0.6), (0.5, 1),
                                                 (1, 0.6), (0.7, 0.6), (0.7, 0)]))
        case .star5:
            return AnyShape(StarShape())
        default:
            return AnyShape(Rectangle())
        }
    }
}

/// Renders a picture from a local file on disk.
struct PictureView: View {
    let picture: PictureElement

    var body: some View {
        if let nsImage = croppedImage {
            Image(nsImage: nsImage)
                .resizable()
                .aspectRatio(contentMode: .fill)
                .clipped()
        } else {
            Rectangle().fill(Color.gray.opacity(0.2))
        }
    }

    /// Loads the image, applying the OOXML `srcRect` crop if present.
    private var croppedImage: NSImage? {
        guard let nsImage = NSImage(contentsOf: picture.imageURL) else { return nil }
        guard !picture.crop.isZero,
              let cg = nsImage.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            return nsImage
        }
        let w = Double(cg.width), h = Double(cg.height)
        let rect = CGRect(x: picture.crop.left * w,
                          y: picture.crop.top * h,
                          width: (1 - picture.crop.left - picture.crop.right) * w,
                          height: (1 - picture.crop.top - picture.crop.bottom) * h)
        guard rect.width > 0, rect.height > 0, let sub = cg.cropping(to: rect) else { return nsImage }
        return NSImage(cgImage: sub, size: NSSize(width: rect.width, height: rect.height))
    }
}

/// Measures a view's laid-out height so text can be shrunk to fit its box.
private struct HeightPreferenceKey: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}

/// Lays out a text body: a vertical stack of paragraphs, each built by
/// concatenating styled runs. All point sizes are multiplied by `scale`.
///
/// When the box's frame is known (`autoShrink`), the content is measured and
/// uniformly scaled down if it would overflow — emulating PowerPoint's
/// "shrink text on overflow" and absorbing any font-substitution metric drift
/// that would otherwise clip text. Vertical anchoring follows `bodyPr@anchor`.
struct TextBoxView: View {
    let box: TextBox
    let scale: CGFloat
    /// Paragraph indices hidden by the current animation build step.
    var hiddenParagraphIndices: Set<Int> = []
    @State private var measuredHeight: CGFloat = 0

    private var boxHeight: CGFloat { box.frame.height * scale }

    var body: some View {
        let fit: CGFloat = {
            guard box.autoShrink, boxHeight > 0, measuredHeight > boxHeight else { return 1 }
            return max(0.2, boxHeight / measuredHeight)
        }()

        paragraphStack
            .background(GeometryReader { geo in
                Color.clear.preference(key: HeightPreferenceKey.self, value: geo.size.height)
            })
            .onPreferenceChange(HeightPreferenceKey.self) { measuredHeight = $0 }
            .scaleEffect(fit, anchor: .top)
            .frame(maxWidth: .infinity, maxHeight: box.autoShrink ? .infinity : nil,
                   alignment: anchorAlignment)
    }

    private var paragraphStack: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(Array(box.paragraphs.enumerated()), id: \.offset) { index, para in
                paragraphView(para)
                    .padding(.top, para.spaceBefore * scale)
                    .padding(.bottom, para.spaceAfter * scale)
                    .frame(maxWidth: .infinity, alignment: frameAlignment(para.alignment))
                    .opacity(hiddenParagraphIndices.contains(index) ? 0 : 1)
            }
        }
        .padding(.leading, box.insets.leading * scale)
        .padding(.trailing, box.insets.trailing * scale)
        .padding(.top, box.insets.top * scale)
        .padding(.bottom, box.insets.bottom * scale)
    }

    private var anchorAlignment: Alignment {
        switch box.verticalAnchor {
        case .top: return .top
        case .center: return .center
        case .bottom: return .bottom
        }
    }

    @ViewBuilder
    private func paragraphView(_ para: Paragraph) -> some View {
        // Hanging-indent geometry: the paragraph block's leftmost edge is
        // marginLeft + indent; the bullet occupies the "hang" gap and the text
        // body starts at marginLeft.
        let leftEdge = max(0, para.marginLeft + para.indent)
        let baseSize = para.runs.first?.fontSize ?? 18
        // Reserve at least a glyph's worth of room for the bullet so it isn't
        // clipped when the file's hanging gap is narrower than the bullet.
        let hang = para.bullet != nil ? max(-para.indent, baseSize * 0.95) : max(0, -para.indent)

        // An empty paragraph is vertical spacing only — no bullet.
        let hasText = para.runs.contains { !$0.text.trimmingCharacters(in: .whitespaces).isEmpty }

        HStack(alignment: .firstTextBaseline, spacing: 0) {
            if let bullet = para.bullet, hasText {
                bulletView(bullet, baseSize: baseSize)
                    .fixedSize()
                    .frame(width: hang * scale, alignment: .leading)
            }
            styledText(para)
                .multilineTextAlignment(para.alignment)
                .lineSpacing(max(0, (para.lineSpacing - 1) * baseSize * scale))
                .fixedSize(horizontal: false, vertical: true)
                .shadow(color: para.runs.contains(where: \.shadow) ? .black.opacity(0.45) : .clear,
                        radius: 2.5 * scale, y: 2 * scale)
                // Honor the paragraph's own alignment — a hardcoded .leading
                // here once broke centered hymn lyrics.
                .frame(maxWidth: .infinity, alignment: frameAlignment(para.alignment))
        }
        .padding(.leading, leftEdge * scale)
    }

    private func bulletView(_ bullet: Bullet, baseSize: Double) -> some View {
        Text(bullet.glyph)
            .font(FontResolver.font(name: bullet.fontName, size: baseSize * bullet.sizePercent * scale))
            .foregroundColor(bullet.color ?? .black)
    }

    private func styledText(_ para: Paragraph) -> Text {
        para.runs.reduce(Text("")) { partial, run in
            partial + styledRun(run)
        }
    }

    private func styledRun(_ run: TextRun) -> Text {
        var text = Text(run.text)
        text = text.font(FontResolver.font(name: run.fontName, size: run.fontSize * scale))
        if run.bold { text = text.bold() }
        if run.italic { text = text.italic() }
        if run.underline { text = text.underline() }
        if run.strikethrough { text = text.strikethrough() }
        return text.foregroundColor(run.color)
    }

    private func frameAlignment(_ a: TextAlignment) -> Alignment {
        switch a {
        case .center: return .center
        case .trailing: return .trailing
        default: return .leading
        }
    }
}
