using Avalonia;
using Avalonia.Media;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Controls;

// The Crop tool's composition overlays, drawn inside the box in screen space: Rule of Thirds, Grid, Diagonal,
// Triangle, Golden Ratio and Golden Spiral (O cycles, Shift+O turns the triangle and spiral).
public sealed partial class ImageCanvas
{
    private static readonly double Phi = (1 + Math.Sqrt(5)) / 2;

    private void DrawCropGuide(DrawingContext context, Rect rect)
    {
        void Line(double x0, double y0, double x1, double y1) => context.DrawLine(ThirdsPen, new Point(x0, y0), new Point(x1, y1));
        void Vertical(double f) => Line(rect.Left + rect.Width * f, rect.Top, rect.Left + rect.Width * f, rect.Bottom);
        void Horizontal(double f) => Line(rect.Left, rect.Top + rect.Height * f, rect.Right, rect.Top + rect.Height * f);
        int flip = CropOverlayOrientation & 3;

        switch (CropOverlay)
        {
            case CropOverlay.RuleOfThirds:
                for (int i = 1; i < 3; i++)
                {
                    Vertical(i / 3.0);
                    Horizontal(i / 3.0);
                }
                break;
            case CropOverlay.Grid:
            {
                // About every 40 screen points, whole divisions of the box.
                int nx = Math.Max(2, (int)Math.Round(rect.Width / 40)), ny = Math.Max(2, (int)Math.Round(rect.Height / 40));
                for (int i = 1; i < nx; i++) Vertical(i / (double)nx);
                for (int i = 1; i < ny; i++) Horizontal(i / (double)ny);
                break;
            }
            case CropOverlay.Diagonal:
            {
                // 45° lines from each corner, as far as the box goes.
                double d = Math.Min(rect.Width, rect.Height);
                Line(rect.Left, rect.Top, rect.Left + d, rect.Top + d);
                Line(rect.Right, rect.Top, rect.Right - d, rect.Top + d);
                Line(rect.Left, rect.Bottom, rect.Left + d, rect.Bottom - d);
                Line(rect.Right, rect.Bottom, rect.Right - d, rect.Bottom - d);
                break;
            }
            case CropOverlay.Triangle:
            {
                // One diagonal, and from the other two corners the perpendiculars to it.
                bool other = (flip & 1) != 0;
                Point a = other ? rect.TopRight : rect.TopLeft, b = other ? rect.BottomLeft : rect.BottomRight;
                Point c = other ? rect.TopLeft : rect.TopRight, e = other ? rect.BottomRight : rect.BottomLeft;
                context.DrawLine(ThirdsPen, a, b);
                context.DrawLine(ThirdsPen, c, Foot(c, a, b));
                context.DrawLine(ThirdsPen, e, Foot(e, a, b));
                break;
            }
            case CropOverlay.GoldenRatio:
            {
                double g = 1 / Phi; // 0.618
                foreach (double f in new[] { 1 - g, g })
                {
                    Vertical(f);
                    Horizontal(f);
                }
                break;
            }
            case CropOverlay.GoldenSpiral:
                DrawGoldenSpiral(context, rect, flip);
                break;
        }
    }

    /// <summary>The foot of the perpendicular from <paramref name="p"/> to the line through <paramref name="a"/> and <paramref name="b"/>.</summary>
    private static Point Foot(Point p, Point a, Point b)
    {
        var ab = b - a;
        double t = ((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / (ab.X * ab.X + ab.Y * ab.Y);
        return a + ab * t;
    }

    /// <summary>
    /// The golden spiral: a golden rectangle (φ × 1) cut into ever smaller squares, clockwise from the left, with a
    /// quarter circle in each; stretched to the box and flipped horizontally and/or vertically by <paramref name="flip"/>.
    /// </summary>
    private void DrawGoldenSpiral(DrawingContext context, Rect rect, int flip)
    {
        Point Map(double u, double v)
        {
            if ((flip & 1) != 0) u = Phi - u;
            if ((flip & 2) != 0) v = 1 - v;
            return new Point(rect.Left + u / Phi * rect.Width, rect.Top + v * rect.Height);
        }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            double x = 0, y = 0, w = Phi, h = 1;
            g.BeginFigure(Map(0, 1), false);
            for (int step = 0; step < 12 && Math.Min(w, h) * rect.Height > 0.5; step++)
            {
                double s, cx, cy, a0;
                switch (step % 4)
                {
                    case 0: s = h; cx = x + s; cy = y + s; a0 = Math.PI; x += s; w -= s; break; // square on the left
                    case 1: s = w; cx = x; cy = y + s; a0 = -Math.PI / 2; y += s; h -= s; break; // on the top
                    case 2: s = h; cx = x + w - s; cy = y; a0 = 0; w -= s; break; // on the right
                    default: s = w; cx = x + s; cy = y + h - s; a0 = Math.PI / 2; h -= s; break; // on the bottom
                }
                for (int k = 1; k <= 16; k++)
                {
                    double a = a0 + k / 16.0 * Math.PI / 2;
                    g.LineTo(Map(cx + s * Math.Cos(a), cy + s * Math.Sin(a)));
                }
            }
            g.EndFigure(false);
        }
        context.DrawGeometry(null, ThirdsPen, geometry);
        // The first two cuts, which carry the proportion.
        double first = 1; // square side on the left
        context.DrawLine(ThirdsPen, Map(first, 0), Map(first, 1));
        context.DrawLine(ThirdsPen, Map(first, Phi - 1), Map(Phi, Phi - 1));
    }
}
