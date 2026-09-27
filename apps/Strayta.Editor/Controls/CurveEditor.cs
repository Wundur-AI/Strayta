using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Strayta.Core;
using Strayta.Rendering;

namespace Strayta.Editor.Controls;

/// <summary>Draws a 256-bin histogram as filled bars, scaled so a single spike (pure black or white) does not flatten the rest.</summary>
public class HistogramView : Control
{
    public static readonly StyledProperty<int[]?> ValuesProperty = AvaloniaProperty.Register<HistogramView, int[]?>(nameof(Values));

    static HistogramView() => AffectsRender<HistogramView>(ValuesProperty);

    public int[]? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    public override void Render(DrawingContext context) => DrawHistogram(context, this, Values, new Rect(Bounds.Size));

    internal static void DrawHistogram(DrawingContext context, Control owner, int[]? bins, Rect area)
    {
        if (bins is not { Length: 256 } || area.Width <= 0) return;
        // Scale to the second-highest bin: one clipped spike should not hide the shape of everything else.
        int top = bins.OrderByDescending(v => v).Skip(1).FirstOrDefault();
        if (top <= 0) top = Math.Max(1, bins.Max());
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(area.Left, area.Bottom), true);
            for (int i = 0; i < 256; i++)
            {
                double h = Math.Min(1.0, bins[i] / (double)top) * area.Height;
                g.LineTo(new Point(area.Left + (i + 0.5) / 256 * area.Width, area.Bottom - h));
            }
            g.LineTo(new Point(area.Right, area.Bottom));
            g.EndFigure(true);
        }
        context.DrawGeometry(Brush(owner, "St.TextMuted", Colors.Gray, 0.45), null, geometry);
    }

    internal static IBrush Brush(Control owner, string key, Color fallback, double opacity = 1)
    {
        var color = owner.TryFindResource(key, owner.ActualThemeVariant, out var r) && r is ISolidColorBrush b ? b.Color : fallback;
        return new SolidColorBrush(color, opacity);
    }
}

/// <summary>
/// Photoshop-style curve graph: click to add a point, drag points to shape the curve, drag a point off the graph
/// to remove it. The curve is drawn with the renderer's own spline, so it shows exactly what will be applied.
/// </summary>
public sealed class CurveEditor : Control
{
    private const double Pad = 6, HitRadius = 7, RemoveDistance = 24;
    private const int MaxPoints = 19; // Photoshop's limit

    public static readonly StyledProperty<IReadOnlyList<CurvePoint>?> PointsProperty =
        AvaloniaProperty.Register<CurveEditor, IReadOnlyList<CurvePoint>?>(nameof(Points), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<int[]?> HistogramProperty = AvaloniaProperty.Register<CurveEditor, int[]?>(nameof(Histogram));

    private List<CurvePoint>? _editing;
    private int _drag = -1;

    static CurveEditor() => AffectsRender<CurveEditor>(PointsProperty, HistogramProperty);

    public CurveEditor()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public IReadOnlyList<CurvePoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public int[]? Histogram { get => GetValue(HistogramProperty); set => SetValue(HistogramProperty, value); }


    private Rect Graph => new Rect(Bounds.Size).Deflate(Pad);

    private Point ToScreen(double input, double output)
    {
        var g = Graph;
        return new Point(g.Left + input / 255 * g.Width, g.Bottom - output / 255 * g.Height);
    }

    private (double Input, double Output) FromScreen(Point p)
    {
        var g = Graph;
        return ((p.X - g.Left) / g.Width * 255, (g.Bottom - p.Y) / g.Height * 255);
    }

    public override void Render(DrawingContext context)
    {
        var g = Graph;
        if (g.Width <= 0 || g.Height <= 0) return;
        context.FillRectangle(HistogramView.Brush(this, "St.Canvas", Colors.Black), g);
        HistogramView.DrawHistogram(context, this, Histogram, g);

        var line = new Pen(HistogramView.Brush(this, "St.Border", Colors.Gray), 1);
        for (int i = 1; i < 4; i++)
        {
            double x = g.Left + g.Width * i / 4, y = g.Top + g.Height * i / 4;
            context.DrawLine(line, new Point(x, g.Top), new Point(x, g.Bottom));
            context.DrawLine(line, new Point(g.Left, y), new Point(g.Right, y));
        }
        context.DrawLine(line, ToScreen(0, 0), ToScreen(255, 255));
        context.DrawRectangle(line, g);

        var points = _editing ?? Points?.ToList();
        if (points is not { Count: >= 2 }) return;
        var curve = ToneCurves.Curve(points);
        var geometry = new StreamGeometry();
        using (var s = geometry.Open())
        {
            s.BeginFigure(ToScreen(0, curve(0) * 255), false);
            for (int i = 1; i <= 128; i++)
            {
                float v = i / 128f;
                s.LineTo(ToScreen(v * 255, Math.Clamp(curve(v), 0f, 1f) * 255));
            }
        }
        var text = HistogramView.Brush(this, "St.Text", Colors.White);
        context.DrawGeometry(null, new Pen(text, 1.5), geometry);
        for (int i = 0; i < points.Count; i++)
        {
            var c = ToScreen(points[i].Input, points[i].Output);
            var box = new Rect(c.X - 3.5, c.Y - 3.5, 7, 7);
            if (i == _drag) context.FillRectangle(text, box);
            else context.DrawRectangle(HistogramView.Brush(this, "St.Canvas", Colors.Black), new Pen(text, 1.2), box);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Points is not { Count: >= 2 } current) return;
        var points = current.OrderBy(p => p.Input).ToList();
        var pos = e.GetPosition(this);

        // Grab the nearest point, or add one where the person clicked (Photoshop adds on the click position).
        int nearest = -1;
        double best = HitRadius;
        for (int i = 0; i < points.Count; i++)
        {
            double d = Distance(pos, ToScreen(points[i].Input, points[i].Output));
            if (d <= best) (best, nearest) = (d, i);
        }
        if (nearest < 0)
        {
            var (input, output) = FromScreen(pos);
            int x = (int)Math.Round(Math.Clamp(input, 0, 255)), y = (int)Math.Round(Math.Clamp(output, 0, 255));
            if (points.Count >= MaxPoints || points.Any(p => Math.Abs(p.Input - x) < 2)) return;
            points.Add(new CurvePoint(x, y));
            points.Sort((a, b) => a.Input.CompareTo(b.Input));
            nearest = points.FindIndex(p => p.Input == x);
            Emit(points);
        }
        _editing = points;
        _drag = nearest;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag < 0 || _editing is not { } points) return;
        var pos = e.GetPosition(this);
        var g = Graph;
        bool interior = _drag > 0 && _drag < points.Count - 1;

        // Dragging an inner point well outside the graph removes it, as in Photoshop.
        if (interior && points.Count > 2 && (pos.X < g.Left - RemoveDistance || pos.X > g.Right + RemoveDistance
                                              || pos.Y < g.Top - RemoveDistance || pos.Y > g.Bottom + RemoveDistance))
        {
            points.RemoveAt(_drag);
            _drag = -1;
            Emit(points);
            InvalidateVisual();
            return;
        }

        // Points keep their order: an inner point stays between its neighbours.
        var (input, output) = FromScreen(pos);
        int lo = _drag > 0 ? points[_drag - 1].Input + 1 : 0;
        int hi = _drag < points.Count - 1 ? points[_drag + 1].Input - 1 : 255;
        var moved = new CurvePoint((int)Math.Round(Math.Clamp(input, lo, hi)), (int)Math.Round(Math.Clamp(output, 0, 255)));
        if (moved == points[_drag]) return;
        points[_drag] = moved;
        Emit(points);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = -1;
        _editing = null;
        InvalidateVisual();
    }

    private void EndDrag(IPointer pointer)
    {
        _drag = -1;
        _editing = null;
        if (ReferenceEquals(pointer.Captured, this)) pointer.Capture(null);
        InvalidateVisual();
    }

    /// <summary>Publishes an edit through the (two-way) <see cref="Points"/> binding; the drag keeps its own copy.</summary>
    private void Emit(List<CurvePoint> points) => SetCurrentValue(PointsProperty, points.ToArray());

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
