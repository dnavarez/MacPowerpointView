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
        // Shrink-to-fit is applied by scaling the FONT SIZES, not by wrapping the
        // text in a render transform.
        //
        // The previous approach arranged the text wider than its box and scaled
        // it back with a ScaleTransform. Render transforms don't participate in
        // layout, and an offscreen RenderTargetBitmap can draw them differently
        // from on-screen rendering — so the text was drawn at its oversized
        // arranged width and spilled past the slide edge, which cropped
        // thumbnails. Scaling the font sizes is pure layout and therefore
        // renders identically everywhere.
        var shrink = ShrinkFactor(box, frame, scale, hiddenParas);
        return BuildTextBody(box, scale, shrink, hiddenParas);
    }

    /// <summary>Measures the text and returns how much the font sizes must shrink
    /// for it to fit the box, emulating PowerPoint autofit and absorbing
    /// font-substitution metric drift.</summary>
    private static double ShrinkFactor(TextFrame box, Rect frame, double scale, HashSet<int>? hiddenParas)
    {
        if (!box.AutoShrink || frame.Height <= 0 || frame.Width <= 0) return 1.0;

        var availableWidth = Math.Max(1, (frame.Width - box.Insets.Leading - box.Insets.Trailing) * scale);
        var targetHeight = Math.Max(1, (frame.Height - box.Insets.Top - box.Insets.Bottom) * scale);

        var shrink = 1.0;
        // Shrinking changes wrapping, which changes height; a second pass settles it.
        for (int pass = 0; pass < 2; pass++)
        {
            var probe = BuildTextBody(box, scale, shrink, hiddenParas);
            probe.Measure(new Size(availableWidth, double.PositiveInfinity));
            var natural = probe.DesiredSize.Height;
            if (natural <= targetHeight || natural <= 0) break;
            shrink = Math.Max(0.35, shrink * (targetHeight / natural));
        }
        return shrink;
    }

    private static Control BuildTextBody(TextFrame box, double scale, double shrink, HashSet<int>? hiddenParas)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };

        for (int i = 0; i < box.Paragraphs.Count; i++)
        {
            var para = box.Paragraphs[i];
            if (hiddenParas?.Contains(i) == true) continue;
            stack.Children.Add(ParagraphControl(para, scale, shrink));
        }

        return new Border
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
    }

    private static Control ParagraphControl(Paragraph para, double scale, double shrink)
    {
        // `shrink` is the autofit factor; it scales text and the spacing that
        // goes with it, so the paragraph fits by layout rather than by transform.
        var effective = scale * shrink;
        var baseSize = para.Runs.FirstOrDefault()?.FontSize ?? 18;
        var leftEdge = Math.Max(0, para.MarginLeft + para.Indent);
        var hang = para.Bullet != null
            ? Math.Max(-para.Indent, baseSize * 0.95)
            : Math.Max(0, -para.Indent);

        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = para.LineSpacing > 1.01 ? baseSize * effective * para.LineSpacing : double.NaN,
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
                FontSize = Math.Max(1, run.FontSize * effective),
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
                BlurRadius = 3 * effective, OffsetX = 0, OffsetY = 1.5 * effective,
                Color = Colors.Black, Opacity = 0.5
            };

        if (para.Bullet == null || !para.HasVisibleText)
        {
            return new Border
            {
                Margin = new Thickness(leftEdge * effective, para.SpaceBefore * effective, 0, para.SpaceAfter * effective),
                Child = text
            };
        }

        var bulletBlock = new TextBlock
        {
            Text = para.Bullet.Glyph,
            FontSize = Math.Max(1, baseSize * para.Bullet.SizePercent * effective),
            FontFamily = FontResolver.Resolve(para.Bullet.FontName),
            Foreground = new SolidColorBrush(para.Bullet.Color ?? Colors.Black),
            Width = hang * effective,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bulletBlock, Dock.Left);
        row.Children.Add(bulletBlock);
        row.Children.Add(text);

        return new Border
        {
            Margin = new Thickness(leftEdge * effective, para.SpaceBefore * effective, 0, para.SpaceAfter * effective),
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

    /// <summary>Rasterises a slide to a bitmap.
    ///
    /// Thumbnails use this instead of live control trees: a sidebar holding one
    /// full visual tree per slide (each retaining decoded background images)
    /// makes selection and scrolling visibly stutter on large decks.
    ///
    /// <paramref name="scaling"/> must be the target surface's render scaling
    /// (1.0, 1.5, 2.0…). Rasterising at a fixed 96 DPI and then displaying the
    /// result at logical size on a HiDPI screen makes Windows upscale it, which
    /// is exactly what makes thumbnails look blurry. Rendering at
    /// width x scaling device pixels with a matching DPI keeps them 1:1 and
    /// sharp.</summary>
    public static Bitmap RenderToBitmap(Slide slide, Size slideSize, double width, double scaling = 1.0)
    {
        scaling = Math.Clamp(scaling <= 0 ? 1.0 : scaling, 1.0, 4.0);
        var height = Math.Max(1, width * slideSize.Height / slideSize.Width);

        // Lay the slide out at logical size; the DPI on the target does the
        // upscaling, so text and vectors are rendered at full device resolution
        // rather than being magnified afterwards.
        var control = Render(slide, slideSize, width, height);
        var size = new Size(width, height);
        control.Measure(size);
        control.Arrange(new Rect(size));

        var target = new RenderTargetBitmap(
            new PixelSize(Math.Max(1, (int)Math.Round(width * scaling)),
                          Math.Max(1, (int)Math.Round(height * scaling))),
            new Vector(96 * scaling, 96 * scaling));
        target.Render(control);
        return target;
    }

    public static void ClearCaches() => BitmapCache.Clear();
}


