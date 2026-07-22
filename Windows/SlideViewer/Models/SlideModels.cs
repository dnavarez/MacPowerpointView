using Avalonia;
using Avalonia.Media;

namespace SlideViewer.Models;

/// <summary>OOXML English Metric Units → points (1/72").</summary>
public static class Emu
{
    public const double PerPoint = 12700.0;
    public static double ToPoints(double emu) => emu / PerPoint;
}

public sealed class Presentation
{
    public Size Size { get; set; } = new(960, 540);
    public List<Slide> Slides { get; set; } = new();
}

public sealed class Slide
{
    public int Index { get; set; }
    public SlideBackground? Background { get; set; }
    public List<SlideElement> Elements { get; set; } = new();
    /// <summary>Click-triggered animation steps, in order.</summary>
    public List<BuildStep> BuildSteps { get; set; } = new();
    public bool HasTransition { get; set; }
}

/// <summary>One click of animation: shapes revealed/hidden, by drawing id.
/// Text animated "by paragraph" reveals paragraph indices instead.</summary>
public sealed class BuildStep
{
    public HashSet<string> Reveals { get; } = new();
    public HashSet<string> Hides { get; } = new();
    public Dictionary<string, HashSet<int>> ParagraphReveals { get; } = new();
    public bool IsEmpty => Reveals.Count == 0 && Hides.Count == 0 && ParagraphReveals.Count == 0;
}

public abstract class SlideBackground
{
    public sealed class Solid : SlideBackground { public Color Color { get; init; } }
    public sealed class Gradient : SlideBackground
    {
        public List<GradientStopSpec> Stops { get; init; } = new();
        public double Angle { get; init; }
    }
    public sealed class Picture : SlideBackground { public string Path { get; init; } = ""; }
}

public readonly record struct GradientStopSpec(double Position, Color Color);

public abstract class Fill
{
    public sealed class Solid : Fill { public Color Color { get; init; } }
    public sealed class Gradient : Fill
    {
        public List<GradientStopSpec> Stops { get; init; } = new();
        public double Angle { get; init; }
    }
    public sealed class Picture : Fill { public string Path { get; init; } = ""; }
}

public sealed class StrokeInfo
{
    public Color Color { get; set; }
    public double Width { get; set; } = 1;
}

/// <summary>Base for anything positioned on a slide.</summary>
public abstract class SlideElement
{
    /// <summary>Drawing id (cNvPr@id) targeted by animation timing.</summary>
    public string? ShapeId { get; set; }
    public Rect Frame { get; set; }
}

public sealed class TextElement : SlideElement
{
    public TextFrame Box { get; set; } = new();
}

public sealed class PictureElement : SlideElement
{
    public string ImagePath { get; set; } = "";
    /// <summary>srcRect crop as fractions of the source image.</summary>
    public CropInsets Crop { get; set; } = CropInsets.Zero;
}

public readonly record struct CropInsets(double Left, double Top, double Right, double Bottom)
{
    public static readonly CropInsets Zero = new(0, 0, 0, 0);
    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

public sealed class ShapeElement : SlideElement
{
    public Fill? Fill { get; set; }
    public StrokeInfo? Stroke { get; set; }
    public ShapeGeometry Geometry { get; set; } = ShapeGeometry.Rectangle;
    public TextFrame? Text { get; set; }
    public double Rotation { get; set; }
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }
}

public enum ShapeGeometry
{
    Rectangle, RoundedRectangle, Ellipse,
    Triangle, RightTriangle, Diamond, Parallelogram, Trapezoid,
    Pentagon, Hexagon, Chevron, HomePlate,
    ArrowRight, ArrowLeft, ArrowUp, ArrowDown,
    Star5, Line, Other
}

public sealed class TableElement : SlideElement
{
    public List<double> ColumnWidths { get; set; } = new();
    public List<TableRow> Rows { get; set; } = new();
}

public sealed class TableRow
{
    public double Height { get; set; }
    public List<TableCell> Cells { get; set; } = new();
}

public sealed class TableCell
{
    public List<Paragraph> Paragraphs { get; set; } = new();
    public Color? Fill { get; set; }
    public int GridSpan { get; set; } = 1;
    public TextInsets Insets { get; set; } = TextInsets.Default;
}

public sealed class TextFrame
{
    public List<Paragraph> Paragraphs { get; set; } = new();
    public VerticalAnchor VerticalAnchor { get; set; } = VerticalAnchor.Top;
    /// <summary>Shrink text to fit the box on overflow (off for table cells).</summary>
    public bool AutoShrink { get; set; } = true;
    public TextInsets Insets { get; set; } = TextInsets.Default;
}

public enum VerticalAnchor { Top, Center, Bottom }

/// <summary>bodyPr lIns/tIns/rIns/bIns (or tcPr marL/…) in points.</summary>
public readonly record struct TextInsets(double Leading, double Top, double Trailing, double Bottom)
{
    public static readonly TextInsets Default = new(7.2, 3.6, 7.2, 3.6);
}

public enum ParagraphAlignment { Leading, Center, Trailing }

public sealed class Paragraph
{
    public List<TextRun> Runs { get; set; } = new();
    public ParagraphAlignment Alignment { get; set; } = ParagraphAlignment.Leading;
    public int Level { get; set; }
    /// <summary>Text-body left margin in points (marL).</summary>
    public double MarginLeft { get; set; }
    /// <summary>First-line offset relative to MarginLeft; negative = hanging.</summary>
    public double Indent { get; set; }
    public Bullet? Bullet { get; set; }
    public double SpaceBefore { get; set; }
    public double SpaceAfter { get; set; }
    public double LineSpacing { get; set; } = 1.0;

    public bool HasVisibleText =>
        Runs.Any(r => !string.IsNullOrWhiteSpace(r.Text));
}

public sealed class Bullet
{
    public string Glyph { get; set; } = "";
    public string? FontName { get; set; }
    public Color? Color { get; set; }
    public double SizePercent { get; set; } = 1.0;
    /// <summary>buAutoNum@type; null for character bullets.</summary>
    public string? AutoNumFormat { get; set; }
    public int StartAt { get; set; } = 1;
}

public sealed class TextRun
{
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 18;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }
    /// <summary>Defaults to black — never an appearance-dependent color, or
    /// dark mode renders slide text white-on-white.</summary>
    public Color Color { get; set; } = Colors.Black;
    public string? FontName { get; set; }
    public bool Shadow { get; set; }
}
