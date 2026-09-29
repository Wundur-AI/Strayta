using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Controls;

/// <summary>
/// The Gradient Editor's bar: the gradient, opacity stops above it and color stops below, as in Photoshop. Click
/// above or below the bar to add a stop, drag a stop to move it (or well away from the bar to delete it), and drag
/// the diamonds beside the selected stop to move the midpoints. Works on the <see cref="GradientEditorViewModel"/>
/// in its DataContext.
/// </summary>
public sealed class GradientBar : Control
{
    private const double Pad = 10, Row = 18, BarHeight = 30, Marker = 7, DeleteDistance = 28;

    private GradientEditorViewModel? _editor;
    private GradientStopViewModel? _dragStop, _dragMidpoint;
    private bool _removing;

    public GradientBar()
    {
        Height = Row * 2 + BarHeight;
        MinWidth = 200;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_editor is not null) _editor.GradientChanged -= InvalidateVisual;
        _editor = DataContext as GradientEditorViewModel;
        if (_editor is not null)
        {
            _editor.GradientChanged += InvalidateVisual;
            _editor.PropertyChanged += (_, _) => InvalidateVisual();
        }
        InvalidateVisual();
    }

    private double BarWidth => Math.Max(1, Bounds.Width - 2 * Pad);
    private double X(float location) => Pad + location * BarWidth;
    private float Location(double x) => (float)((x - Pad) / BarWidth);
    private static double RowCenter(bool color) => color ? Row + BarHeight + Row / 2 : Row / 2;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_editor is not { } ed) return;
        var bar = new Rect(Pad, Row, BarWidth, BarHeight);
        if (ed.BarImage is { } image) context.DrawImage(image, new Rect(image.Size), bar);
        context.DrawRectangle(null, new Pen(Brushes.Gray, 1), bar);

        var accent = this.TryFindResource("St.Accent", ActualThemeVariant, out var a) && a is IBrush ab ? ab : Brushes.DodgerBlue;
        foreach (bool color in new[] { false, true })
        {
            foreach (var stop in ed.Ordered(color))
            {
                if (_removing && ReferenceEquals(stop, _dragStop)) continue;
                bool selected = ReferenceEquals(stop, ed.SelectedStop) && !ed.MidpointSelected;
                DrawStop(context, stop, color, selected ? accent : Brushes.Gray);
            }
            // Diamonds on both sides of the selected stop.
            if (ed.SelectedStop is { } sel && sel.IsColor == color)
            {
                var ordered = ed.Ordered(color);
                int i = ordered.IndexOf(sel);
                foreach (var left in new[] { i > 0 ? ordered[i - 1] : null, sel })
                    if (left is not null && ed.Next(left) is { } right && right.Location - left.Location > 0.02f)
                    {
                        bool active = ed.MidpointSelected && ReferenceEquals(left, sel);
                        DrawDiamond(context, MidpointX(left, right), RowCenter(color), active ? accent : Brushes.Gray);
                    }
            }
        }
    }

    private double MidpointX(GradientStopViewModel left, GradientStopViewModel right) =>
        X(left.Location + (right.Location - left.Location) * left.Midpoint);

    private void DrawStop(DrawingContext ctx, GradientStopViewModel stop, bool color, IBrush outline)
    {
        double x = X(stop.Location);
        // A little house pointing at the bar, filled with the stop's color (or its opacity as gray).
        double tip = color ? Row + BarHeight : Row, dir = color ? 1 : -1;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x, tip), true);
            g.LineTo(new Point(x + Marker, tip + dir * Marker));
            g.LineTo(new Point(x + Marker, tip + dir * (Row - 2)));
            g.LineTo(new Point(x - Marker, tip + dir * (Row - 2)));
            g.LineTo(new Point(x - Marker, tip + dir * Marker));
            g.EndFigure(true);
        }
        byte gray = (byte)Math.Round((1 - stop.Opacity) * 255);
        IBrush fill = color ? new SolidColorBrush(stop.Color) : new SolidColorBrush(Color.FromRgb(gray, gray, gray));
        ctx.DrawGeometry(fill, new Pen(outline, ReferenceEquals(outline, Brushes.Gray) ? 1 : 2), geo);
    }

    private static void DrawDiamond(DrawingContext ctx, double x, double y, IBrush fill)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x, y - 4), true);
            g.LineTo(new Point(x + 4, y));
            g.LineTo(new Point(x, y + 4));
            g.LineTo(new Point(x - 4, y));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(fill, null, geo);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_editor is not { } ed || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        bool? color = p.Y < Row ? false : p.Y > Row + BarHeight ? true : null;
        if (color is not { } isColor) return;

        // Midpoint diamonds beside the selected stop.
        if (ed.SelectedStop is { } sel && sel.IsColor == isColor)
        {
            var ordered = ed.Ordered(isColor);
            int i = ordered.IndexOf(sel);
            foreach (var left in new[] { sel, i > 0 ? ordered[i - 1] : null })
                if (left is not null && ed.Next(left) is { } right && Math.Abs(MidpointX(left, right) - p.X) <= 5)
                {
                    ed.SelectedStop = left;
                    ed.MidpointSelected = true;
                    _dragMidpoint = left;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    InvalidateVisual();
                    return;
                }
        }

        var hit = ed.Ordered(isColor).OrderBy(s => Math.Abs(X(s.Location) - p.X)).FirstOrDefault(s => Math.Abs(X(s.Location) - p.X) <= Marker + 1);
        if (hit is null) hit = ed.AddStop(isColor, Math.Clamp(Location(p.X), 0f, 1f));
        ed.SelectedStop = hit;
        ed.MidpointSelected = false;
        _dragStop = hit;
        _removing = false;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_editor is not { } ed) return;
        var p = e.GetPosition(this);
        if (_dragMidpoint is { } left && ed.Next(left) is { } right)
        {
            double xa = X(left.Location), xb = X(right.Location);
            if (xb - xa > 1) ed.SetMidpoint(left, (float)((p.X - xa) / (xb - xa)));
        }
        else if (_dragStop is { } stop)
        {
            var row = stop.IsColor ? ed.ColorStops : ed.OpacityStops;
            _removing = row.Count > 2 && Math.Abs(p.Y - RowCenter(stop.IsColor)) > DeleteDistance;
            ed.MoveStop(stop, Location(p.X));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStop is { } stop && _removing) _editor?.RemoveStop(stop);
        _dragStop = null;
        _dragMidpoint = null;
        _removing = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }
}
