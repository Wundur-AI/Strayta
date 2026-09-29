using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Strayta.Editor.Editing;

namespace Strayta.Editor.Controls;

// Free Transform on the canvas: draws the box, its eight handles and the reference point, and turns
// pointer input into FreeTransform drags. Panning (Space, middle button, Hand tool) still works meanwhile.
public sealed partial class ImageCanvas
{
    public static readonly StyledProperty<FreeTransform?> FreeTransformProperty =
        AvaloniaProperty.Register<ImageCanvas, FreeTransform?>(nameof(FreeTransform));

    /// <summary>The open Free Transform to draw and drive, or null.</summary>
    public FreeTransform? FreeTransform { get => GetValue(FreeTransformProperty); set => SetValue(FreeTransformProperty, value); }

    /// <summary>Double-click inside the box: apply the transform.</summary>
    public event Action? TransformCommit;

    private const double HandleSize = 7, HandleGrab = 7; // screen points
    private Point? _transformLast;
    private static Cursor? _rotateCursor;

    private void OnFreeTransformChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.OldValue is FreeTransform old) old.Changed -= InvalidateVisual;
        if (change.NewValue is FreeTransform now) now.Changed += InvalidateVisual;
        _transformLast = null;
        UpdateCursor();
        InvalidateVisual();
    }

    private Point ToScreen((double X, double Y) p) => new(p.X * Zoom + _offset.X, p.Y * Zoom + _offset.Y);

    private void DrawTransformBox(DrawingContext context)
    {
        if (FreeTransform is not { } t) return;
        var corners = t.Corners.Select(ToScreen).ToArray();
        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            g.BeginFigure(corners[0], false);
            for (int i = 1; i < 4; i++) g.LineTo(corners[i]);
            g.EndFigure(true);
        }
        // A dark line under a light one stays visible over any image.
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 2.5), outline);
        context.DrawGeometry(null, new Pen(Brushes.White, 1), outline);

        var fill = Brushes.White;
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), 1);
        foreach (var h in FreeTransform.ScaleHandles)
        {
            var p = ToScreen(t.HandlePosition(h));
            context.DrawRectangle(fill, edge, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
        }
        var c = ToScreen(t.Center);
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 2.5), c, 4, 4);
        context.DrawEllipse(null, new Pen(Brushes.White, 1), c, 4, 4);
    }

    /// <summary>Handles a press while transforming; returns false when the press should pan instead.</summary>
    private bool TransformPressed(PointerPressedEventArgs e)
    {
        if (FreeTransform is not { } t || _panning) return false;
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return true;
        var p = ToImage(e.GetPosition(this));
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
        if (FreeTransform is not { } t || _panning) return false;
        var p = ToImage(e.GetPosition(this));
        if (t.IsDragging)
        {
            _transformLast = p;
            var snapped = SnapTransformPoint(t, p, e.KeyModifiers); // handles and the moved box snap (ImageCanvas.Snapping.cs)
            t.DragTo(snapped.X, snapped.Y, e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            SnapTransformMove(t, e.KeyModifiers);
        }
        else if (!_spaceHeld && Tool != CanvasTool.Hand)
        {
            Cursor = CursorFor(t, t.HitTest(p.X, p.Y, HandleGrab / Zoom));
        }
        return true;
    }

    private void TransformReleased()
    {
        FreeTransform?.EndDrag();
        _transformLast = null;
    }

    /// <summary>Pressing or releasing Shift or Alt mid-drag takes effect at once, without moving the pointer.</summary>
    private void TransformModifiersChanged(KeyEventArgs e)
    {
        if (FreeTransform is { IsDragging: true } t && _transformLast is { } p)
            t.DragTo(p.X, p.Y, e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Alt));
    }

    private Cursor CursorFor(FreeTransform t, TransformHandle h)
    {
        if (h == TransformHandle.Move) return new Cursor(StandardCursorType.SizeAll);
        if (h == TransformHandle.Rotate) return _rotateCursor ??= CreateRotateCursor();
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
