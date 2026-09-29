using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Strayta.Editor.Editing;

namespace Strayta.Editor.Controls;

// The Crop tool on the canvas. The image is drawn through the crop's view (turned and moved under the box), and
// the box, its handles, the rule-of-thirds grid and the darkened shield around it are drawn over it in screen
// space. Dragging only changes the CropBox, so the overlay redraws without re-rendering the document.
public sealed partial class ImageCanvas
{
    public static readonly StyledProperty<CropBox?> CropBoxProperty =
        AvaloniaProperty.Register<ImageCanvas, CropBox?>(nameof(CropBox));

    /// <summary>While true, a drag draws a line that the image is then turned to level (the options bar's Straighten).</summary>
    public static readonly StyledProperty<bool> StraightenModeProperty =
        AvaloniaProperty.Register<ImageCanvas, bool>(nameof(StraightenMode));

    /// <summary>The composition guide inside the crop box (<see cref="ViewModels.CropOverlay"/>).</summary>
    public static readonly StyledProperty<ViewModels.CropOverlay> CropOverlayProperty =
        AvaloniaProperty.Register<ImageCanvas, ViewModels.CropOverlay>(nameof(CropOverlay));

    /// <summary>Quarter flips of the Triangle and Golden Spiral overlays (0..3).</summary>
    public static readonly StyledProperty<int> CropOverlayOrientationProperty =
        AvaloniaProperty.Register<ImageCanvas, int>(nameof(CropOverlayOrientation));

    /// <summary>The Perspective Crop tool's shape to draw and drive, or null.</summary>
    public static readonly StyledProperty<PerspectiveCropBox?> PerspectiveCropProperty =
        AvaloniaProperty.Register<ImageCanvas, PerspectiveCropBox?>(nameof(PerspectiveCrop));

    /// <summary>The open crop to draw and drive, or null.</summary>
    public CropBox? CropBox { get => GetValue(CropBoxProperty); set => SetValue(CropBoxProperty, value); }

    public ViewModels.CropOverlay CropOverlay { get => GetValue(CropOverlayProperty); set => SetValue(CropOverlayProperty, value); }

    public int CropOverlayOrientation { get => GetValue(CropOverlayOrientationProperty); set => SetValue(CropOverlayOrientationProperty, value); }

    public PerspectiveCropBox? PerspectiveCrop { get => GetValue(PerspectiveCropProperty); set => SetValue(PerspectiveCropProperty, value); }

    /// <summary>Double-click inside the perspective shape: apply it.</summary>
    public event Action? PerspectiveCropCommit;

    public bool StraightenMode { get => GetValue(StraightenModeProperty); set => SetValue(StraightenModeProperty, value); }

    /// <summary>Double-click inside the box: apply the crop.</summary>
    public event Action? CropCommit;

    /// <summary>A Straighten line was drawn (frame coordinates: start, end).</summary>
    public event Action<Point, Point>? StraightenDrawn;

    private Point? _cropLast, _straightenStart, _straightenEnd;

    private static readonly IBrush Shield = new ImmutableSolidColorBrush(Color.FromArgb(190, 0, 0, 0));
    private static readonly IPen CropEdgeDark = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 2.5);
    private static readonly IPen CropEdgeLight = new ImmutablePen(Brushes.White, 1);
    private static readonly IPen ThirdsPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1);
    private static readonly IPen HandleDark = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 5, lineCap: PenLineCap.Square);
    private static readonly IPen HandleLight = new ImmutablePen(Brushes.White, 3, lineCap: PenLineCap.Square);

    private void OnCropPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == CropBoxProperty)
        {
            if (change.OldValue is CropBox old) old.Changed -= InvalidateVisual;
            if (change.NewValue is CropBox now) now.Changed += InvalidateVisual;
            _cropLast = _straightenStart = _straightenEnd = null;
            UpdateCursor();
            InvalidateVisual();
        }
        else if (change.Property == PerspectiveCropProperty)
        {
            if (change.OldValue is PerspectiveCropBox old) old.Changed -= InvalidateVisual;
            if (change.NewValue is PerspectiveCropBox now) now.Changed += InvalidateVisual;
            InvalidateVisual();
        }
        else if (change.Property == CropOverlayProperty || change.Property == CropOverlayOrientationProperty)
            InvalidateVisual();
    }

    /// <summary>
    /// The transform, in screen space, that draws the image (laid out at its usual place) turned and moved as the
    /// crop's view says. Identity without a crop.
    /// </summary>
    private Matrix CropImageTransform()
    {
        if (CropBox is not { } box) return Matrix.Identity;
        var v = box.View;
        var view = new Matrix(v.M11, v.M21, v.M12, v.M22, v.Dx, v.Dy); // Avalonia multiplies row vectors
        return Matrix.CreateTranslation(-_offset.X, -_offset.Y) * Matrix.CreateScale(1 / Zoom, 1 / Zoom) * view
               * Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(_offset.X, _offset.Y);
    }

    private void DrawCropOverlay(DrawingContext context)
    {
        DrawPerspectiveCrop(context);
        if (CropBox is not { } box) return;
        var tl = ToScreen((box.Left, box.Top));
        var br = ToScreen((box.Right, box.Bottom));
        var rect = new Rect(tl, br);
        var all = new Rect(Bounds.Size);

        // Darken everything outside the box (Photoshop's shield).
        context.FillRectangle(Shield, new Rect(all.Left, all.Top, all.Width, Math.Max(0, rect.Top - all.Top)));
        context.FillRectangle(Shield, new Rect(all.Left, rect.Bottom, all.Width, Math.Max(0, all.Bottom - rect.Bottom)));
        context.FillRectangle(Shield, new Rect(all.Left, rect.Top, Math.Max(0, rect.Left - all.Left), rect.Height));
        context.FillRectangle(Shield, new Rect(rect.Right, rect.Top, Math.Max(0, all.Right - rect.Right), rect.Height));

        DrawCropGuide(context, rect); // ImageCanvas.CropOverlays.cs
        context.DrawRectangle(null, CropEdgeDark, rect);
        context.DrawRectangle(null, CropEdgeLight, rect);

        // Handles: corner brackets and short bars on the sides, as in Photoshop.
        double arm = Math.Min(14, Math.Min(rect.Width, rect.Height) / 3);
        foreach (var pen in new[] { HandleDark, HandleLight })
        {
            void Corner(Point p, double dx, double dy)
            {
                context.DrawLine(pen, p, p + new Vector(dx * arm, 0));
                context.DrawLine(pen, p, p + new Vector(0, dy * arm));
            }
            Corner(rect.TopLeft, 1, 1);
            Corner(rect.TopRight, -1, 1);
            Corner(rect.BottomRight, -1, -1);
            Corner(rect.BottomLeft, 1, -1);
            var c = rect.Center;
            context.DrawLine(pen, new Point(c.X - arm / 2, rect.Top), new Point(c.X + arm / 2, rect.Top));
            context.DrawLine(pen, new Point(c.X - arm / 2, rect.Bottom), new Point(c.X + arm / 2, rect.Bottom));
            context.DrawLine(pen, new Point(rect.Left, c.Y - arm / 2), new Point(rect.Left, c.Y + arm / 2));
            context.DrawLine(pen, new Point(rect.Right, c.Y - arm / 2), new Point(rect.Right, c.Y + arm / 2));
        }

        if (_straightenStart is { } s && _straightenEnd is { } e)
        {
            var a = ToScreen((s.X, s.Y));
            var b = ToScreen((e.X, e.Y));
            context.DrawLine(CropEdgeDark, a, b);
            context.DrawLine(CropEdgeLight, a, b);
        }
    }

    /// <summary>Handles a press while cropping; returns false when the press should pan instead.</summary>
    private bool CropPressed(PointerPressedEventArgs e)
    {
        if (_panning) return false;
        if (PerspectivePressed(e)) return true; // ImageCanvas.PerspectiveCrop.cs
        if (CropBox is not { } box) return Tool == CanvasTool.Crop; // between a crop and its new box: presses do nothing
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed || box.Locked) return true;
        var p = ToImage(e.GetPosition(this));
        if (StraightenMode)
        {
            _straightenStart = _straightenEnd = p;
            return true;
        }
        var handle = box.HitTest(p.X, p.Y, HandleGrab / Zoom);
        if (e.ClickCount == 2 && handle == TransformHandle.Move)
        {
            CropCommit?.Invoke();
            return true;
        }
        box.BeginDrag(handle, p.X, p.Y);
        _cropLast = p;
        return true;
    }

    /// <summary>Handles pointer movement while cropping (drag or hover cursor); false when panning.</summary>
    private bool CropMoved(PointerEventArgs e)
    {
        if (!_panning && PerspectiveMoved(e)) return true;
        if (CropBox is not { } box || _panning) return false;
        var p = ToImage(e.GetPosition(this));
        if (_straightenStart is not null)
        {
            _straightenEnd = p;
            InvalidateVisual();
        }
        else if (box.IsDragging)
        {
            _cropLast = p;
            box.DragTo(p.X, p.Y, e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        }
        else if (!_spaceHeld && Tool != CanvasTool.Hand)
        {
            Cursor = StraightenMode ? new Cursor(StandardCursorType.Cross) : CropCursor(box.HitTest(p.X, p.Y, HandleGrab / Zoom));
        }
        return true;
    }

    private void CropReleased()
    {
        PerspectiveCrop?.EndDrag();
        if (_straightenStart is { } s && _straightenEnd is { } e)
        {
            _straightenStart = _straightenEnd = null;
            StraightenDrawn?.Invoke(s, e);
            InvalidateVisual();
        }
        CropBox?.EndDrag();
        _cropLast = null;
    }

    /// <summary>Pressing or releasing Shift or Alt mid-drag takes effect at once.</summary>
    private void CropModifiersChanged(KeyEventArgs e)
    {
        if (CropBox is { IsDragging: true } box && _cropLast is { } p)
            box.DragTo(p.X, p.Y, e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Alt));
    }

    private static Cursor CropCursor(TransformHandle h) => h switch
    {
        TransformHandle.Move => new Cursor(StandardCursorType.SizeAll),
        TransformHandle.Rotate => _rotateCursor ??= CreateRotateCursor(),
        TransformHandle.Left or TransformHandle.Right => new Cursor(StandardCursorType.SizeWestEast),
        TransformHandle.Top or TransformHandle.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
        TransformHandle.TopLeft or TransformHandle.BottomRight => new Cursor(StandardCursorType.BottomRightCorner),
        _ => new Cursor(StandardCursorType.BottomLeftCorner),
    };
}
