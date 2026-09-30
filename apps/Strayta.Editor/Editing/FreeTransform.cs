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
/// What handle drags do (Edit › Transform): Free Transform scales, rotates and moves, and with ⌘ distorts, skews or
/// puts in perspective; the other modes fix handle drags to one kind, as in Photoshop. Warp hands the box to a
/// <see cref="WarpTransform"/>.
/// </summary>
public enum TransformMode
{
    Free,
    Scale,
    Rotate,
    Skew,
    Distort,
    Perspective,
    Warp,
}

/// <summary>
/// The state of a Free Transform (⌘T): the layer's original box plus a scale, rotation and position, and the
/// Photoshop drag conventions for changing them. Corner drags scale proportionally (Shift frees them), side
/// drags stretch one axis (Shift keeps proportions), Alt/Option scales about the center, dragging inside moves
/// (Shift locks the axis) and dragging outside rotates about the center (Shift snaps to 15°).
/// <para>
/// Distorting (⌘-drag a corner, or the Distort mode), skewing (⌘⇧-drag a side, or Skew) and perspective (⌘⌥⇧-drag a
/// corner, or Perspective) move the box's corners freely: they are kept as a quadrilateral in the original box's own
/// coordinates, before the scale, rotation and position, so those keep working on a distorted box. The whole map is
/// then a projective one (<see cref="Map"/>), affine while the corners stay a parallelogram.
/// </para>
/// Coordinates are document pixels; nothing here touches pixels.
/// </summary>
public sealed class FreeTransform : ObservableObject
{
    private readonly PixelRect _original;
    private double _centerX, _centerY, _scaleX = 1, _scaleY = 1, _angle;
    private (double X, double Y)[]? _quad; // the corners (TL, TR, BR, BL) in original coordinates when distorted
    private TransformMode _mode;

    // Drag in progress: the handle and the state and point it started from.
    private TransformHandle _drag;
    private (double CenterX, double CenterY, double ScaleX, double ScaleY, double Angle) _start;
    private (double X, double Y)[]? _startQuad;
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

    /// <summary>
    /// False when the layers cannot be distorted or put in perspective without losing their live data (type, shapes):
    /// ⌘-drags then scale as usual. Skews stay allowed (they are affine).
    /// </summary>
    public bool AllowsPerspective { get; set; } = true;

    /// <summary>
    /// Content-Aware Scale (Edit › Content-Aware Scale): the box only scales and moves, and the layer is resized by
    /// seam carving rather than resampled.
    /// </summary>
    public bool ContentAware { get; init; }

    /// <summary>What handle drags do (Edit › Transform › Scale, Rotate, Skew, Distort, Perspective, Warp).</summary>
    public TransformMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWarping));
            OnPropertyChanged(nameof(ShowsBox));
            OnPropertyChanged(nameof(ModeName));
            OnPropertyChanged(nameof(Hint));
            Changed?.Invoke();
        }
    }

    /// <summary>True in Warp mode (the options bar shows the warp's settings).</summary>
    public bool IsWarping => _mode == TransformMode.Warp;

    /// <summary>True outside Warp mode (the options bar shows the box's values).</summary>
    public bool ShowsBox => !IsWarping;

    /// <summary>The options bar's title for the mode.</summary>
    public string ModeName => ContentAware ? "Content-Aware Scale" : _mode switch
    {
        TransformMode.Scale => "Scale",
        TransformMode.Rotate => "Rotate",
        TransformMode.Skew => "Skew",
        TransformMode.Distort => "Distort",
        TransformMode.Perspective => "Perspective",
        TransformMode.Warp => "Warp",
        _ => "Free Transform",
    };

    /// <summary>What drags do in the mode, for the options bar.</summary>
    public string Hint => ContentAware ? "Drag a handle to scale by content (Shift: free, Option: from center) · inside to move · Enter applies" : _mode switch
    {
        TransformMode.Scale => "Drag a handle to scale (Shift: free, Option: from center) · inside to move",
        TransformMode.Rotate => "Drag anywhere outside to rotate (Shift: 15° steps) · inside to move",
        TransformMode.Skew => "Drag a side to slant it (Option: both sides) · a corner slides along a side",
        TransformMode.Distort => "Drag a corner or side anywhere (Option: the opposite one mirrors it)",
        TransformMode.Perspective => "Drag a corner: its neighbor mirrors it, for perspective",
        _ => AllowsPerspective
            ? "Drag a corner to scale (Shift: free, Option: from center) · ⌘ distorts · ⌘⇧ side skews · ⌘⌥⇧ perspective · outside rotates"
            : "Drag a corner to scale (Shift: free, Option: from center) · ⌘⇧ side skews · outside to rotate · inside to move",
    };

    /// <summary>The warp, once Warp mode has been used (it stays when switching back to Free Transform).</summary>
    public WarpTransform? Warp
    {
        get => _warp;
        set => SetProperty(ref _warp, value);
    }

    private WarpTransform? _warp;

    /// <summary>The map from the warp's frame to the document (set by the document; the canvas draws and drags the mesh through it).</summary>
    public Func<Projective>? WarpFrame { get; set; }

    /// <summary>True when the warp has been changed (or started out bent, a smart object's or type's own warp).</summary>
    public bool HasWarp => Warp is not null && (WarpEdited || !Warp.IsIdentity);

    /// <summary>True once the warp has been changed in this transform.</summary>
    public bool WarpEdited { get; private set; }

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
    public bool IsIdentity => _quad is null && !HasWarp && Matrix.IsIntegerTranslation(out int dx, out int dy) && dx == 0 && dy == 0;

    /// <summary>True when the corners have been moved apart from scaling and turning (distort, skew, perspective).</summary>
    public bool IsDistorted => _quad is not null;

    /// <summary>
    /// Document-space map from the original pixels to their transformed place, without the distortion (the scale,
    /// rotation and position). A pure move snaps to whole pixels, like the Move tool, so it never blurs the layer.
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

    /// <summary>The whole map, distortion included, from original document positions to transformed ones.</summary>
    public Projective Map
    {
        get
        {
            var a = Projective.FromAffine(Matrix);
            if (_quad is null) return a;
            return Projective.Translation(-_original.Left, -_original.Top)
                .Then(Projective.RectToQuad(_original.Width, _original.Height, _quad))
                .Then(a);
        }
    }

    /// <summary>True when <see cref="Map"/> is affine (no perspective), so live layers can follow it exactly.</summary>
    public bool IsAffine => _quad is null || Map.IsAffine;

    /// <summary>The whole map as an affine one (exact when <see cref="IsAffine"/>).</summary>
    public Affine AffineMap => _quad is null ? Matrix : Map.ToAffine();

    /// <summary>The box's corners in document space: top-left, top-right, bottom-right, bottom-left (before flips).</summary>
    public (double X, double Y)[] Corners
    {
        get
        {
            var m = Matrix;
            var q = LocalCorners;
            return [m.Apply(q[0].X, q[0].Y), m.Apply(q[1].X, q[1].Y), m.Apply(q[2].X, q[2].Y), m.Apply(q[3].X, q[3].Y)];
        }
    }

    private (double X, double Y)[] LocalCorners => _quad ?? RectCorners;

    private (double X, double Y)[] RectCorners =>
        [(_original.Left, _original.Top), (_original.Right, _original.Top), (_original.Right, _original.Bottom), (_original.Left, _original.Bottom)];

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
        double x = ox + ix * _original.Width / 2.0, y = oy + iy * _original.Height / 2.0;
        return _quad is null ? Matrix.Apply(x, y) : Map.Apply(x, y);
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
        return Inside(Corners, x, y) ? TransformHandle.Move : TransformHandle.Rotate;
    }

    /// <summary>True when (x, y) is inside the quadrilateral (either winding).</summary>
    private static bool Inside((double X, double Y)[] q, double x, double y)
    {
        int positive = 0, negative = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = q[i];
            var b = q[(i + 1) % 4];
            double cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            if (cross > 0) positive++;
            else if (cross < 0) negative++;
        }
        return positive == 0 || negative == 0;
    }

    public void BeginDrag(TransformHandle handle, double x, double y)
    {
        if (Locked) return;
        _drag = handle;
        _start = (_centerX, _centerY, _scaleX, _scaleY, _angle);
        _startQuad = _quad is null ? null : [.. _quad];
        _startPoint = (x, y);
    }

    /// <summary>What a drag of <paramref name="handle"/> does in the current mode with these modifiers.</summary>
    public DragKind KindOf(TransformHandle handle, bool shift, bool alt, bool command)
    {
        bool corner = handle is TransformHandle.TopLeft or TransformHandle.TopRight or TransformHandle.BottomRight or TransformHandle.BottomLeft;
        bool side = handle is TransformHandle.Top or TransformHandle.Right or TransformHandle.Bottom or TransformHandle.Left;
        var kind = handle switch
        {
            TransformHandle.None => DragKind.None,
            TransformHandle.Move => _mode == TransformMode.Warp ? DragKind.None : DragKind.Move,
            TransformHandle.Rotate => _mode is TransformMode.Free or TransformMode.Rotate ? DragKind.Rotate : DragKind.None,
            _ => _mode switch
            {
                TransformMode.Free when command && corner && alt && shift => DragKind.Perspective,
                TransformMode.Free when command && corner => DragKind.Distort,
                TransformMode.Free when command && side && shift => DragKind.SkewSide,
                TransformMode.Free when command && side => DragKind.DistortSide,
                TransformMode.Free or TransformMode.Scale => DragKind.Scale,
                TransformMode.Rotate => DragKind.Rotate,
                TransformMode.Skew => corner ? DragKind.SkewCorner : DragKind.SkewSide,
                TransformMode.Distort => corner ? DragKind.Distort : DragKind.DistortSide,
                TransformMode.Perspective => corner ? DragKind.Perspective : DragKind.SkewSide,
                _ => DragKind.None,
            },
        };
        // Layers that cannot take a perspective (type, shapes) scale instead of distorting; skews are affine and stay.
        if (!AllowsPerspective && kind is DragKind.Distort or DragKind.DistortSide or DragKind.Perspective)
            kind = _mode == TransformMode.Free ? DragKind.Scale : DragKind.None;
        // Content-Aware Scale neither turns nor distorts.
        if (ContentAware && kind is not (DragKind.Move or DragKind.Scale or DragKind.None))
            kind = handle is TransformHandle.Rotate ? DragKind.None : DragKind.Scale;
        return kind;
    }

    public void DragTo(double x, double y, bool shift, bool alt, bool command = false)
    {
        if (Locked) return;
        var (cx, cy, sx, sy, angle) = _start;
        var kind = KindOf(_drag, shift, alt, command);
        // Every drag works from where it started, so changing modifiers mid-drag switches cleanly.
        _quad = _startQuad is null ? null : [.. _startQuad];
        switch (kind)
        {
            case DragKind.None:
                Set(cx, cy, sx, sy, angle, force: true);
                return;
            case DragKind.Move:
                double dx = x - _startPoint.X, dy = y - _startPoint.Y;
                if (shift)
                {
                    if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
                    else dx = 0;
                }
                Set(cx + dx, cy + dy, sx, sy, angle, force: true);
                return;
            case DragKind.Rotate:
                double a0 = Math.Atan2(_startPoint.Y - cy, _startPoint.X - cx), a1 = Math.Atan2(y - cy, x - cx);
                double turned = angle + (a1 - a0) * 180 / Math.PI;
                if (shift) turned = Math.Round(turned / 15) * 15;
                Set(cx, cy, sx, sy, turned, force: true);
                return;
            case DragKind.Scale:
                break;
            default:
                Set(cx, cy, sx, sy, angle, force: true);
                Distort(kind, x, y, alt);
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
        Set(wx, wy, nsx, nsy, angle, force: true);
    }

    /// <summary>
    /// Moves corners of the (start) quadrilateral for a distort, skew or perspective drag. The pointer's movement is
    /// taken back into the original box's coordinates (before scale and rotation), where the corners live. A result
    /// that would fold the box (not convex) is refused, as Photoshop refuses it.
    /// </summary>
    private void Distort(DragKind kind, double x, double y, bool alt)
    {
        var inv = Matrix.Invert();
        var (px, py) = inv.Apply(x, y);
        var (sx, sy) = inv.Apply(_startPoint.X, _startPoint.Y);
        double dx = px - sx, dy = py - sy;
        var q = _startQuad is null ? RectCorners : [.. _startQuad];
        int c = CornerIndex(_drag);
        var (e0, e1) = SideCorners(_drag);
        switch (kind)
        {
            case DragKind.Distort when c >= 0:
                q[c] = (q[c].X + dx, q[c].Y + dy);
                if (alt) q[(c + 2) % 4] = (q[(c + 2) % 4].X - dx, q[(c + 2) % 4].Y - dy);
                break;
            case DragKind.DistortSide when e0 >= 0:
                q[e0] = (q[e0].X + dx, q[e0].Y + dy);
                q[e1] = (q[e1].X + dx, q[e1].Y + dy);
                if (alt)
                {
                    q[(e0 + 2) % 4] = (q[(e0 + 2) % 4].X - dx, q[(e0 + 2) % 4].Y - dy);
                    q[(e1 + 2) % 4] = (q[(e1 + 2) % 4].X - dx, q[(e1 + 2) % 4].Y - dy);
                }
                break;
            case DragKind.SkewSide when e0 >= 0:
            {
                // The side slides along itself.
                var (ux, uy) = Unit(q[e1].X - q[e0].X, q[e1].Y - q[e0].Y);
                double t = dx * ux + dy * uy;
                q[e0] = (q[e0].X + t * ux, q[e0].Y + t * uy);
                q[e1] = (q[e1].X + t * ux, q[e1].Y + t * uy);
                if (alt)
                {
                    q[(e0 + 2) % 4] = (q[(e0 + 2) % 4].X - t * ux, q[(e0 + 2) % 4].Y - t * uy);
                    q[(e1 + 2) % 4] = (q[(e1 + 2) % 4].X - t * ux, q[(e1 + 2) % 4].Y - t * uy);
                }
                break;
            }
            case DragKind.SkewCorner when c >= 0:
            {
                // The corner slides along whichever of its sides the pointer follows more.
                var (ax, ay) = Unit(q[(c + 1) % 4].X - q[c].X, q[(c + 1) % 4].Y - q[c].Y);
                var (bx, by) = Unit(q[(c + 3) % 4].X - q[c].X, q[(c + 3) % 4].Y - q[c].Y);
                double ta = dx * ax + dy * ay, tb = dx * bx + dy * by;
                q[c] = Math.Abs(ta) >= Math.Abs(tb) ? (q[c].X + ta * ax, q[c].Y + ta * ay) : (q[c].X + tb * bx, q[c].Y + tb * by);
                break;
            }
            case DragKind.Perspective when c >= 0:
            {
                // The corner slides along a side and its neighbor on that side mirrors it, keeping a symmetric trapezoid.
                int na = (c + 1) % 4, nb = (c + 3) % 4;
                var (ax, ay) = Unit(q[na].X - q[c].X, q[na].Y - q[c].Y);
                var (bx, by) = Unit(q[nb].X - q[c].X, q[nb].Y - q[c].Y);
                double ta = dx * ax + dy * ay, tb = dx * bx + dy * by;
                if (Math.Abs(ta) >= Math.Abs(tb))
                {
                    q[c] = (q[c].X + ta * ax, q[c].Y + ta * ay);
                    q[na] = (q[na].X - ta * ax, q[na].Y - ta * ay);
                }
                else
                {
                    q[c] = (q[c].X + tb * bx, q[c].Y + tb * by);
                    q[nb] = (q[nb].X - tb * bx, q[nb].Y - tb * by);
                }
                break;
            }
            default:
                return;
        }
        if (!IsConvex(q)) return;
        SetQuad(q);
    }

    /// <summary>Sets the distortion: the corners (TL, TR, BR, BL) in original coordinates, before scale and rotation.</summary>
    private void SetQuad((double X, double Y)[] q)
    {
        var rect = RectCorners;
        bool isRect = q.Zip(rect).All(p => Math.Abs(p.First.X - p.Second.X) < 1e-9 && Math.Abs(p.First.Y - p.Second.Y) < 1e-9);
        _quad = isRect ? null : q;
        OnPropertyChanged(nameof(IsDistorted));
        Changed?.Invoke();
    }

    /// <summary>Moves the box's corners to <paramref name="corners"/> (document space; TL, TR, BR, BL), keeping scale and rotation.</summary>
    public bool SetCorners(IReadOnlyList<(double X, double Y)> corners)
    {
        if (Locked || corners.Count != 4) return false;
        var inv = Matrix.Invert();
        var q = corners.Select(p => inv.Apply(p.X, p.Y)).ToArray();
        if (!IsConvex(q)) return false;
        SetQuad(q);
        return true;
    }

    /// <summary>True for a quadrilateral whose corners all turn the same way (so the projective map is defined on all of it).</summary>
    public static bool IsConvex(IReadOnlyList<(double X, double Y)> q)
    {
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = q[i];
            var b = q[(i + 1) % 4];
            var c = q[(i + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-9) return false;
            int s = Math.Sign(cross);
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        double l = Math.Sqrt(x * x + y * y);
        return l < 1e-12 ? (1, 0) : (x / l, y / l);
    }

    private static int CornerIndex(TransformHandle h) => h switch
    {
        TransformHandle.TopLeft => 0,
        TransformHandle.TopRight => 1,
        TransformHandle.BottomRight => 2,
        TransformHandle.BottomLeft => 3,
        _ => -1,
    };

    private static (int, int) SideCorners(TransformHandle h) => h switch
    {
        TransformHandle.Top => (0, 1),
        TransformHandle.Right => (1, 2),
        TransformHandle.Bottom => (2, 3),
        TransformHandle.Left => (3, 0),
        _ => (-1, -1),
    };

    public void EndDrag() => _drag = TransformHandle.None;

    public bool IsDragging => _drag != TransformHandle.None;

    /// <summary>Nudges the box by whole pixels (arrow keys).</summary>
    public void Nudge(double dx, double dy) => Set(_centerX + dx, _centerY + dy, _scaleX, _scaleY, _angle);

    // ---- Edit › Transform › Rotate and Flip --------------------------------------------------------

    /// <summary>Turns the box by <paramref name="degrees"/> (clockwise) about its center.</summary>
    public void Rotate(double degrees) => Set(_centerX, _centerY, _scaleX, _scaleY, _angle + degrees);

    /// <summary>Mirrors the box left to right about its center (in the document's horizontal).</summary>
    public void FlipHorizontal() => Set(_centerX, _centerY, -_scaleX, _scaleY, -_angle);

    /// <summary>Mirrors the box top to bottom about its center.</summary>
    public void FlipVertical() => Set(_centerX, _centerY, _scaleX, -_scaleY, -_angle);

    private void Set(double cx, double cy, double sx, double sy, double angle, bool force = false)
    {
        if (Locked || !double.IsFinite(cx + cy + sx + sy + angle)) return;
        angle %= 360;
        if (angle > 180) angle -= 360;
        if (angle <= -180) angle += 360;
        if (cx == _centerX && cy == _centerY && sx == _scaleX && sy == _scaleY && angle == _angle)
        {
            if (force) Changed?.Invoke(); // the distortion may have changed
            return;
        }
        (_centerX, _centerY, _scaleX, _scaleY, _angle) = (cx, cy, sx, sy, angle);
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(WidthPercent));
        OnPropertyChanged(nameof(HeightPercent));
        OnPropertyChanged(nameof(Angle));
        Changed?.Invoke();
    }

    /// <summary>Tells listeners the warp changed (it lives in <see cref="Warp"/>).</summary>
    internal void RaiseChanged()
    {
        WarpEdited = true;
        OnPropertyChanged(nameof(HasWarp));
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

/// <summary>What one Free Transform drag does.</summary>
public enum DragKind
{
    None,
    Move,
    Rotate,
    Scale,
    Distort,
    DistortSide,
    SkewSide,
    SkewCorner,
    Perspective,
}
