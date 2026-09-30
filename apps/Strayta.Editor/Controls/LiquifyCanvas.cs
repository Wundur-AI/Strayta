using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Strayta.Editor.Editing;

namespace Strayta.Editor.Controls;

/// <summary>
/// Liquify's preview: the liquified layer fitted to the view over a checkerboard, the brush outline at the pointer, and
/// strokes of the current tool (the stationary tools keep working while the button is held, at the brush's rate).
/// </summary>
public sealed class LiquifyCanvas : Control
{
    private readonly LiquifySession _session;
    private readonly WriteableBitmap _bitmap;
    private readonly DispatcherTimer _timer;
    private Point? _hover;
    private bool _stroking, _alt;
    private readonly IBrush _checker;

    public LiquifyCanvas(LiquifySession session)
    {
        _session = session;
        _bitmap = new WriteableBitmap(new PixelSize(session.PreviewWidth, session.PreviewHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) =>
        {
            if (_stroking) _session.Tick(_alt);
        };
        _checker = CheckerBrush();
        ClipToBounds = true;
        Focusable = true;
        session.Changed += Refresh;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiquifySession.BrushSize)) InvalidateVisual();
        };
        Refresh();
    }

    private static IBrush CheckerBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(Color.FromRgb(204, 204, 204)), Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)) });
        group.Children.Add(new GeometryDrawing { Brush = Brushes.White, Geometry = new RectangleGeometry(new Rect(0, 0, 8, 8)) });
        group.Children.Add(new GeometryDrawing { Brush = Brushes.White, Geometry = new RectangleGeometry(new Rect(8, 8, 8, 8)) });
        return new DrawingBrush(group) { TileMode = TileMode.Tile, DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute) };
    }

    private void Refresh()
    {
        using (var fb = _bitmap.Lock())
        {
            int stride = _session.PreviewWidth * 4;
            for (int y = 0; y < _session.PreviewHeight; y++)
                Marshal.Copy(_session.Preview, y * stride, fb.Address + y * fb.RowBytes, stride);
        }
        InvalidateVisual();
    }

    /// <summary>Screen points per preview pixel, and where the image's top-left sits.</summary>
    private (double Scale, Point Offset) Fit()
    {
        double k = Math.Min(Bounds.Width / _session.PreviewWidth, Bounds.Height / _session.PreviewHeight) * 0.98;
        if (!(k > 0)) k = 1;
        return (k, new Point((Bounds.Width - _session.PreviewWidth * k) / 2, (Bounds.Height - _session.PreviewHeight * k) / 2));
    }

    /// <summary>A point of this control in document pixels.</summary>
    private (double X, double Y) ToDocument(Point p)
    {
        var (k, o) = Fit();
        var d = _session.Field.Domain;
        return (d.Left + (p.X - o.X) / k * _session.Scale, d.Top + (p.Y - o.Y) / k * _session.Scale);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(40, 40, 40)), new Rect(Bounds.Size));
        var (k, o) = Fit();
        var dest = new Rect(o.X, o.Y, _session.PreviewWidth * k, _session.PreviewHeight * k);
        context.FillRectangle(_checker, dest);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            context.DrawImage(_bitmap, new Rect(0, 0, _session.PreviewWidth, _session.PreviewHeight), dest);
        if (_hover is { } h)
        {
            double r = Math.Max(2, _session.BrushSize / 2 / _session.Scale * k);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1.5), h, r, r);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 0.75), h, r, r);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var (x, y) = ToDocument(e.GetPosition(this));
        _session.BeginStroke(x, y, _alt);
        _stroking = true;
        if (_session.ToolRepeats) _timer.Start();
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this);
        if (_stroking)
        {
            _alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            foreach (var p in e.GetIntermediatePoints(this))
            {
                var (x, y) = ToDocument(p.Position);
                _session.StrokeTo(x, y, _alt);
            }
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _stroking = false;
        _timer.Stop();
        _session.EndStroke();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        _session.Changed -= Refresh;
    }
}
