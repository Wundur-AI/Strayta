using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>Parts of the Free Transform box a drag can start on.</summary>
public enum TransformHandle
{
    None,
    Move,
    Rotate,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

/// <summary>
/// The state of a Free Transform (⌘T): the layer's original box plus a scale, rotation and position, and the
/// Photoshop drag conventions for changing them. Corner drags scale proportionally (Shift frees them), side
/// drags stretch one axis (Shift keeps proportions), Alt/Option scales about the center, dragging inside moves
/// (Shift locks the axis) and dragging outside rotates about the center (Shift snaps to 15°).
/// Coordinates are document pixels; nothing here touches pixels.
/// </summary>
public sealed class FreeTransform : ObservableObject
{
    private readonly PixelRect _original;
    private double _centerX, _centerY, _scaleX = 1, _scaleY = 1, _angle;

    // Drag in progress: the handle and the state and point it started from.
    private TransformHandle _drag;
    private (double CenterX, double CenterY, double ScaleX, double ScaleY, double Angle) _start;
    private (double X, double Y) _startPoint;

    public FreeTransform(PixelRect original)
    {
        if (original.IsEmpty) throw new ArgumentException("Nothing to transform.", nameof(original));
        _original = original;
        _centerX = OriginalCenter.X;
        _centerY = OriginalCenter.Y;
    }

    /// <summary>Raised after any change, from a drag or from the options bar.</summary>
    public event Action? Changed;

    public PixelRect Original => _original;
    private (double X, double Y) OriginalCenter => ((_original.Left + _original.Right) / 2.0, (_original.Top + _original.Bottom) / 2.0);

    /// <summary>While true (the commit is being computed) drags and edits are ignored.</summary>
    public bool Locked { get; set; }

    // ---- Values shown in the options bar -----------------------------------------------------------

    /// <summary>Horizontal position of the box's center (Photoshop's reference point), in pixels.</summary>
    public double X { get => _centerX; set => Set(value, _centerY, _scaleX, _scaleY, _angle); }

    public double Y { get => _centerY; set => Set(_centerX, value, _scaleX, _scaleY, _angle); }

    /// <summary>Width as a percentage of the original; negative when flipped.</summary>
    public double WidthPercent { get => _scaleX * 100; set => Set(_centerX, _centerY, NonZero(value / 100, _original.Width), _scaleY, _angle); }

    public double HeightPercent { get => _scaleY * 100; set => Set(_centerX, _centerY, _scaleX, NonZero(value / 100, _original.Height), _angle); }

    /// <summary>Rotation in degrees, clockwise, in (-180, 180].</summary>
    public double Angle { get => _angle; set => Set(_centerX, _centerY, _scaleX, _scaleY, value); }

    /// <summary>True when the transform would leave the pixels exactly as they are.</summary>
    public bool IsIdentity => Matrix.IsIntegerTranslation(out int dx, out int dy) && dx == 0 && dy == 0;

    /// <summary>
    /// Document-space map from the original pixels to their transformed place. A pure move snaps to whole
    /// pixels, like the Move tool, so it never blurs the layer.
    /// </summary>
    public Affine Matrix
    {
        get
        {
            var (ox, oy) = OriginalCenter;
            var m = Affine.Translation(-ox, -oy)
                .Then(Affine.Scale(_scaleX, _scaleY))
                .Then(Affine.Rotation(_angle * Math.PI / 180))
                .Then(Affine.Translation(_centerX, _centerY));
            bool linearIdentity = _scaleX == 1 && _scaleY == 1 && _angle == 0;
            return linearIdentity ? m with { Dx = Math.Round(m.Dx), Dy = Math.Round(m.Dy) } : m;
        }
    }

    /// <summary>The box's corners in document space: top-left, top-right, bottom-right, bottom-left (before flips).</summary>
    public (double X, double Y)[] Corners
    {
        get
        {
            var m = Matrix;
            return
            [
                m.Apply(_original.Left, _original.Top), m.Apply(_original.Right, _original.Top),
                m.Apply(_original.Right, _original.Bottom), m.Apply(_original.Left, _original.Bottom),
            ];
        }
    }

    /// <summary>The center of the box in document space.</summary>
    public (double X, double Y) Center => (_centerX, _centerY);

    public static readonly TransformHandle[] ScaleHandles =
    [
        TransformHandle.TopLeft, TransformHandle.Top, TransformHandle.TopRight, TransformHandle.Right,
        TransformHandle.BottomRight, TransformHandle.Bottom, TransformHandle.BottomLeft, TransformHandle.Left,
    ];

    /// <summary>Where a scale handle sits, in document space.</summary>
    public (double X, double Y) HandlePosition(TransformHandle handle)
    {
        var (ix, iy) = Signs(handle);
        var (ox, oy) = OriginalCenter;
        return Matrix.Apply(ox + ix * _original.Width / 2.0, oy + iy * _original.Height / 2.0);
    }

    /// <summary>What a press at (x, y) grabs: a handle within <paramref name="tolerance"/> pixels, the inside, or the outside.</summary>
    public TransformHandle HitTest(double x, double y, double tolerance)
    {
        TransformHandle best = TransformHandle.None;
        double bestDist = tolerance;
        foreach (var h in ScaleHandles)
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
        var (lx, ly) = ToLocal(x, y, _centerX, _centerY, _angle);
        bool inside = Math.Abs(lx) <= Math.Abs(_scaleX * _original.Width / 2) && Math.Abs(ly) <= Math.Abs(_scaleY * _original.Height / 2);
        return inside ? TransformHandle.Move : TransformHandle.Rotate;
    }

    public void BeginDrag(TransformHandle handle, double x, double y)
    {
        if (Locked) return;
        _drag = handle;
        _start = (_centerX, _centerY, _scaleX, _scaleY, _angle);
        _startPoint = (x, y);
    }

    public void DragTo(double x, double y, bool shift, bool alt)
    {
        if (Locked) return;
        var (cx, cy, sx, sy, angle) = _start;
        switch (_drag)
        {
            case TransformHandle.None:
                return;
            case TransformHandle.Move:
                double dx = x - _startPoint.X, dy = y - _startPoint.Y;
                if (shift)
                {
                    if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
                    else dx = 0;
                }
                Set(cx + dx, cy + dy, sx, sy, angle);
                return;
            case TransformHandle.Rotate:
                double a0 = Math.Atan2(_startPoint.Y - cy, _startPoint.X - cx), a1 = Math.Atan2(y - cy, x - cx);
                double turned = angle + (a1 - a0) * 180 / Math.PI;
                if (shift) turned = Math.Round(turned / 15) * 15;
                Set(cx, cy, sx, sy, turned);
                return;
        }

        // Scaling happens in the box's own (rotated) frame, measured from the fixed anchor: the opposite
        // handle, or the center with Alt.
        var (ix, iy) = Signs(_drag);
        double w = _original.Width, h = _original.Height;
        var (qx, qy) = ToLocal(x, y, cx, cy, angle);
        double hx = ix * sx * w / 2, hy = iy * sy * h / 2;
        double ax = alt ? 0 : -hx, ay = alt ? 0 : -hy;
        double rx = 1, ry = 1;
        bool corner = ix != 0 && iy != 0;
        bool proportional = corner ? !shift : shift;
        if (corner && proportional)
        {
            // Project the pointer onto the anchor→handle diagonal so both axes scale by the same factor.
            double vx = hx - ax, vy = hy - ay;
            rx = ry = ((qx - ax) * vx + (qy - ay) * vy) / (vx * vx + vy * vy);
        }
        else
        {
            if (ix != 0) rx = (qx - ax) / (hx - ax);
            if (iy != 0) ry = (qy - ay) / (hy - ay);
            if (proportional) (rx, ry) = ix != 0 ? (rx, Math.Abs(rx)) : (Math.Abs(ry), ry);
        }
        double nsx = NonZero(sx * rx, w), nsy = NonZero(sy * ry, h);
        // The anchor stays put: the new center is halfway between it and the moved handle.
        double lcx = alt || ix == 0 ? 0 : ax + (nsx / sx) * (hx - ax) / 2;
        double lcy = alt || iy == 0 ? 0 : ay + (nsy / sy) * (hy - ay) / 2;
        var (wx, wy) = FromLocal(lcx, lcy, cx, cy, angle);
        Set(wx, wy, nsx, nsy, angle);
    }

    public void EndDrag() => _drag = TransformHandle.None;

    public bool IsDragging => _drag != TransformHandle.None;

    /// <summary>Nudges the box by whole pixels (arrow keys).</summary>
    public void Nudge(double dx, double dy) => Set(_centerX + dx, _centerY + dy, _scaleX, _scaleY, _angle);

    private void Set(double cx, double cy, double sx, double sy, double angle)
    {
        if (Locked || !double.IsFinite(cx + cy + sx + sy + angle)) return;
        angle %= 360;
        if (angle > 180) angle -= 360;
        if (angle <= -180) angle += 360;
        if (cx == _centerX && cy == _centerY && sx == _scaleX && sy == _scaleY && angle == _angle) return;
        (_centerX, _centerY, _scaleX, _scaleY, _angle) = (cx, cy, sx, sy, angle);
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(WidthPercent));
        OnPropertyChanged(nameof(HeightPercent));
        OnPropertyChanged(nameof(Angle));
        Changed?.Invoke();
    }

    /// <summary>Keeps a scale from collapsing the layer below one pixel (which could not be inverted).</summary>
    private static double NonZero(double scale, double size)
    {
        double min = 1 / Math.Max(1, size);
        return Math.Abs(scale) >= min ? scale : (scale < 0 ? -min : min);
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

    private static (double X, double Y) ToLocal(double x, double y, double cx, double cy, double angle)
    {
        var (sin, cos) = Math.SinCos(-angle * Math.PI / 180);
        double dx = x - cx, dy = y - cy;
        return (cos * dx - sin * dy, sin * dx + cos * dy);
    }

    private static (double X, double Y) FromLocal(double lx, double ly, double cx, double cy, double angle)
    {
        var (sin, cos) = Math.SinCos(angle * Math.PI / 180);
        return (cx + cos * lx - sin * ly, cy + sin * lx + cos * ly);
    }
}
