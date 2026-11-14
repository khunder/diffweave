using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DiffWeave.App.Services;

/// <summary>
/// The application icon, drawn with vector primitives (same artwork as packaging/diffweave.svg): two document panes
/// joined by woven ribbons on a blue-teal tile. Rendered at runtime for the window icon and by a test into
/// packaging/diffweave.png.
/// </summary>
public static class AppIcon
{
    /// <summary>Draws the icon into a square of <paramref name="size"/> pixels.</summary>
    public static void Draw(DrawingContext dc, double size)
    {
        double s = size / 256.0;
        using DrawingContext.PushedState scale = dc.PushTransform(Matrix.CreateScale(s, s));
        LinearGradientBrush bg = new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#1E3A8A"), 0), new GradientStop(Color.Parse("#0F766E"), 1) },
        };
        dc.DrawRectangle(bg, null, new Rect(8, 8, 240, 240), 52, 52);
        IBrush paper = new SolidColorBrush(Colors.White, 0.95);
        IBrush line = new SolidColorBrush(Color.Parse("#94A3B8"));
        dc.DrawRectangle(paper, null, new Rect(40, 52, 64, 152), 10, 10);
        dc.DrawRectangle(paper, null, new Rect(152, 52, 64, 152), 10, 10);
        void Bar(double x, double y, double w, double h, IBrush b) => dc.DrawRectangle(b, null, new Rect(x, y, w, h), 4, 4);
        Bar(52, 70, 40, 8, line);
        Bar(52, 90, 30, 8, line);
        Bar(52, 110, 40, 14, new SolidColorBrush(Color.Parse("#F59E0B")));
        Bar(52, 136, 34, 8, line);
        Bar(52, 156, 40, 8, line);
        Bar(164, 70, 40, 8, line);
        Bar(164, 90, 30, 8, line);
        Bar(164, 130, 40, 14, new SolidColorBrush(Color.Parse("#22C55E")));
        Bar(164, 156, 40, 8, line);

        StreamGeometry band = new();
        using (StreamGeometryContext c = band.Open())
        {
            c.BeginFigure(new Point(104, 110), true);
            c.CubicBezierTo(new Point(128, 110), new Point(128, 130), new Point(152, 130));
            c.LineTo(new Point(152, 144));
            c.CubicBezierTo(new Point(128, 144), new Point(128, 124), new Point(104, 124));
            c.EndFigure(true);
        }

        dc.DrawGeometry(new SolidColorBrush(Color.Parse("#FDE68A")), null, band);
        Ribbon(dc, new Point(104, 86), new Point(130, 86), new Point(126, 170), new Point(152, 170), "#A78BFA");
        Ribbon(dc, new Point(104, 170), new Point(130, 170), new Point(126, 86), new Point(152, 86), "#5EEAD4");
    }

    private static void Ribbon(DrawingContext dc, Point a, Point c1, Point c2, Point b, string color)
    {
        StreamGeometry g = new();
        using (StreamGeometryContext c = g.Open())
        {
            c.BeginFigure(a, false);
            c.CubicBezierTo(c1, c2, b);
            c.EndFigure(false);
        }

        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse(color)), 7, lineCap: PenLineCap.Round), g);
    }

    /// <summary>Renders the icon to a bitmap.</summary>
    public static RenderTargetBitmap Render(int size)
    {
        RenderTargetBitmap bmp = new(new PixelSize(size, size));
        using (DrawingContext dc = bmp.CreateDrawingContext())
        {
            Draw(dc, size);
        }

        return bmp;
    }
}
