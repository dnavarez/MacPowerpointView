using Avalonia;
using Avalonia.Media;
using SlideViewer.Models;

namespace SlideViewer.Rendering;

/// <summary>Builds the drawing geometry for each preset autoshape.</summary>
public static class ShapeGeometryFactory
{
    public static Geometry Build(ShapeGeometry kind, double w, double h, double scale) => kind switch
    {
        ShapeGeometry.Ellipse => new EllipseGeometry(new Rect(0, 0, w, h)),
        ShapeGeometry.RoundedRectangle => RoundedRect(w, h),
        ShapeGeometry.Triangle => Polygon(w, h, (0.5, 0), (1, 1), (0, 1)),
        ShapeGeometry.RightTriangle => Polygon(w, h, (0, 0), (0, 1), (1, 1)),
        ShapeGeometry.Diamond => Polygon(w, h, (0.5, 0), (1, 0.5), (0.5, 1), (0, 0.5)),
        ShapeGeometry.Parallelogram => Polygon(w, h, (0.25, 0), (1, 0), (0.75, 1), (0, 1)),
        ShapeGeometry.Trapezoid => Polygon(w, h, (0.25, 0), (0.75, 0), (1, 1), (0, 1)),
        ShapeGeometry.Pentagon => Polygon(w, h, (0.5, 0), (1, 0.38), (0.81, 1), (0.19, 1), (0, 0.38)),
        ShapeGeometry.Hexagon => Polygon(w, h, (0.25, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0.25, 1), (0, 0.5)),
        ShapeGeometry.Chevron => Polygon(w, h, (0, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0, 1), (0.25, 0.5)),
        ShapeGeometry.HomePlate => Polygon(w, h, (0, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0, 1)),
        ShapeGeometry.ArrowRight => Polygon(w, h, (0, 0.3), (0.6, 0.3), (0.6, 0), (1, 0.5), (0.6, 1), (0.6, 0.7), (0, 0.7)),
        ShapeGeometry.ArrowLeft => Polygon(w, h, (1, 0.3), (0.4, 0.3), (0.4, 0), (0, 0.5), (0.4, 1), (0.4, 0.7), (1, 0.7)),
        ShapeGeometry.ArrowUp => Polygon(w, h, (0.3, 1), (0.3, 0.4), (0, 0.4), (0.5, 0), (1, 0.4), (0.7, 0.4), (0.7, 1)),
        ShapeGeometry.ArrowDown => Polygon(w, h, (0.3, 0), (0.3, 0.6), (0, 0.6), (0.5, 1), (1, 0.6), (0.7, 0.6), (0.7, 0)),
        ShapeGeometry.Star5 => Star(w, h),
        _ => new RectangleGeometry(new Rect(0, 0, w, h))
    };

    private static Geometry RoundedRect(double w, double h)
    {
        var radius = Math.Min(w, h) * 0.12;
        return new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
    }

    private static Geometry Polygon(double w, double h, params (double x, double y)[] points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(points[0].x * w, points[0].y * h), isFilled: true);
            for (int i = 1; i < points.Length; i++)
                ctx.LineTo(new Point(points[i].x * w, points[i].y * h));
            ctx.EndFigure(true);
        }
        return geometry;
    }

    private static Geometry Star(double w, double h)
    {
        var cx = w / 2; var cy = h / 2;
        var outer = Math.Min(w, h) / 2;
        var inner = outer * 0.382;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < 10; i++)
            {
                var angle = (i * 36.0 - 90.0) * Math.PI / 180.0;
                var r = i % 2 == 0 ? outer : inner;
                var pt = new Point(cx + Math.Cos(angle) * r, cy + Math.Sin(angle) * r);
                if (i == 0) ctx.BeginFigure(pt, isFilled: true);
                else ctx.LineTo(pt);
            }
            ctx.EndFigure(true);
        }
        return geometry;
    }
}
