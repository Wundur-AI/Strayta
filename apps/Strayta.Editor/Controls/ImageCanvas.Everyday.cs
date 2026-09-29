using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Strayta.Editor.Controls;

// Eyedropper, Paint Bucket, Gradient and Zoom input on the canvas, and what they draw over the image: the
// Eyedropper's sampling ring and the Gradient's drag line. Holding Option with the Brush, Paint Bucket or Gradient
// turns the pointer into the Eyedropper until it is released, as in Photoshop.
public sealed partial class ImageCanvas
{
    /// <summary>Eyedropper press at an image pixel: (x, y, into the background color). Returns false to refuse.</summary>
    public Func<int, int, bool, bool>? EyedropperBegin { get; set; }
    public event Action<int, int>? EyedropperMove;
    public event Action? EyedropperEnd;

    /// <summary>The foreground (false) or background (true) color, for the sampling ring.</summary>
    public Func<bool, Color>? EyedropperColor { get; set; }

    /// <summary>A Paint Bucket click at an image pixel.</summary>
    public event Action<int, int>? PaintBucketClicked;

    /// <summary>Gradient drag in image coordinates: begin (returns false to refuse), end point updates, release.</summary>
    public Func<float, float, bool>? GradientBegin { get; set; }
    public event Action<float, float>? GradientMove;
    public event Action? GradientEnd;

    private enum ToolGesture { None, Eyedropper, Gradient, Zoom }

    private ToolGesture _gesture;
    private bool _altHeld;
    private Color _ringPrevious;
    private bool _ringBackground;
    private Point _ringAt;
    private Point _gradientStart, _gradientEnd, _gradientRaw;
    private Point _zoomPress;
    private double _zoomFrom;
    private bool _zoomScrubbed, _zoomOut;

    private static Cursor? _eyedropperCursor, _bucketCursor, _zoomInCursor, _zoomOutCursor;

    /// <summary>Photoshop's preset zoom levels, which a Zoom-tool click or ⌘+ / ⌘− step through.</summary>
    private static readonly double[] ZoomLevels =
    [
        0.02, 0.03, 0.04, 0.05, 1 / 16.0, 1 / 12.0, 1 / 8.0, 1 / 6.0, 0.25, 1 / 3.0, 0.5, 2 / 3.0,
        1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 24, 32, 48, 64,
    ];

    /// <summary>Option-click with these tools samples a color instead of painting.</summary>
    private bool OptionPicksColor => Tool is CanvasTool.Brush or CanvasTool.PaintBucket or CanvasTool.Gradient;

    /// <summary>True while the pointer acts as the Eyedropper: the tool itself, or Option held with a painting tool.</summary>
    private bool EyedropperActive => Tool == CanvasTool.Eyedropper || (_altHeld && OptionPicksColor);

    /// <summary>Handles a press for these tools; false when another tool should handle it.</summary>
    private bool EverydayPressed(PointerPressedEventArgs e, PointerPointProperties props)
    {
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        SetAlt(alt);
        bool temporary = alt && OptionPicksColor;
        if (!temporary && Tool is not (CanvasTool.Eyedropper or CanvasTool.PaintBucket or CanvasTool.Gradient or CanvasTool.Zoom)) return false;
        _dragStart = null; // these drags must never fall through to moving the layer
        if (!props.IsLeftButtonPressed) return true;

        var screen = e.GetPosition(this);
        var p = ToImage(screen);
        int px = (int)Math.Floor(p.X), py = (int)Math.Floor(p.Y);
        if (temporary || Tool == CanvasTool.Eyedropper)
        {
            // With the Eyedropper itself Option samples into the background color; as a stand-in it is the trigger.
            bool background = !temporary && alt;
            var previous = EyedropperColor?.Invoke(background) ?? Colors.Transparent;
            if (EyedropperBegin?.Invoke(px, py, background) != true) return true;
            (_gesture, _ringPrevious, _ringBackground, _ringAt) = (ToolGesture.Eyedropper, previous, background, screen);
        }
        else if (Tool == CanvasTool.PaintBucket) PaintBucketClicked?.Invoke(px, py);
        else if (Tool == CanvasTool.Gradient)
        {
            if (GradientBegin?.Invoke((float)p.X, (float)p.Y) != true) return true;
            _gesture = ToolGesture.Gradient;
            _gradientStart = _gradientEnd = _gradientRaw = p;
        }
        else
        {
            (_gesture, _zoomPress, _zoomFrom, _zoomScrubbed, _zoomOut) = (ToolGesture.Zoom, screen, Zoom, false, alt);
        }
        InvalidateVisual();
        return true;
    }

    /// <summary>Handles movement during one of these drags; false when there is none.</summary>
    private bool EverydayMoved(PointerEventArgs e)
    {
        if (_gesture == ToolGesture.None)
        {
            SetAlt(e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            return false;
        }
        var screen = e.GetPosition(this);
        switch (_gesture)
        {
            case ToolGesture.Eyedropper:
                _ringAt = screen;
                var p = ToImage(screen);
                EyedropperMove?.Invoke((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
                InvalidateVisual();
                break;
            case ToolGesture.Gradient:
                _gradientRaw = ToImage(screen);
                _gradientEnd = GradientEndPoint(_gradientRaw, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                GradientMove?.Invoke((float)_gradientEnd.X, (float)_gradientEnd.Y);
                InvalidateVisual();
                break;
            case ToolGesture.Zoom:
                // Scrubby zoom: dragging right zooms in, left zooms out, around where the drag started; 100 points doubles.
                double dx = screen.X - _zoomPress.X;
                if (!_zoomScrubbed && Math.Abs(dx) < 3) break;
                _zoomScrubbed = true;
                ZoomAround(_zoomPress, _zoomFrom * Math.Pow(2, dx / 100));
                break;
        }
        return true;
    }

    private void EverydayReleased(PointerReleasedEventArgs e)
    {
        var gesture = _gesture;
        _gesture = ToolGesture.None;
        switch (gesture)
        {
            case ToolGesture.Eyedropper:
                EyedropperEnd?.Invoke();
                break;
            case ToolGesture.Gradient:
                _gradientEnd = GradientEndPoint(ToImage(e.GetPosition(this)), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                GradientMove?.Invoke((float)_gradientEnd.X, (float)_gradientEnd.Y);
                GradientEnd?.Invoke();
                break;
            case ToolGesture.Zoom when !_zoomScrubbed:
                ZoomStep(!_zoomOut, _zoomPress);
                break;
        }
        if (gesture != ToolGesture.None) InvalidateVisual();
    }

    /// <summary>Option changes the cursor (and the gradient's constraint follows Shift) without moving the pointer.</summary>
    private void EverydayKeyChanged(KeyEventArgs e, bool down)
    {
        SetAlt(e.Key is Key.LeftAlt or Key.RightAlt ? down : e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        if (_gesture == ToolGesture.Gradient && e.Key is Key.LeftShift or Key.RightShift)
        {
            _gradientEnd = GradientEndPoint(_gradientRaw, down);
            GradientMove?.Invoke((float)_gradientEnd.X, (float)_gradientEnd.Y);
            InvalidateVisual();
        }
    }

    private void SetAlt(bool held)
    {
        if (held == _altHeld) return;
        _altHeld = held;
        UpdateCursor();
        InvalidateVisual(); // the brush outline hides while Option picks colors
    }

    /// <summary>Shift constrains the gradient to multiples of 45°, keeping its length along that direction.</summary>
    private Point GradientEndPoint(Point p, bool constrain)
    {
        if (!constrain) return p;
        var d = p - _gradientStart;
        double angle = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 4)) * (Math.PI / 4);
        var (sin, cos) = Math.SinCos(angle);
        double length = Math.Max(0, d.X * cos + d.Y * sin);
        return new Point(_gradientStart.X + cos * length, _gradientStart.Y + sin * length);
    }

    // ---- Zoom -------------------------------------------------------------------------------------

    /// <summary>The next preset zoom level in or out, keeping <paramref name="anchor"/> (default: the view's center) in place.</summary>
    public void ZoomStep(bool zoomIn, Point? anchor = null)
    {
        if (Source is null) return;
        double z = ZoomTarget; // a step during an animated step continues from where it was heading (ImageCanvas.Zoom.cs)
        double next = zoomIn
            ? ZoomLevels.FirstOrDefault(l => l > z * 1.001, ZoomLevels[^1])
            : ZoomLevels.LastOrDefault(l => l < z / 1.001, ZoomLevels[0]);
        ZoomAnimated(anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2), next);
    }

    private void ZoomAround(Point p, double next)
    {
        double old = Zoom;
        next = Math.Clamp(next, ZoomLevels[0], ZoomLevels[^1]);
        if (next == old) return;
        _offset = new Vector(p.X - (p.X - _offset.X) * next / old, p.Y - (p.Y - _offset.Y) * next / old);
        Zoom = next;
        InvalidateVisual();
    }

    // ---- Drawing and cursors ----------------------------------------------------------------------

    private void RenderEverydayTools(DrawingContext context)
    {
        if (_gesture == ToolGesture.Gradient) DrawGradientLine(context);
        if (_gesture == ToolGesture.Eyedropper) DrawSamplingRing(context);
    }

    /// <summary>The drag line with a dot at each end, dark under light so it shows over any image.</summary>
    private void DrawGradientLine(DrawingContext context)
    {
        var a = new Point(_gradientStart.X * Zoom + _offset.X, _gradientStart.Y * Zoom + _offset.Y);
        var b = new Point(_gradientEnd.X * Zoom + _offset.X, _gradientEnd.Y * Zoom + _offset.Y);
        var dark = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 3, lineCap: PenLineCap.Round);
        var light = new Pen(Brushes.White, 1.2, lineCap: PenLineCap.Round);
        context.DrawLine(dark, a, b);
        context.DrawLine(light, a, b);
        foreach (var p in new[] { a, b })
        {
            context.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1), p, 3.5, 3.5);
        }
    }

    /// <summary>
    /// Photoshop's sampling ring: the new color in the top half, the color it replaces in the bottom half, framed by
    /// neutral gray so both read against any image.
    /// </summary>
    private void DrawSamplingRing(DrawingContext context)
    {
        const double outer = 52, inner = 34;
        var c = _ringAt;
        var now = EyedropperColor?.Invoke(_ringBackground) ?? _ringPrevious;
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(128, 128, 128)), 8), c, outer + 4, outer + 4);
        context.DrawGeometry(new SolidColorBrush(now), null, HalfRing(c, outer, inner, top: true));
        context.DrawGeometry(new SolidColorBrush(_ringPrevious), null, HalfRing(c, outer, inner, top: false));
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 1);
        context.DrawEllipse(null, edge, c, outer + 8, outer + 8);
        context.DrawEllipse(null, edge, c, inner, inner);
    }

    private static StreamGeometry HalfRing(Point c, double outer, double inner, bool top)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        // Clockwise on screen along the outside, back along the inside.
        ctx.BeginFigure(new Point(c.X + (top ? -outer : outer), c.Y), true);
        ctx.ArcTo(new Point(c.X + (top ? outer : -outer), c.Y), new Size(outer, outer), 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(c.X + (top ? inner : -inner), c.Y));
        ctx.ArcTo(new Point(c.X + (top ? -inner : inner), c.Y), new Size(inner, inner), 0, false, SweepDirection.CounterClockwise);
        ctx.EndFigure(true);
        return g;
    }

    /// <summary>The cursor for these tools (null for other tools).</summary>
    private Cursor? EverydayCursor()
    {
        if (EyedropperActive) return _eyedropperCursor ??= IconCursor("M20.2 3.8a2.4 2.4 0 0 0-3.4 0L14 6.6l-1.3-1.3-1.9 1.9 6 6 1.9-1.9-1.3-1.3 2.8-2.8a2.4 2.4 0 0 0 0-3.4Z M13 8.9l-8.3 8.3L4 20l2.8-.7 8.3-8.3", 4, 20);
        return Tool switch
        {
            CanvasTool.PaintBucket => _bucketCursor ??= IconCursor("M11 4.5l7 7-6.3 6.3a1.6 1.6 0 0 1-2.2 0l-4.8-4.8a1.6 1.6 0 0 1 0-2.2Z M4.2 12.5H17 M8.5 2.5l2.5 2 M20 14.5s1.8 2.3 1.8 3.5a1.8 1.8 0 0 1-3.6 0c0-1.2 1.8-3.5 1.8-3.5Z", 20, 19.8),
            CanvasTool.Zoom when _altHeld => _zoomOutCursor ??= IconCursor("M10.5 3.5a7 7 0 1 0 0 14a7 7 0 1 0 0-14Z M15.5 15.5L21 21 M7.5 10.5h6", 10.5, 10.5),
            CanvasTool.Zoom => _zoomInCursor ??= IconCursor("M10.5 3.5a7 7 0 1 0 0 14a7 7 0 1 0 0-14Z M15.5 15.5L21 21 M7.5 10.5h6 M10.5 7.5v6", 10.5, 10.5),
            CanvasTool.Gradient => new Cursor(StandardCursorType.Cross),
            _ => null,
        };
    }

    /// <summary>A cursor drawn from a 24×24 line icon, with its hot spot at (<paramref name="hx"/>, <paramref name="hy"/>) on that grid.</summary>
    private static Cursor IconCursor(string data, double hx, double hy)
    {
        try
        {
            const int size = 48; // pixels at 2× scale: a 24-point cursor
            var geometry = StreamGeometry.Parse(data);
            var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(192, 192));
            using (var ctx = bitmap.CreateDrawingContext())
            using (ctx.PushTransform(Matrix.CreateScale(20 / 24.0, 20 / 24.0) * Matrix.CreateTranslation(2, 2)))
            {
                ctx.DrawGeometry(null, new Pen(Brushes.White, 3.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
                ctx.DrawGeometry(null, new Pen(Brushes.Black, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
            }
            return new Cursor(bitmap, new PixelPoint((int)Math.Round((hx * 20 / 24.0 + 2) * 2), (int)Math.Round((hy * 20 / 24.0 + 2) * 2)));
        }
        catch (Exception)
        {
            return new Cursor(StandardCursorType.Cross);
        }
    }
}
