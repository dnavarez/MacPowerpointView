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
                    elementView(element, scale: scale)
                        .frame(width: max(1, element.frame.width * scale),
                               height: max(1, element.frame.height * scale),
                               alignment: .topLeading)
                        .offset(x: element.frame.minX * scale,
                                y: element.frame.minY * scale)
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
        switch element {
        case .text(let box):
            TextBoxView(box: box, scale: scale)
        case .image(let pic):
            PictureView(picture: pic)
        case .shape(let shape):
            ShapeView(shape: shape, scale: scale)
        }
    }
}

/// Draws an autoshape (fill + stroke) with optional overlaid text.
struct ShapeView: View {
    let shape: ShapeElement
    let scale: CGFloat

    var body: some View {
        ZStack {
            shapePath
                .fill(shape.fill?.color ?? Color.clear)
                .overlay(
                    shapePath.stroke(shape.stroke?.color ?? .clear,
                                     lineWidth: (shape.stroke?.width ?? 0) * scale)
                )
            if let text = shape.text {
                TextBoxView(box: text, scale: scale)
            }
        }
        .rotationEffect(.degrees(shape.rotation))
    }

    private var shapePath: AnyShape {
        switch shape.geometry {
        case .ellipse:
            return AnyShape(Ellipse())
        case .roundedRectangle:
            let radius = min(shape.frame.width, shape.frame.height) * 0.12 * scale
            return AnyShape(RoundedRectangle(cornerRadius: radius))
        default:
            return AnyShape(Rectangle())
        }
    }
}

/// Renders a picture from a local file on disk.
struct PictureView: View {
    let picture: PictureElement

    var body: some View {
        if let nsImage = NSImage(contentsOf: picture.imageURL) {
            Image(nsImage: nsImage)
                .resizable()
                .aspectRatio(contentMode: .fill)
                .clipped()
        } else {
            Rectangle().fill(Color.gray.opacity(0.2))
        }
    }
}

/// Lays out a text body: a vertical stack of paragraphs, each built by
/// concatenating styled runs. All point sizes are multiplied by `scale`.
struct TextBoxView: View {
    let box: TextBox
    let scale: CGFloat

    var body: some View {
        VStack(alignment: .leading, spacing: 2 * scale) {
            ForEach(Array(box.paragraphs.enumerated()), id: \.offset) { _, para in
                paragraphView(para)
                    .frame(maxWidth: .infinity, alignment: frameAlignment(para.alignment))
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .padding(.horizontal, 4 * scale)
        .padding(.vertical, 2 * scale)
    }

    @ViewBuilder
    private func paragraphView(_ para: Paragraph) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 4 * scale) {
            if para.hasBullet {
                Text("•")
                    .font(.system(size: (para.runs.first?.fontSize ?? 18) * scale))
                    .foregroundColor(para.runs.first?.color ?? .primary)
            }
            styledText(para)
                .multilineTextAlignment(para.alignment)
                .shadow(color: para.runs.contains(where: \.shadow) ? .black.opacity(0.45) : .clear,
                        radius: 2.5 * scale, y: 2 * scale)
        }
        .padding(.leading, CGFloat(para.level) * 18 * scale)
    }

    private func styledText(_ para: Paragraph) -> Text {
        para.runs.reduce(Text("")) { partial, run in
            partial + styledRun(run)
        }
    }

    private func styledRun(_ run: TextRun) -> Text {
        var text = Text(run.text)
        let size = run.fontSize * scale
        if let name = run.fontName {
            text = text.font(.custom(name, size: size))
        } else {
            text = text.font(.system(size: size))
        }
        if run.bold { text = text.bold() }
        if run.italic { text = text.italic() }
        if run.underline { text = text.underline() }
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
