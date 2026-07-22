using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using SlideViewer.Models;
using SlideViewer.Rendering;

namespace SlideViewer.Views;

/// <summary>
/// Writes a diagnostic bundle to the Desktop: the actual thumbnail bitmaps plus
/// every measurement that governs how they are sized and displayed.
///
/// This exists because a thumbnail problem reported on a real Windows machine
/// could not be reproduced on the development machine. Rather than guessing,
/// the bundle shows whether a wrong image was produced (a rendering problem) or
/// a correct image was displayed wrongly (a layout problem).
/// </summary>
public static class Diagnostics
{
    public static string Write(Window window, ListBox thumbs, Presentation? presentation,
                               double renderedThumbWidth, ColumnDefinition? sidebarColumn)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "SlideViewer-Diagnostics");
        Directory.CreateDirectory(folder);

        var report = new StringBuilder();
        report.AppendLine("SlideViewer thumbnail diagnostics");
        report.AppendLine($"Time           : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Version        : 1.0.2");
        report.AppendLine($"OS             : {Environment.OSVersion}");
        report.AppendLine();

        report.AppendLine("── Display ─────────────────────────────────");
        report.AppendLine($"Window size    : {window.Width:F0} x {window.Height:F0}");
        report.AppendLine($"Window bounds  : {window.Bounds.Width:F0} x {window.Bounds.Height:F0}");
        report.AppendLine($"RenderScaling  : {window.RenderScaling:F2}");
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen != null)
        {
            report.AppendLine($"Screen bounds  : {screen.Bounds.Width} x {screen.Bounds.Height} (device px)");
            report.AppendLine($"Screen working : {screen.WorkingArea.Width} x {screen.WorkingArea.Height}");
            report.AppendLine($"Screen scaling : {screen.Scaling:F2}");
        }
        report.AppendLine();

        report.AppendLine("── Sidebar / list ──────────────────────────");
        report.AppendLine($"Sidebar column ActualWidth : {sidebarColumn?.ActualWidth ?? -1:F1}");
        report.AppendLine($"Sidebar column Width       : {sidebarColumn?.Width.ToString() ?? "n/a"}");
        report.AppendLine($"ListBox bounds             : {thumbs.Bounds.Width:F1} x {thumbs.Bounds.Height:F1}");

        var scroll = thumbs.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll != null)
        {
            report.AppendLine($"Viewport                   : {scroll.Viewport.Width:F1} x {scroll.Viewport.Height:F1}");
            report.AppendLine($"Extent                     : {scroll.Extent.Width:F1} x {scroll.Extent.Height:F1}");
            report.AppendLine($"Horizontal overflow        : {scroll.Extent.Width > scroll.Viewport.Width + 1}");
        }
        report.AppendLine($"Rasterised thumb width     : {renderedThumbWidth:F1}");
        report.AppendLine();

        report.AppendLine("── Realised rows ───────────────────────────");
        foreach (var (item, i) in thumbs.GetVisualDescendants().OfType<ListBoxItem>().Take(4).Select((c, i) => (c, i)))
        {
            var image = item.GetVisualDescendants().OfType<Image>().FirstOrDefault();
            var bitmap = image?.Source as Bitmap;
            report.AppendLine($"row {i}: item={item.Bounds.Width:F1}x{item.Bounds.Height:F1} " +
                              $"image={image?.Bounds.Width ?? -1:F1}x{image?.Bounds.Height ?? -1:F1} " +
                              $"stretch={image?.Stretch} " +
                              $"bitmapLogical={bitmap?.Size.Width ?? -1:F1}x{bitmap?.Size.Height ?? -1:F1} " +
                              $"bitmapPixels={bitmap?.PixelSize.Width ?? -1}x{bitmap?.PixelSize.Height ?? -1} " +
                              $"dpi={bitmap?.Dpi.X ?? -1:F0}");
        }
        report.AppendLine();

        // The decisive artefacts: what the bitmaps actually contain.
        if (presentation != null)
        {
            report.AppendLine("── Saved bitmaps ───────────────────────────");
            var slideIndexes = new[] { 0, 1, 2 }.Where(i => i < presentation.Slides.Count);
            foreach (var index in slideIndexes)
            {
                // Exactly what the sidebar renders.
                var asDisplayed = Path.Combine(folder, $"thumb-slide{index + 1}-asdisplayed.png");
                SaveBitmap(SlideRenderer.RenderToBitmap(presentation.Slides[index], presentation.Size,
                    renderedThumbWidth, window.RenderScaling), asDisplayed);
                report.AppendLine($"slide {index + 1}: {Path.GetFileName(asDisplayed)} " +
                                  $"(width {renderedThumbWidth:F0}, scaling {window.RenderScaling:F2})");

                // A known-good reference at scaling 1 for comparison.
                var reference = Path.Combine(folder, $"thumb-slide{index + 1}-reference.png");
                SaveBitmap(SlideRenderer.RenderToBitmap(presentation.Slides[index], presentation.Size, 300, 1.0), reference);
                report.AppendLine($"slide {index + 1}: {Path.GetFileName(reference)} (width 300, scaling 1.00)");
            }
        }

        var reportPath = Path.Combine(folder, "report.txt");
        File.WriteAllText(reportPath, report.ToString());

        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); } catch { }
        return folder;
    }

    private static void SaveBitmap(Bitmap bitmap, string path)
    {
        try
        {
            using var stream = File.Create(path);
            bitmap.Save(stream);
        }
        catch { }
    }
}
