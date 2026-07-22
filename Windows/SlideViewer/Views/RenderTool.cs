using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SlideViewer.Parsing;
using SlideViewer.Rendering;

namespace SlideViewer.Views;

/// <summary>
/// Offscreen slide rasterizer for verification:
/// <c>SlideViewer --render deck.pptx 4 out.png</c>
///
/// Renders through the same visual tree the app displays, so a PNG produced
/// here reflects what the window shows.
/// </summary>
public static class RenderTool
{
    /// <summary>Rasterises a thumbnail at a given scaling so the DPI-awareness of
    /// the sidebar can be verified: at 2x the PNG must contain 2x the pixels.</summary>
    public static int RunThumb(string deckPath, int slideIndex, string outPath, double width, double scaling)
    {
        using var parser = new PptxParser(deckPath);
        var pres = parser.Parse();
        if (slideIndex < 0 || slideIndex >= pres.Slides.Count) return 1;
        var bitmap = SlideRenderer.RenderToBitmap(pres.Slides[slideIndex], pres.Size, width, scaling);
        using var stream = File.Create(outPath);
        bitmap.Save(stream);
        Console.WriteLine($"THUMB scaling={scaling} requestedLogical={width:F0} " +
                          $"pixels={bitmap.PixelSize.Width}x{bitmap.PixelSize.Height} " +
                          $"reportedSize={bitmap.Size.Width:F0}x{bitmap.Size.Height:F0} " +
                          $"dpi={bitmap.Dpi.X:F0}");
        return 0;
    }

    public static int Run(string deckPath, int slideIndex, string outPath, double width = 1280)
    {
        using var parser = new PptxParser(deckPath);
        var pres = parser.Parse();
        if (slideIndex < 0 || slideIndex >= pres.Slides.Count)
        {
            Console.Error.WriteLine($"Slide {slideIndex + 1} out of range (1..{pres.Slides.Count})");
            return 1;
        }

        var height = width * pres.Size.Height / pres.Size.Width;
        var control = SlideRenderer.Render(pres.Slides[slideIndex], pres.Size, width, height);

        var root = new Border
        {
            Width = width,
            Height = height,
            Background = Avalonia.Media.Brushes.White,
            Child = control
        };
        var size = new Size(width, height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        using var bitmap = new RenderTargetBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96));
        bitmap.Render(root);
        using var stream = File.Create(outPath);
        bitmap.Save(stream);

        Console.WriteLine($"Rendered slide {slideIndex + 1} of {pres.Slides.Count} -> {outPath}");
        return 0;
    }
}
