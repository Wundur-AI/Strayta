using Avalonia;
using Avalonia.Input;
using Strayta.Editor.Editing;
using PixelRect = Strayta.Core.PixelRect;

namespace Strayta.Editor.Controls;

// Snapping for the canvas's drags (View › Snap): the Move tool, Free Transform's handles and box, marquee, crop and text
// box drags, guides and the ruler origin. A drag takes a Snapper from the document when it starts (BeginSnap) and snaps
// each pointer position through it; holding Control turns snapping off for that moment, as in Photoshop on the Mac.
// Other tools (shapes, the pen) can do the same: call BeginSnap() on press and SnapDrag() on each move.
public sealed partial class ImageCanvas
{
    /// <summary>Builds the snapper for a drag (DocumentViewModel.CreateSnapper); null snaps nothing.</summary>
    public Func<SnapRequest, Snapper>? SnapperFactory { get; set; }

    /// <summary>The box the Move tool is about to move (the selected layer's pixels), so its edges and center can snap.</summary>
    public Func<PixelRect?>? MoveBounds { get; set; }

    // The current drag's snapper (Off between drags) and, for the Move tool, the moved box and what has been sent so far.
    private Snapper _dragSnapper = Snapper.Off;
    private Snapper? _moveSnapper;
    private PixelRect? _moveBox;
    private Vector _moveEmitted;
    private TransformHandle _snapHandle;

    /// <summary>A snapper for a drag starting now (see <see cref="Snapper"/>).</summary>
    internal Snapper BeginSnap(bool excludeSelected = false, int excludeGuide = -1) =>
        SnapperFactory?.Invoke(new SnapRequest(Zoom, excludeSelected, excludeGuide)) ?? Snapper.Off;

    /// <summary>Control on the Mac suspends snapping while it is held (Photoshop's modifier).</summary>
    private static bool SnapSuspended(KeyModifiers modifiers) => OperatingSystem.IsMacOS() && modifiers.HasFlag(KeyModifiers.Control);

    /// <summary>A dragged point (document coordinates) through the current drag's snapper.</summary>
    private Point SnapDrag(Point image, KeyModifiers modifiers)
    {
        if (!_dragSnapper.IsActive || SnapSuspended(modifiers)) return image;
        var (x, y) = _dragSnapper.SnapPoint(image.X, image.Y);
        return new Point(x, y);
    }

    /// <summary>Every press starts without a snapper; the drag it begins asks for one.</summary>
    private void ResetDragSnapping()
    {
        _dragSnapper = Snapper.Off;
        _moveSnapper = null;
        _moveBox = null;
        _moveEmitted = default;
    }

    /// <summary>
    /// The Move tool's drag: <c>_moveRemainder</c> holds the whole drag in document pixels since the press. Whole pixels
    /// are sent as <see cref="MoveDelta"/> steps; with snapping, the moved box's nearest edge or center lands on a target.
    /// </summary>
    private void MoveDragged(Point pos, Point start, KeyModifiers modifiers)
    {
        _moveRemainder += (pos - start) / Zoom;
        _dragStart = pos;
        var total = _moveRemainder;
        int tx = (int)Math.Truncate(total.X), ty = (int)Math.Truncate(total.Y);
        if (!SnapSuspended(modifiers))
        {
            if (_moveSnapper is null)
            {
                _moveSnapper = BeginSnap(excludeSelected: true);
                _moveBox = _moveSnapper.IsActive ? MoveBounds?.Invoke() : null;
            }
            if (_moveBox is { } b)
            {
                var (sx, sy) = _moveSnapper.SnapRect(b.Left + total.X, b.Top + total.Y, b.Right + total.X, b.Bottom + total.Y);
                if (sx != 0) tx = (int)Math.Round(total.X + sx);
                if (sy != 0) ty = (int)Math.Round(total.Y + sy);
            }
        }
        int dx = tx - (int)_moveEmitted.X, dy = ty - (int)_moveEmitted.Y;
        if (dx == 0 && dy == 0) return;
        _moveEmitted = new Vector(tx, ty);
        MoveDelta?.Invoke(dx, dy);
    }

    /// <summary>A whole Move tool drag by a screen distance, as the pointer makes it (for the self-test).</summary>
    internal void MoveDragForTest(Vector screen, KeyModifiers modifiers = KeyModifiers.None)
    {
        ResetDragSnapping();
        _moveRemainder = default;
        var start = new Point(100, 100);
        MoveDragged(start + screen, start, modifiers);
        _dragStart = null;
    }

    // ---- Free Transform --------------------------------------------------------------------------------

    private void BeginTransformSnap(TransformHandle handle)
    {
        _snapHandle = handle;
        _dragSnapper = BeginSnap(excludeSelected: true);
    }

    /// <summary>A scale handle of an unturned box follows the snapped pointer.</summary>
    private Point SnapTransformPoint(FreeTransform t, Point p, KeyModifiers modifiers) =>
        _snapHandle is not (TransformHandle.Move or TransformHandle.Rotate or TransformHandle.None) && Math.Abs(t.Angle % 90) < 1e-9
            ? SnapDrag(p, modifiers) : p;

    /// <summary>Moving the box: its bounding box's nearest edge or center lands on a target.</summary>
    private void SnapTransformMove(FreeTransform t, KeyModifiers modifiers)
    {
        if (_snapHandle != TransformHandle.Move || !_dragSnapper.IsActive || SnapSuspended(modifiers)) return;
        var corners = t.Corners;
        double l = corners.Min(c => c.X), r = corners.Max(c => c.X), top = corners.Min(c => c.Y), b = corners.Max(c => c.Y);
        var (dx, dy) = _dragSnapper.SnapRect(l, top, r, b);
        if (dx != 0 || dy != 0) t.Nudge(dx, dy);
    }

    // ---- Crop ----------------------------------------------------------------------------------------------

    /// <summary>The crop box's edges snap while it is straight and the image under it has not been moved or turned.</summary>
    private void BeginCropSnap(CropBox box, TransformHandle handle)
    {
        var v = box.View;
        bool straight = !box.IsRotated && v.M11 == 1 && v.M22 == 1 && v.M12 == 0 && v.M21 == 0 && v.Dx == 0 && v.Dy == 0;
        _dragSnapper = straight && handle is not (TransformHandle.Move or TransformHandle.Rotate or TransformHandle.None) ? BeginSnap() : Snapper.Off;
    }
}
