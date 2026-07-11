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
    /// Click-triggered animation steps, in order. Each advance during a
    /// presentation executes the next step before moving to the next slide.
    var buildSteps: [BuildStep] = []
    /// Whether the slide defines an entrance transition (rendered as a crossfade).
    var hasTransition: Bool = false
}

/// One click's worth of animation: shapes revealed (entrance effects) and
/// shapes hidden (exit effects), identified by their drawing `cNvPr` id.
/// Text placeholders animated "by paragraph" reveal specific paragraph indices
/// (`paragraphReveals`) instead of the whole shape.
struct BuildStep {
    var reveals: Set<String> = []
    var hides: Set<String> = []
    var paragraphReveals: [String: Set<Int>] = [:]
}

/// A slide background, which may be a solid color or a full-bleed image.
/// Resolved from the slide itself, or inherited from its layout or master.
enum SlideBackground {
    case color(Color)
    case gradient(stops: [GradientStop], angle: Double)
    case image(URL)
}

enum SlideElement: Identifiable {
    case text(TextBox)
    case image(PictureElement)
    case shape(ShapeElement)
    case table(TableElement)

    var id: UUID {
        switch self {
        case .text(let t): return t.id
        case .image(let p): return p.id
        case .shape(let s): return s.id
        case .table(let t): return t.id
        }
    }

    var frame: CGRect {
        switch self {
        case .text(let t): return t.frame
        case .image(let p): return p.frame
        case .shape(let s): return s.frame
        case .table(let t): return t.frame
        }
    }

    /// The drawing id used by animation timing, if the element has one.
    var shapeID: String? {
        switch self {
        case .text(let t): return t.shapeID
        case .image(let p): return p.shapeID
        case .shape(let s): return s.shapeID
        case .table(let t): return t.shapeID
        }
    }
}

/// A table from a `graphicFrame` (`a:tbl`).
struct TableElement: Identifiable {
    let id = UUID()
    /// Drawing id (`cNvPr@id`) used by animation timing to target this shape.
    var shapeID: String?
    var frame: CGRect
    /// Column widths in points (from `a:tblGrid`).
    var columnWidths: [Double]
    var rows: [TableRow]
}

struct TableRow {
    /// Minimum row height in points; rows grow to fit content.
    var height: Double
    var cells: [TableCell]
}

struct TableCell {
    var paragraphs: [Paragraph]
    var fill: Color?
    /// Number of grid columns this cell spans (≥ 1).
    var gridSpan: Int = 1
    var insets: TextInsets = TextInsets()
}

/// A positioned text container (from a shape's text body).
struct TextBox: Identifiable {
    let id = UUID()
    /// Drawing id (`cNvPr@id`) used by animation timing to target this shape.
    var shapeID: String?
    var frame: CGRect
    var paragraphs: [Paragraph]
    /// Optional fill behind the text (shape fill).
    var fill: Fill?
    /// Vertical anchoring within the box (OOXML `bodyPr@anchor`).
    var verticalAnchor: VerticalAnchor = .top
    /// Whether text should shrink to fit the box when it overflows. Enabled for
    /// slide text boxes (whose frame is known) and disabled for table cells.
    var autoShrink: Bool = true
    var insets: TextInsets = TextInsets()
}

enum VerticalAnchor {
    case top, center, bottom
}

/// Text-body insets in points (OOXML `bodyPr` lIns/tIns/rIns/bIns or table
/// `tcPr` marL/marT/marR/marB). Defaults are OOXML's: 0.1" sides, 0.05" ends.
struct TextInsets {
    var leading: Double = 7.2
    var top: Double = 3.6
    var trailing: Double = 7.2
    var bottom: Double = 3.6
}

struct Paragraph {
    var runs: [TextRun]
    var alignment: TextAlignment = .leading
    /// Outline level (0 = top level).
    var level: Int = 0
    /// Left margin of the text body, in points (OOXML `marL`).
    var marginLeft: Double = 0
    /// First-line/bullet offset relative to `marginLeft`, in points (OOXML
    /// `indent`). Negative for a hanging indent (bullet left of the text).
    var indent: Double = 0
    /// The paragraph's bullet, or nil when unbulleted.
    var bullet: Bullet?
    var spaceBefore: Double = 0   // points
    var spaceAfter: Double = 0    // points
    /// Line spacing multiplier (OOXML `lnSpc/spcPct`; 1.0 = single).
    var lineSpacing: Double = 1.0
}

/// A paragraph bullet, rendered as `glyph` in `fontName` (e.g. a Wingdings
/// character), scaled relative to the first run's size.
struct Bullet {
    var glyph: String
    var fontName: String?
    var color: Color?
    var sizePercent: Double = 1.0
    /// Auto-number scheme (`buAutoNum@type`), nil for character bullets. The
    /// resolved number text is written into `glyph` during paragraph parsing.
    var autoNumFormat: String?
    var startAt: Int = 1
}

struct TextRun {
    var text: String
    var fontSize: Double = 18          // points
    var bold: Bool = false
    var italic: Bool = false
    var underline: Bool = false
    var strikethrough: Bool = false
    /// Text color. Defaults to black — never an appearance-dependent color:
    /// slide content must not invert in dark mode (white-on-white bug).
    var color: Color = .black
    var fontName: String?
    /// Whether the run carries an outer shadow effect (common on lyric decks).
    var shadow: Bool = false
}

struct PictureElement: Identifiable {
    let id = UUID()
    /// Drawing id (`cNvPr@id`) used by animation timing to target this shape.
    var shapeID: String?
    var frame: CGRect
    var imageURL: URL
    /// Crop insets as fractions of the source image (OOXML `srcRect`), in the
    /// order left, top, right, bottom. Zero means no crop.
    var crop: EdgeInsetsFraction = .zero
}

/// Fractional crop insets (0…1) from each edge of a source image.
struct EdgeInsetsFraction {
    var left: Double = 0
    var top: Double = 0
    var right: Double = 0
    var bottom: Double = 0
    static let zero = EdgeInsetsFraction()
    var isZero: Bool { left == 0 && top == 0 && right == 0 && bottom == 0 }
}

/// A geometric shape with an optional fill and border.
struct ShapeElement: Identifiable {
    let id = UUID()
    /// Drawing id (`cNvPr@id`) used by animation timing to target this shape.
    var shapeID: String?
    var frame: CGRect
    var fill: Fill?
    var stroke: StrokeInfo?
    var geometry: ShapeGeometry
    /// Some shapes carry text; kept alongside the geometry.
    var text: TextBox?
    var rotation: Double = 0   // degrees, clockwise
    var flipH: Bool = false
    var flipV: Bool = false
}

enum ShapeGeometry {
    case rectangle, roundedRectangle, ellipse
    case triangle, rightTriangle, diamond, parallelogram, trapezoid
    case pentagon, hexagon, chevron, homePlate
    case arrowRight, arrowLeft, arrowUp, arrowDown
    case star5
    /// A straight line / connector: drawn corner-to-corner of its frame.
    case line
    case other   // rendered as a rectangle fallback
}

/// A shape or background fill.
enum Fill {
    case solid(Color)
    /// Linear gradient; `angle` in degrees, OOXML convention (0° = left→right,
    /// clockwise positive).
    case gradient(stops: [GradientStop], angle: Double)
    case image(URL)
}

struct GradientStop {
    var position: Double   // 0…1
    var color: Color
}

struct StrokeInfo {
    var color: Color
    var width: Double
}
