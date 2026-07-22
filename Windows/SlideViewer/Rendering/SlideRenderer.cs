using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Documents;
using TextElement = SlideViewer.Models.TextElement;
using Avalonia.Media.Imaging;
using SlideViewer.Models;
using Path = Avalonia.Controls.Shapes.Path;

namespace SlideViewer.Rendering;

/// <summary>
/// Builds an Avalonia visual tree for a slide.
///
/// Geometry and font sizes (slide points) are multiplied by the fit scale and
/// laid out at final size — never rendered at native size and transformed —
/// so text stays crisp at thumbnail and full-screen sizes alike.
/// </summary>
public static class SlideRenderer
{
    private static readonly Dictionary<string, Bitmap?> BitmapCache = new();

    /// <summary>Renders a slide into a fixed-size canvas.</summary>
    public static Control Render(Slide slide, Size slideSize, double width, double height,
        ISet<string>? hiddenShapes = null, IDictionary<string, HashSet<int>>? hiddenParagraphs = null)
    {
        var scale = Math.Min(width / Math.Max(1, slideSize.Width),
                             height / Math.Max(1, slideSize.Height));
        var w = slideSize.Width * scale;
        var h = slideSize.Height * scale;

        var canvas = new Canvas { Width = w, Height = h, ClipToBounds = true };
        canvas.Children.Add(BackgroundControl(slide.Background, w, h));

        foreach (var element in slide.Elements)
        {
            var hidden = element.ShapeId != null && hiddenShapes?.Contains(element.ShapeId) == true;
            if (hidden) continue;

            HashSet<int>? hiddenParas = null;
            if (element.ShapeId != null)
                hiddenParagraphs?.TryGetValue(element.ShapeId, out hiddenParas);

            var control = ElementControl(element, scale, hiddenParas);
            if (control == null) continue;

            control.Width = Math.Max(1, element.Frame.Width * scale);
            control.Height = Math.Max(1, element.Frame.Height * scale);
            Canvas.SetLeft(control, element.Frame.X * scale);
            Canvas.SetTop(control, element.Frame.Y * scale);
            canvas.Children.Add(control);
        }

        // Centre the slide inside the requested area.
        return new Border
        {
            Width = width, Height = height,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                Width = w, Height = h,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = canvas
            }
        };
    }

    private static Control BackgroundControl(SlideBackground? background, double w, double h)
    {
        var rect = new Rectangle { Width = w, Height = h };
        switch (background)
        {
            case SlideBackground.Solid s:
                rect.Fill = new SolidColorBrush(s.Color);
                break;
            case SlideBackground.Gradient g:
                rect.Fill = GradientBrush(g.Stops, g.Angle);
                break;
            case SlideBackground.Picture p:
                var bmp = LoadBitmap(p.Path);
                if (bmp != null)
                    return new Image
                    {
                        Source = bmp, Width = w, Height = h,
                        Stretch = Stretch.UniformToFill
                    };
                rect.Fill = Brushes.White;
                break;
            default:
                rect.Fill = Brushes.White;
                break;
        }
        return rect;
    }

    private static Control? ElementControl(SlideElement element, double scale, HashSet<int>? hiddenParas)
        => element switch
        {
            TextElement t => TextBoxControl(t.Box, t.Frame, scale, hiddenParas),
            PictureElement p => PictureControl(p, scale),
            ShapeElement s => ShapeControl(s, scale, hiddenParas),
            TableElement tb => TableControl(tb, scale),
            _ => null
        };

    // ── Pictures ────────────────────────────────────────────────────────────

    private static Control PictureControl(PictureElement picture, double scale)
    {
        var bmp = LoadBitmap(picture.ImagePath);
        if (bmp == null)
            return new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)) };

        var image = new Image { Source = bmp, Stretch = Stretch.Fill };
        if (picture.Crop.IsZero) return image;

        // srcRect crop: show only the retained region of the source.
        var pw = bmp.PixelSize.Width;
        var ph = bmp.PixelSize.Height;
        var src = new Rect(
            picture.Crop.Left * pw, picture.Crop.Top * ph,
            Math.Max(1, (1 - picture.Crop.Left - picture.Crop.Right) * pw),
            Math.Max(1, (1 - picture.Crop.Top - picture.Crop.Bottom) * ph));
        return new Image
        {
            Source = new CroppedBitmap(bmp, new PixelRect(
                (int)src.X, (int)src.Y, (int)src.Width, (int)src.Height)),
            Stretch = Stretch.Fill
        };
    }

    // ── Shapes ──────────────────────────────────────────────────────────────

    private static Control ShapeControl(ShapeElement shape, double scale, HashSet<int>? hiddenParas)
    {
        var w = Math.Max(1, shape.Frame.Width * scale);
        var h = Math.Max(1, shape.Frame.Height * scale);
        var panel = new Panel();

        var strokeBrush = shape.Stroke != null ? new SolidColorBrush(shape.Stroke.Color) : null;
        var strokeWidth = (shape.Stroke?.Width ?? 0) * scale;

        if (shape.Geometry == ShapeGeometry.Line)
        {
            panel.Children.Add(new Line
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(w, h),
                Stroke = (IBrush?)strokeBrush ?? Brushes.Black,
                StrokeThickness = Math.Max(strokeWidth, 0.75 * scale)
            });
        }
        else
        {
            var geometry = ShapeGeometryFactory.Build(shape.Geometry, w, h, scale);
            var fillBrush = FillBrush(shape.Fill);

            if (shape.Fill is Fill.Picture pic && LoadBitmap(pic.Path) is { } bmp)
            {
                panel.Children.Add(new Image
                {
                    Source = bmp, Stretch = Stretch.UniformToFill,
                    Clip = geometry, Width = w, Height = h
                });
                if (strokeBrush != null)
                    panel.Children.Add(new Path
                    {
                        Data = geometry, Stroke = strokeBrush, StrokeThickness = strokeWidth
                    });
            }
            else
            {
                panel.Children.Add(new Path
                {
                    Data = geometry,
                    Fill = fillBrush,
                    Stroke = strokeBrush,
                    StrokeThickness = strokeWidth
                });
            }
        }

        if (shape.Text != null)
            panel.Children.Add(TextBoxControl(shape.Text, shape.Frame, scale, hiddenParas));

        // Flips then rotation, about the shape's centre.
        var transforms = new TransformGroup();
        if (shape.FlipH || shape.FlipV)
            transforms.Children.Add(new ScaleTransform(shape.FlipH ? -1 : 1, shape.FlipV ? -1 : 1));
        if (Math.Abs(shape.Rotation) > 0.01)
            transforms.Children.Add(new RotateTransform(shape.Rotation));
        if (transforms.Children.Count > 0)
        {
            panel.RenderTransform = transforms;
            panel.RenderTransformOrigin = RelativePoint.Center;
        }
        return panel;
    }

    private static IBrush? FillBrush(Fill? fill) => fill switch
    {
        Fill.Solid s => new SolidColorBrush(s.Color),
        Fill.Gradient g => GradientBrush(g.Stops, g.Angle),
        _ => null
    };

    /// <summary>OOXML gradient angle (degrees, 0° = left→right, clockwise) →
    /// Avalonia start/end points.</summary>
    private static IBrush GradientBrush(List<GradientStopSpec> stops, double angle)
    {
        var rad = angle * Math.PI / 180.0;
        var dx = Math.Cos(rad) / 2.0;
        var dy = Math.Sin(rad) / 2.0;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5 - dx, 0.5 - dy, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5 + dx, 0.5 + dy, RelativeUnit.Relative)
        };
        foreach (var s in stops)
            brush.GradientStops.Add(new GradientStop(s.Color, Math.Clamp(s.Position, 0, 1)));
        return brush;
    }

    // ── Tables ──────────────────────────────────────────────────────────────

    private static Control TableControl(TableElement table, double scale)
    {
        var grid = new Grid();
        foreach (var w in table.ColumnWidths)
            grid.ColumnDefinitions.Add(new ColumnDefinition(w * scale, GridUnitType.Pixel));
        foreach (var row in table.Rows)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            int gridColumn = 0;
            foreach (var cell in row.Cells)
            {
                var border = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
                    BorderThickness = new Thickness(Math.Max(0.5, 0.5 * scale)),
                    Background = cell.Fill.HasValue ? new SolidColorBrush(cell.Fill.Value) : null,
                    MinHeight = row.Height * scale,
                    Child = TextBoxControl(new TextFrame
                    {
                        Paragraphs = cell.Paragraphs,
                        AutoShrink = false,
                        Insets = cell.Insets
                    }, default, scale, null)
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, Math.Min(gridColumn, Math.Max(0, table.ColumnWidths.Count - 1)));
                Grid.SetColumnSpan(border, Math.Max(1, cell.GridSpan));
                grid.Children.Add(border);
                gridColumn += Math.Max(1, cell.GridSpan);
            }
        }
        return grid;
    }

    // ── Text ────────────────────────────────────────────────────────────────

    private static Control TextBoxControl(TextFrame box, Rect frame, double scale, HashSet<int>? hiddenParas)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };

        for (int i = 0; i < box.Paragraphs.Count; i++)
        {
            var para = box.Paragraphs[i];
            if (hiddenParas?.Contains(i) == true) continue;
            stack.Children.Add(ParagraphControl(para, scale));
        }

        var padded = new Border
        {
            Padding = new Thickness(box.Insets.Leading * scale, box.Insets.Top * scale,
                                    box.Insets.Trailing * scale, box.Insets.Bottom * scale),
            Child = stack,
            VerticalAlignment = box.VerticalAnchor switch
            {
                VerticalAnchor.Center => VerticalAlignment.Center,
                VerticalAnchor.Bottom => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Top
            },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Shrink-to-fit: emulates PowerPoint autofit and absorbs font-metric
        // drift from substitution, so text never gets clipped.
        if (box.AutoShrink && frame.Height > 0)
            return new ShrinkToFit
            {
                Child = padded,
                TargetHeight = frame.Height * scale,
                VerticalAlignment = VerticalAlignment.Stretch
            };

        return padded;
    }

    private static Control ParagraphControl(Paragraph para, double scale)
    {
        var baseSize = para.Runs.FirstOrDefault()?.FontSize ?? 18;
        var leftEdge = Math.Max(0, para.MarginLeft + para.Indent);
        var hang = para.Bullet != null
            ? Math.Max(-para.Indent, baseSize * 0.95)
            : Math.Max(0, -para.Indent);

        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = para.LineSpacing > 1.01 ? baseSize * scale * para.LineSpacing : double.NaN,
            TextAlignment = para.Alignment switch
            {
                ParagraphAlignment.Center => TextAlignment.Center,
                ParagraphAlignment.Trailing => TextAlignment.Right,
                _ => TextAlignment.Left
            },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        foreach (var run in para.Runs)
        {
            var inline = new Run(run.Text)
            {
                FontSize = Math.Max(1, run.FontSize * scale),
                FontFamily = FontResolver.Resolve(run.FontName),
                FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal,
                FontStyle = run.Italic ? FontStyle.Italic : FontStyle.Normal,
                Foreground = new SolidColorBrush(run.Color)
            };
            var decorations = new TextDecorationCollection();
            if (run.Underline) decorations.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
            if (run.Strikethrough) decorations.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
            if (decorations.Count > 0) inline.TextDecorations = decorations;
            text.Inlines!.Add(inline);
        }

        // Soft shadow for runs carrying an outerShdw effect.
        if (para.Runs.Any(r => r.Shadow))
            text.Effect = new DropShadowEffect
            {
                BlurRadius = 3 * scale, OffsetX = 0, OffsetY = 1.5 * scale,
                Color = Colors.Black, Opacity = 0.5
            };

        if (para.Bullet == null || !para.HasVisibleText)
        {
            return new Border
            {
                Margin = new Thickness(leftEdge * scale, para.SpaceBefore * scale, 0, para.SpaceAfter * scale),
                Child = text
            };
        }

        var bulletBlock = new TextBlock
        {
            Text = para.Bullet.Glyph,
            FontSize = Math.Max(1, baseSize * para.Bullet.SizePercent * scale),
            FontFamily = FontResolver.Resolve(para.Bullet.FontName),
            Foreground = new SolidColorBrush(para.Bullet.Color ?? Colors.Black),
            Width = hang * scale,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bulletBlock, Dock.Left);
        row.Children.Add(bulletBlock);
        row.Children.Add(text);

        return new Border
        {
            Margin = new Thickness(leftEdge * scale, para.SpaceBefore * scale, 0, para.SpaceAfter * scale),
            Child = row
        };
    }

    // ── Bitmaps ─────────────────────────────────────────────────────────────

    private static Bitmap? LoadBitmap(string path)
    {
        if (BitmapCache.TryGetValue(path, out var cached)) return cached;
        Bitmap? bmp = null;
        try { if (File.Exists(path)) bmp = new Bitmap(path); } catch { }
        BitmapCache[path] = bmp;
        return bmp;
    }

    public static void ClearCaches() => BitmapCache.Clear();
}

/// <summary>
/// Scales its child down uniformly when the child's natural height exceeds
/// <see cref="TargetHeight"/> — PowerPoint's "shrink text on overflow".
/// </summary>
public sealed class ShrinkToFit : Decorator
{
    public double TargetHeight { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child == null) return default;
        Child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var natural = Child.DesiredSize;
        var height = TargetHeight > 0 ? Math.Min(TargetHeight, availableSize.Height) : availableSize.Height;
        return new Size(availableSize.Width, double.IsInfinity(height) ? natural.Height : height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child == null) return finalSize;
        Child.Measure(new Size(finalSize.Width, double.PositiveInfinity));
        var natural = Child.DesiredSize.Height;
        var target = TargetHeight > 0 ? TargetHeight : finalSize.Height;

        var factor = natural > target && natural > 0 ? Math.Max(0.2, target / natural) : 1.0;
        if (factor < 1.0)
        {
            Child.RenderTransform = new ScaleTransform(factor, factor);
            Child.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
            Child.Arrange(new Rect(0, 0, finalSize.Width / factor, natural));
        }
        else
        {
            Child.RenderTransform = null;
            Child.Arrange(new Rect(0, 0, finalSize.Width, Math.Max(natural, finalSize.Height)));
        }
        return finalSize;
    }
}
