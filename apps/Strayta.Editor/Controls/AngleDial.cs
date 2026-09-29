using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Strayta.Editor.Controls;

/// <summary>
/// Photoshop's angle dial: a circle with a line from its center pointing at <see cref="Angle"/> (degrees
/// counterclockwise from the right, -180..180); click or drag to turn it, Shift snaps to 15°. With
/// <see cref="ShowAltitude"/> it becomes Bevel &amp; Emboss's light globe: a crosshair whose distance from the center is
/// the light's <see cref="Altitude"/> (90° in the middle, 0° at the rim), and a drag sets both.
/// </summary>
public sealed class AngleDial : Control
{
    public static readonly StyledProperty<double> AngleProperty =
        AvaloniaProperty.Register<AngleDial, double>(nameof(Angle), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> AltitudeProperty =
        AvaloniaProperty.Register<AngleDial, double>(nameof(Altitude), 30, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> ShowAltitudeProperty = AvaloniaProperty.Register<AngleDial, bool>(nameof(ShowAltitude));

    private bool _dragging;

    static AngleDial() => AffectsRender<AngleDial>(AngleProperty, AltitudeProperty, ShowAltitudeProperty);

    public AngleDial()
    {
        Width = Height = 44;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Drag to set the angle");
    }

    public double Angle { get => GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
    public double Altitude { get => GetValue(AltitudeProperty); set => SetValue(AltitudeProperty, value); }
    public bool ShowAltitude { get => GetValue(ShowAltitudeProperty); set => SetValue(ShowAltitudeProperty, value); }

    private (Point Center, double Radius) Geometry()
    {
        double r = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) / 2 - 2);
        return (new Point(Bounds.Width / 2, Bounds.Height / 2), r);
    }

    /// <summary>Where the dial points: on the rim, or (with altitude) nearer the center the higher the light is.</summary>
    private Point Tip()
    {
        var (c, r) = Geometry();
        double a = Angle * Math.PI / 180, reach = ShowAltitude ? r * (1 - Math.Clamp(Altitude, 0, 90) / 90) : r;
        return new Point(c.X + Math.Cos(a) * reach, c.Y - Math.Sin(a) * reach);
    }

    public override void Render(DrawingContext context)
    {
        var (c, r) = Geometry();
        var text = HistogramView.Brush(this, "St.Text", Colors.White);
        context.DrawEllipse(HistogramView.Brush(this, "St.Canvas", Colors.Black), new Pen(HistogramView.Brush(this, "St.Border", Colors.Gray), 1), c, r, r);
        var tip = Tip();
        if (ShowAltitude)
        {
            // The globe's latitude circles, then a crosshair at the light.
            var faint = new Pen(HistogramView.Brush(this, "St.Border", Colors.Gray), 1);
            context.DrawEllipse(null, faint, c, r * 2 / 3, r * 2 / 3);
            context.DrawEllipse(null, faint, c, r / 3, r / 3);
            var pen = new Pen(text, 1.2);
            context.DrawLine(pen, new Point(tip.X - 4, tip.Y), new Point(tip.X + 4, tip.Y));
            context.DrawLine(pen, new Point(tip.X, tip.Y - 4), new Point(tip.X, tip.Y + 4));
            context.DrawLine(new Pen(text, 0.8), c, tip);
        }
        else
        {
            context.DrawLine(new Pen(text, 1.5), c, tip);
            context.DrawEllipse(text, null, c, 1.5, 1.5);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        Set(e.GetPosition(this), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Set(e.GetPosition(this), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    /// <summary>The angle (and altitude) that put the dial's point at <paramref name="p"/>.</summary>
    public static (double Angle, double Altitude) ValueAt(Point p, Point center, double radius, bool snap)
    {
        double dx = p.X - center.X, dy = center.Y - p.Y;
        double angle = Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI);
        if (snap) angle = Math.Round(angle / 15) * 15;
        if (angle <= -180) angle += 360;
        double altitude = Math.Round(90 * (1 - Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / radius, 0, 1)));
        return (angle, altitude);
    }

    private void Set(Point p, bool snap)
    {
        var (c, r) = Geometry();
        var (angle, altitude) = ValueAt(p, c, r, snap);
        SetCurrentValue(AngleProperty, angle);
        if (ShowAltitude) SetCurrentValue(AltitudeProperty, altitude);
    }
}
