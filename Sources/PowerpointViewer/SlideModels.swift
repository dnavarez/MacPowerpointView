import Foundation
import SwiftUI

/// Geometry and content model for a parsed presentation.
///
/// All measurements are stored in **points** (1/72 inch). OOXML natively uses
/// English Metric Units (EMU); conversion happens at parse time via `Emu`.
enum Emu {
    static let perPoint: Double = 12700
    static func toPoints(_ emu: Double) -> Double { emu / perPoint }
    static func toPoints(_ emu: Int) -> Double { Double(emu) / perPoint }
}

struct Presentation {
    /// Slide canvas size in points.
    var size: CGSize
    var slides: [Slide]
}

struct Slide: Identifiable {
    let id = UUID()
    var index: Int
    var background: SlideBackground?
    var elements: [SlideElement]
}

/// A slide background, which may be a solid color or a full-bleed image.
/// Resolved from the slide itself, or inherited from its layout or master.
enum SlideBackground {
    case color(Color)
    case image(URL)
}

enum SlideElement: Identifiable {
    case text(TextBox)
    case image(PictureElement)
    case shape(ShapeElement)

    var id: UUID {
        switch self {
        case .text(let t): return t.id
        case .image(let p): return p.id
        case .shape(let s): return s.id
        }
    }

    var frame: CGRect {
        switch self {
        case .text(let t): return t.frame
        case .image(let p): return p.frame
        case .shape(let s): return s.frame
        }
    }
}

/// A positioned text container (from a shape's text body).
struct TextBox: Identifiable {
    let id = UUID()
    var frame: CGRect
    var paragraphs: [Paragraph]
    /// Optional fill behind the text (shape fill).
    var fill: FillStyle?
}

struct Paragraph {
    var runs: [TextRun]
    var alignment: TextAlignment = .leading
    /// Bullet level (0 = top level). Used for indentation.
    var level: Int = 0
    var hasBullet: Bool = false
}

struct TextRun {
    var text: String
    var fontSize: Double = 18          // points
    var bold: Bool = false
    var italic: Bool = false
    var underline: Bool = false
    var color: Color = .primary
    var fontName: String?
    /// Whether the run carries an outer shadow effect (common on lyric decks).
    var shadow: Bool = false
}

struct PictureElement: Identifiable {
    let id = UUID()
    var frame: CGRect
    var imageURL: URL
}

/// A geometric shape with an optional fill and border.
struct ShapeElement: Identifiable {
    let id = UUID()
    var frame: CGRect
    var fill: FillStyle?
    var stroke: StrokeInfo?
    var geometry: ShapeGeometry
    /// Some shapes carry text; kept alongside the geometry.
    var text: TextBox?
    var rotation: Double = 0   // degrees
}

enum ShapeGeometry {
    case rectangle
    case roundedRectangle
    case ellipse
    case other   // rendered as a rectangle fallback
}

struct FillStyle {
    var color: Color
}

struct StrokeInfo {
    var color: Color
    var width: Double
}
