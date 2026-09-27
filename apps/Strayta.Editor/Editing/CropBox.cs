using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>
/// The Crop tool's state, in Photoshop's (non-classic) style: the crop box stays upright on screen and the image
/// moves and turns under it. Two things describe it:
/// <list type="bullet">
/// <item><see cref="View"/>, a rigid map (rotation and move) from document pixels to the "frame" the box is drawn
/// in. The canvas draws the image through it; the frame itself maps to the screen with the usual zoom and pan.</item>
/// <item>The box, an upright rectangle in frame coordinates.</item>
/// </list>
/// Dragging a handle resizes the box (Shift keeps its proportions, Option works from the center, a ratio preset
/// always keeps them), dragging inside moves the image under the box, dragging outside turns the image about the
/// box's center, shrinking the box if needed so no empty corners appear. Nothing here touches pixels; committing
/// maps the document through <see cref="ResultMap"/> onto a <see cref="ResultWidth"/>×<see cref="ResultHeight"/> canvas.
/// </summary>
public sealed class CropBox : ObservableObject
{
    private readonly int _docWidth, _docHeight;
    private Affine _view = Affine.Identity;
    private double _angle, _left, _top, _right, _bottom;

    // Drag in progress: the handle, and the state and point it started from.
    private TransformHandle _drag;
    private (Affine View, double Angle, double Left, double Top, double Right, double Bottom) _start;
    private (double X, double Y) _startPoint;
    private bool _startInside;

    public CropBox(int docWidth, int docHeight)
    {
        _docWidth = docWidth;
        _docHeight = docHeight;
        (_left, _top, _right, _bottom) = (0, 0, docWidth, docHeight);
    }

    /// <summary>Raised after every change, so the canvas redraws the overlay (the document is not re-rendered).</summary>
    public event Action? Changed;

    /// <summary>While true (the crop is being applied) drags and edits are ignored.</summary>
    public bool Locked { get; set; }

    /// <summary>Document pixels to frame coordinates: how the image is turned and moved under the box.</summary>
    public Affine View => _view;

    /// <summary>How far the image is turned, in degrees clockwise, in (-180, 180].</summary>
    public double Angle => _angle;

    public double Left => _left;
    public double Top => _top;
    public double Right => _right;
    public double Bottom => _bottom;
    public (double X, double Y) Center => ((_left + _right) / 2, (_top + _bottom) / 2);

    /// <summary>Width over height the box keeps, or null for a free box.</summary>
    public double? AspectRatio { get; private set; }

    public bool IsRotated => Math.Abs(_angle) > 1e-9;

    /// <summary>Size of the cropped canvas in pixels.</summary>
    public int ResultWidth => Math.Max(1, (int)Math.Round(_right - _left));
    public int ResultHeight => Math.Max(1, (int)Math.Round(_bottom - _top));

    /// <summary>Old document coordinates to the cropped canvas.</summary>
    public Affine ResultMap => _view.Then(Affine.Translation(-_left, -_top));

    /// <summary>False while the box still frames the whole, unturned image (committing would do nothing).</summary>
    public bool IsModified =>
        !(_view.IsIntegerTranslation(out int dx, out int dy) && dx == 0 && dy == 0 && _left == 0 && _top == 0 && _right == _docWidth && _bottom == _docHeight);

    /// <summary>"1200 × 800 px", and the angle when turned, for the options bar.</summary>
    public string SizeText => IsRotated ? $"{ResultWidth} × {ResultHeight} px · {_angle:0.0}°" : $"{ResultWidth} × {ResultHeight} px";

    /// <summary>The image's corners in frame coordinates (top-left, top-right, bottom-right, bottom-left).</summary>
    public (double X, double Y)[] ImageCorners =>
    [
        _view.Apply(0, 0), _view.Apply(_docWidth, 0), _view.Apply(_docWidth, _docHeight), _view.Apply(0, _docHeight),
    ];

    // ---- Options bar ------------------------------------------------------------------------------

    /// <summary>Back to the whole, unturned image (Esc, or the reset button).</summary>
    public void Reset()
    {
        if (Locked) return;
        _view = Affine.Identity;
        _angle = 0;
        (_left, _top, _right, _bottom) = (0, 0, _docWidth, _docHeight);
        if (AspectRatio is { } ratio) FitRatio(ratio);
        Notify();
    }

    /// <summary>
    /// A ratio preset (null frees the box): the box becomes the largest one of that shape inside it, centered, as
    /// Photoshop does when a preset is picked.
    /// </summary>
    public void SetAspectRatio(double? ratio)
    {
        if (Locked) return;
        AspectRatio = ratio is > 0 ? ratio : null;
        if (AspectRatio is { } r) FitRatio(r);
        Notify();
    }

    /// <summary>Swaps the box's width and height about its center (the options bar's ⇄).</summary>
    public void SwapOrientation()
    {
        if (Locked) return;
        if (AspectRatio is { } r) AspectRatio = 1 / r;
        var (cx, cy) = Center;
        double w = _right - _left, h = _bottom - _top;
        SetBox(cx - h / 2, cy - w / 2, cx + h / 2, cy - w / 2 + w, round: !IsRotated);
        Notify();
    }

    private void FitRatio(double ratio)
    {
        var (cx, cy) = Center;
        double w = _right - _left, h = _bottom - _top;
        if (w / h > ratio) w = h * ratio;
        else h = w / ratio;
        SetBox(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2, round: !IsRotated);
    }

    // ---- Dragging ---------------------------------------------------------------------------------

    /// <summary>What a press at frame point (x, y) grabs: a handle within <paramref name="tolerance"/>, the inside (move the image) or the outside (turn it).</summary>
    public TransformHandle HitTest(double x, double y, double tolerance)
    {
        var best = TransformHandle.None;
        double bestDist = tolerance;
        foreach (var h in FreeTransform.ScaleHandles)
        {
            var (hx, hy) = HandlePosition(h);
            double d = Math.Max(Math.Abs(hx - x), Math.Abs(hy - y));
            if (d <= bestDist)
            {
                bestDist = d;
                best = h;
            }
        }
        if (best != TransformHandle.None) return best;
        // Edges are grabbed along their whole length, as in Photoshop.
        bool inX = x > _left - tolerance && x < _right + tolerance, inY = y > _top - tolerance && y < _bottom + tolerance;
        if (inY && Math.Abs(x - _left) <= tolerance) return TransformHandle.Left;
        if (inY && Math.Abs(x - _right) <= tolerance) return TransformHandle.Right;
        if (inX && Math.Abs(y - _top) <= tolerance) return TransformHandle.Top;
        if (inX && Math.Abs(y - _bottom) <= tolerance) return TransformHandle.Bottom;
        return x >= _left && x <= _right && y >= _top && y <= _bottom ? TransformHandle.Move : TransformHandle.Rotate;
    }

    /// <summary>Where a handle sits, in frame coordinates.</summary>
    public (double X, double Y) HandlePosition(TransformHandle handle)
    {
        var (ix, iy) = Signs(handle);
        var (cx, cy) = Center;
        return (cx + ix * (_right - _left) / 2, cy + iy * (_bottom - _top) / 2);
    }

    public bool IsDragging => _drag != TransformHandle.None;

    public void BeginDrag(TransformHandle handle, double x, double y)
    {
        if (Locked) return;
        _drag = handle;
        _start = (_view, _angle, _left, _top, _right, _bottom);
        _startPoint = (x, y);
        _startInside = MaxScaleInside(_start.View, _left, _top, _right, _bottom) >= 1 - 1e-6;
    }

    public void DragTo(double x, double y, bool shift, bool alt)
    {
        if (Locked || _drag == TransformHandle.None) return;
        var s = _start;
        switch (_drag)
        {
            case TransformHandle.Move:
            {
                // The image follows the pointer; unturned it moves by whole pixels so it is never resampled.
                double dx = x - _startPoint.X, dy = y - _startPoint.Y;
                if (shift)
                {
                    if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
                    else dx = 0;
                }
                if (Math.Abs(s.Angle) < 1e-9) (dx, dy) = (Math.Round(dx), Math.Round(dy));
                _view = s.View.Then(Affine.Translation(dx, dy));
                Notify();
                return;
            }
            case TransformHandle.Rotate:
            {
                double cx = (s.Left + s.Right) / 2, cy = (s.Top + s.Bottom) / 2;
                double a0 = Math.Atan2(_startPoint.Y - cy, _startPoint.X - cx), a1 = Math.Atan2(y - cy, x - cx);
                double angle = s.Angle + (a1 - a0) * 180 / Math.PI;
                if (shift) angle = Math.Round(angle / 15) * 15;
                RotateFromStart(angle);
                return;
            }
        }

        var (ix, iy) = Signs(_drag);
        double left = s.Left, top = s.Top, right = s.Right, bottom = s.Bottom;
        double scx = (left + right) / 2, scy = (top + bottom) / 2;
        // Anchor: the opposite side, or the center with Option.
        double ax = alt ? scx : ix > 0 ? left : right, ay = alt ? scy : iy > 0 ? top : bottom;
        double w = ix == 0 ? right - left : Math.Max(1, Math.Abs(x - ax) * (alt ? 2 : 1));
        double h = iy == 0 ? bottom - top : Math.Max(1, Math.Abs(y - ay) * (alt ? 2 : 1));
        // Past the anchor the box does not flip; it stops at one pixel.
        if (ix != 0 && (x - ax) * ix < 0) w = 1;
        if (iy != 0 && (y - ay) * iy < 0) h = 1;

        double? ratio = AspectRatio ?? (shift ? (s.Right - s.Left) / (s.Bottom - s.Top) : null);
        if (ratio is { } k)
        {
            if (ix != 0 && iy != 0)
            {
                if (w / k > h) h = w / k;
                else w = h * k;
            }
            else if (ix != 0) h = w / k;
            else w = h * k;
        }

        // Place the new size against the anchor; an edge drag with a fixed ratio grows about the other axis' center.
        double nl = ix == 0 ? scx - w / 2 : alt ? ax - w / 2 : ix > 0 ? ax : ax - w;
        double nt = iy == 0 ? scy - h / 2 : alt ? ay - h / 2 : iy > 0 ? ay : ay - h;
        if (ix == 0 && ratio is null) (nl, w) = (left, right - left);
        if (iy == 0 && ratio is null) (nt, h) = (top, bottom - top);
        SetBox(nl, nt, nl + w, nt + h, round: Math.Abs(s.Angle) < 1e-9);
        Notify();
    }

    public void EndDrag() => _drag = TransformHandle.None;

    /// <summary>
    /// The Straighten tool: turns the image so the line drawn from (x0, y0) to (x1, y1) (frame coordinates) becomes
    /// level, or plumb when it is closer to vertical.
    /// </summary>
    public void Straighten(double x0, double y0, double x1, double y1)
    {
        if (Locked || Math.Abs(x1 - x0) + Math.Abs(y1 - y0) < 2) return;
        double line = Math.Atan2(y1 - y0, x1 - x0) * 180 / Math.PI;
        double target = Math.Round(line / 90) * 90;
        BeginDrag(TransformHandle.Rotate, x0, y0);
        RotateFromStart(_angle - (line - target));
        EndDrag();
    }

    /// <summary>Sets the angle (degrees) by turning the image from its state when the drag began about the box's center.</summary>
    private void RotateFromStart(double angle)
    {
        var s = _start;
        double cx = (s.Left + s.Right) / 2, cy = (s.Top + s.Bottom) / 2;
        double turn = (angle - s.Angle) * Math.PI / 180;
        _view = s.View.Then(Affine.Translation(-cx, -cy)).Then(Affine.Rotation(turn)).Then(Affine.Translation(cx, cy));
        _angle = Normalize(angle);
        if (Math.Abs(_angle) < 1e-9) _angle = 0;
        (_left, _top, _right, _bottom) = (s.Left, s.Top, s.Right, s.Bottom);
        // A box that fit inside the image keeps fitting: it shrinks (same shape, same center) rather than showing
        // empty corners, and grows back when the image is turned back.
        if (_startInside)
        {
            double k = Math.Min(1, MaxScaleInside(_view, s.Left, s.Top, s.Right, s.Bottom));
            double hw = (s.Right - s.Left) / 2 * k, hh = (s.Bottom - s.Top) / 2 * k;
            (_left, _top, _right, _bottom) = (cx - hw, cy - hh, cx + hw, cy + hh);
        }
        if (_angle == 0)
        {
            // Back to upright: snap the image and box to whole pixels again.
            _view = new Affine(1, 0, 0, 1, Math.Round(_view.Dx), Math.Round(_view.Dy));
            SetBox(Math.Ceiling(_left - 1e-6), Math.Ceiling(_top - 1e-6), Math.Floor(_right + 1e-6), Math.Floor(_bottom + 1e-6), round: false);
        }
        Notify();
    }

    /// <summary>
    /// The largest factor the box (scaled about its center) can take while staying inside the image seen through
    /// <paramref name="view"/>; infinite when it cannot leave it, 0 when its center is outside.
    /// </summary>
    private double MaxScaleInside(Affine view, double left, double top, double right, double bottom)
    {
        (double X, double Y)[] quad =
        [
            view.Apply(0, 0), view.Apply(_docWidth, 0), view.Apply(_docWidth, _docHeight), view.Apply(0, _docHeight),
        ];
        double cx = (left + right) / 2, cy = (top + bottom) / 2, hw = (right - left) / 2, hh = (bottom - top) / 2;
        // The view is rigid and keeps orientation, so the corners run clockwise on screen (y down).
        double best = double.PositiveInfinity;
        for (int i = 0; i < 4; i++)
        {
            var (px, py) = quad[i];
            var (qx, qy) = quad[(i + 1) % 4];
            double nx = qy - py, ny = -(qx - px); // outward normal for a clockwise (y-down) polygon
            double len = Math.Sqrt(nx * nx + ny * ny);
            nx /= len;
            ny /= len;
            double room = nx * (px - cx) + ny * (py - cy); // distance from the center to the edge, inward positive
            if (room < -1e-9) return 0;
            double reach = Math.Abs(nx) * hw + Math.Abs(ny) * hh; // farthest box corner along the normal at scale 1
            if (reach > 1e-12) best = Math.Min(best, room / reach);
        }
        return best;
    }

    private void SetBox(double left, double top, double right, double bottom, bool round)
    {
        if (round)
        {
            (left, top) = (Math.Round(left), Math.Round(top));
            (right, bottom) = (Math.Max(left + 1, Math.Round(right)), Math.Max(top + 1, Math.Round(bottom)));
        }
        (_left, _top, _right, _bottom) = (left, top, Math.Max(right, left + 1e-3), Math.Max(bottom, top + 1e-3));
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Angle));
        OnPropertyChanged(nameof(IsModified));
        Changed?.Invoke();
    }

    private static double Normalize(double angle)
    {
        angle %= 360;
        if (angle > 180) angle -= 360;
        if (angle <= -180) angle += 360;
        return angle;
    }

    private static (int X, int Y) Signs(TransformHandle h) => h switch
    {
        TransformHandle.TopLeft => (-1, -1),
        TransformHandle.Top => (0, -1),
        TransformHandle.TopRight => (1, -1),
        TransformHandle.Right => (1, 0),
        TransformHandle.BottomRight => (1, 1),
        TransformHandle.Bottom => (0, 1),
        TransformHandle.BottomLeft => (-1, 1),
        TransformHandle.Left => (-1, 0),
        _ => (0, 0),
    };
}
