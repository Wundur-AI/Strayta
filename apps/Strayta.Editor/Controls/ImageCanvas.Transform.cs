using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Strayta.Editor.Editing;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Controls;

// Free Transform on the canvas: draws the box (a quadrilateral once distorted), its eight handles and the reference
// point, or the warp's mesh in Warp mode, and turns pointer input into FreeTransform / WarpTransform drags. ⌘-drags
// distort, ⌘⇧ skews a side and ⌘⌥⇧ puts a corner in perspective, as in Photoshop; a right-click opens the Transform
// menu. Panning (Space, middle button, Hand tool) still works meanwhile.
public sealed partial class ImageCanvas
{
    public static readonly StyledProperty<FreeTransform?> FreeTransformProperty =
        AvaloniaProperty.Register<ImageCanvas, FreeTransform?>(nameof(FreeTransform));

    /// <summary>The open Free Transform to draw and drive, or null.</summary>
    public FreeTransform? FreeTransform { get => GetValue(FreeTransformProperty); set => SetValue(FreeTransformProperty, value); }

    /// <summary>Double-click inside the box: apply the transform.</summary>
    public event Action? TransformCommit;

    /// <summary>Right-click while transforming: show the Transform menu at this point (control coordinates).</summary>
    public event Action<Point>? TransformMenuRequested;

    private const double HandleSize = 7, HandleGrab = 7; // screen points
    private Point? _transformLast;
    private bool _warpDragging;
    private static Cursor? _rotateCursor;

    private void OnFreeTransformChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.OldValue is FreeTransform old) old.Changed -= InvalidateVisual;
        if (change.NewValue is FreeTransform now) now.Changed += InvalidateVisual;
        _transformLast = null;
        _warpDragging = false;
        UpdateCursor();
        InvalidateVisual();
    }

    private Point ToScreen((double X, double Y) p) => new(p.X * Zoom + _offset.X, p.Y * Zoom + _offset.Y);

    private static readonly IPen DarkLine = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 2.5);

    private void DrawTransformBox(DrawingContext context)
    {
        DrawPuppet(context); // ImageCanvas.PuppetWarp.cs
        if (FreeTransform is not { } t) return;
        if (t.Mode == TransformMode.Warp && t.Warp is { } warp && t.WarpFrame is { } frame)
        {
            DrawWarp(context, warp, frame());
            return;
        }
        var corners = t.Corners.Select(ToScreen).ToArray();
        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            g.BeginFigure(corners[0], false);
            for (int i = 1; i < 4; i++) g.LineTo(corners[i]);
            g.EndFigure(true);
        }
        // A dark line under a light one stays visible over any image.
        context.DrawGeometry(null, DarkLine, outline);
        context.DrawGeometry(null, new Pen(Brushes.White, 1), outline);

        var fill = Brushes.White;
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), 1);
        foreach (var h in FreeTransform.ScaleHandles)
        {
            var p = ToScreen(t.HandlePosition(h));
            context.DrawRectangle(fill, edge, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
        }
        var c = ToScreen(t.Center);
        context.DrawEllipse(null, DarkLine, c, 4, 4);
        context.DrawEllipse(null, new Pen(Brushes.White, 1), c, 4, 4);
    }

    /// <summary>
    /// The warp's mesh: grid lines through the patches' thirds (the lines Photoshop draws), and for a custom warp the
    /// anchors (squares), their handles (dots) and the handle lines.
    /// </summary>
    private void DrawWarp(DrawingContext context, WarpTransform warp, Projective frame)
    {
        WarpMesh mesh;
        try
        {
            mesh = warp.DocumentMesh(frame);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return;
        }
        var sx = mesh.SlicesX;
        var sy = mesh.SlicesY;
        var lines = new StreamGeometry();
        using (var g = lines.Open())
        {
            void Curve(Func<double, (double X, double Y)> at)
            {
                const int steps = 32;
                g.BeginFigure(ToScreen(at(0)), false);
                for (int k = 1; k <= steps; k++) g.LineTo(ToScreen(at(k / (double)steps)));
                g.EndFigure(false);
            }
            double x0 = sx[0], x1 = sx[^1], y0 = sy[0], y1 = sy[^1];
            for (int i = 0; i < sx.Count - 1; i++)
                for (int k = 0; k < 3; k++)
                {
                    double u = sx[i] + (sx[i + 1] - sx[i]) * k / 3;
                    Curve(t => mesh.Map(u, y0 + (y1 - y0) * t));
                }
            Curve(t => mesh.Map(x1, y0 + (y1 - y0) * t));
            for (int j = 0; j < sy.Count - 1; j++)
                for (int k = 0; k < 3; k++)
                {
                    double v = sy[j] + (sy[j + 1] - sy[j]) * k / 3;
                    Curve(t => mesh.Map(x0 + (x1 - x0) * t, v));
                }
            Curve(t => mesh.Map(x0 + (x1 - x0) * t, y1));
        }
        context.DrawGeometry(null, DarkLine, lines);
        context.DrawGeometry(null, new Pen(Brushes.White, 1), lines);
        if (!warp.IsCustom) return;

        var points = mesh.Points;
        int columns = 3 * (sx.Count - 1) + 1, rows = 3 * (sy.Count - 1) + 1;
        var handleLine = new Pen(new SolidColorBrush(Color.FromArgb(230, 60, 140, 255)), 1);
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), 1);
        for (int r = 0; r < rows; r += 3)
            for (int c = 0; c < columns; c += 3)
            {
                var a = ToScreen(points[r * columns + c]);
                foreach (var (nr, nc) in new[] { (r - 1, c), (r + 1, c), (r, c - 1), (r, c + 1) })
                {
                    if (nr < 0 || nr >= rows || nc < 0 || nc >= columns) continue;
                    var h = ToScreen(points[nr * columns + nc]);
                    context.DrawLine(handleLine, a, h);
                    context.DrawEllipse(Brushes.White, edge, h, 3, 3);
                }
            }
        for (int r = 0; r < rows; r += 3)
            for (int c = 0; c < columns; c += 3)
            {
                var a = ToScreen(points[r * columns + c]);
                context.DrawRectangle(Brushes.White, edge, new Rect(a.X - HandleSize / 2, a.Y - HandleSize / 2, HandleSize, HandleSize));
            }
    }

    /// <summary>Handles a press while transforming; returns false when the press should pan instead.</summary>
    private bool TransformPressed(PointerPressedEventArgs e)
    {
        if (PuppetPressed(e)) return true; // ImageCanvas.PuppetWarp.cs
        if (FreeTransform is not { } t || _panning) return false;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            TransformMenuRequested?.Invoke(e.GetPosition(this));
            return true;
        }
        if (!props.IsLeftButtonPressed) return true;
        var p = ToImage(e.GetPosition(this));
        if (t.Mode == TransformMode.Warp && t.Warp is { } warp && t.WarpFrame is { } frame)
        {
            if (e.ClickCount == 2)
            {
                TransformCommit?.Invoke();
                return true;
            }
            _warpDragging = warp.BeginDrag(frame(), p.X, p.Y, HandleGrab / Zoom);
            return true;
        }
        var handle = t.HitTest(p.X, p.Y, HandleGrab / Zoom);
        if (e.ClickCount == 2 && handle == TransformHandle.Move)
        {
            TransformCommit?.Invoke();
            return true;
        }
        t.BeginDrag(handle, p.X, p.Y);
        BeginTransformSnap(handle); // ImageCanvas.Snapping.cs
        _transformLast = p;
        return true;
    }

    /// <summary>Handles pointer movement while transforming (drag or hover cursor); false when panning.</summary>
    private bool TransformMoved(PointerEventArgs e)
    {
        if (PuppetMoved(e)) return true;
        if (FreeTransform is not { } t || _panning) return false;
        var p = ToImage(e.GetPosition(this));
        if (_warpDragging && t.Warp is { } warp && t.WarpFrame is { } frame)
        {
            warp.DragTo(frame(), p.X, p.Y);
            return true;
        }
        if (t.IsDragging)
        {
            _transformLast = p;
            var snapped = SnapTransformPoint(t, p, e.KeyModifiers); // handles and the moved box snap (ImageCanvas.Snapping.cs)
            t.DragTo(snapped.X, snapped.Y, e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Alt), IsCommand(e.KeyModifiers));
            SnapTransformMove(t, e.KeyModifiers);
        }
        else if (!_spaceHeld && Tool != CanvasTool.Hand)
        {
            Cursor = t.Mode == TransformMode.Warp ? new Cursor(StandardCursorType.Arrow) : CursorFor(t, t.HitTest(p.X, p.Y, HandleGrab / Zoom), e.KeyModifiers);
        }
        return true;
    }

    private void TransformReleased()
    {
        PuppetReleased();
        FreeTransform?.EndDrag();
        FreeTransform?.Warp?.EndDrag();
        _warpDragging = false;
        _transformLast = null;
    }

    /// <summary>Pressing or releasing Shift, Alt or ⌘ mid-drag takes effect at once, without moving the pointer.</summary>
    private void TransformModifiersChanged(KeyEventArgs e)
    {
        if (FreeTransform is { IsDragging: true } t && _transformLast is { } p)
            t.DragTo(p.X, p.Y, e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Alt), IsCommand(e.KeyModifiers));
    }

    private Cursor CursorFor(FreeTransform t, TransformHandle h, KeyModifiers modifiers = KeyModifiers.None)
    {
        var kind = t.KindOf(h, modifiers.HasFlag(KeyModifiers.Shift), modifiers.HasFlag(KeyModifiers.Alt), IsCommand(modifiers));
        if (kind is DragKind.Distort or DragKind.DistortSide or DragKind.Perspective or DragKind.SkewSide or DragKind.SkewCorner or DragKind.None)
            return new Cursor(StandardCursorType.Arrow);
        if (h == TransformHandle.Move) return new Cursor(StandardCursorType.SizeAll);
        if (kind == DragKind.Rotate) return _rotateCursor ??= CreateRotateCursor();
        // Pick the resize cursor that matches the handle's direction on screen, so it follows the rotation.
        var (hx, hy) = t.HandlePosition(h);
        var (cx, cy) = t.Center;
        double angle = Math.Atan2(hy - cy, hx - cx) * 180 / Math.PI;
        int octant = (int)Math.Round(((angle % 180) + 180) % 180 / 45) % 4;
        return new Cursor(octant switch
        {
            0 => StandardCursorType.SizeWestEast,
            1 => StandardCursorType.BottomRightCorner,
            2 => StandardCursorType.SizeNorthSouth,
            _ => StandardCursorType.BottomLeftCorner,
        });
    }

    /// <summary>A curved two-headed arrow, like Photoshop's rotate cursor (a crosshair where custom cursors fail).</summary>
    private static Cursor CreateRotateCursor()
    {
        try
        {
            return DrawRotateCursor();
        }
        catch (Exception)
        {
            return new Cursor(StandardCursorType.Cross);
        }
    }

    private static Cursor DrawRotateCursor()
    {
        var geometry = StreamGeometry.Parse("M5 15 A10 10 0 0 1 15 5 M5 15 L2.5 11.5 M5 15 L8.5 12.5 M15 5 L11.5 2.5 M15 5 L12.5 8.5");
        var bitmap = new RenderTargetBitmap(new PixelSize(40, 40), new Vector(192, 192));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.DrawGeometry(null, new Pen(Brushes.White, 3.5, lineCap: PenLineCap.Round), geometry);
            ctx.DrawGeometry(null, new Pen(Brushes.Black, 1.5, lineCap: PenLineCap.Round), geometry);
        }
        return new Cursor(bitmap, new PixelPoint(20, 20));
    }
}
