using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>
/// The Perspective Crop tool's quadrilateral, in document pixels. Dragging on the image draws it as a box; then
/// each corner drags on its own (a side handle moves both of its corners, the inside moves the whole shape) until it
/// follows the edges of something that should come out rectangular. Committing maps it onto an upright canvas of
/// <see cref="ResultWidth"/>×<see cref="ResultHeight"/>. Corners are clockwise from the one that becomes top-left.
/// Only convex shapes can be straightened; a drag that would fold the shape stops where it still works.
/// </summary>
public sealed class PerspectiveCropBox : ObservableObject
{
    private (double X, double Y)[] _quad = [];

    // Drag in progress: what is dragged, the shape and point it started from.
    private int _drag = -1; // 0..3 corners, 4..7 sides (side i runs from corner i to i+1), 8 move, 9 drawing
    private (double X, double Y)[] _start = [];
    private (double X, double Y) _startPoint;

    public const int Move = 8, Draw = 9;

    /// <summary>Raised after every change, so the canvas redraws the overlay.</summary>
    public event Action? Changed;

    /// <summary>While true (the crop is being applied) drags are ignored.</summary>
    public bool Locked { get; set; }

    /// <summary>The corners, or empty before a shape is drawn.</summary>
    public IReadOnlyList<(double X, double Y)> Corners => _quad;

    public bool HasShape => _quad.Length == 4;

    public bool IsDragging => _drag >= 0;

    /// <summary>Size of the straightened canvas (the longer of each pair of opposite sides).</summary>
    public (int Width, int Height) ResultSize => HasShape ? CanvasOperations.PerspectiveSize(_quad) : (0, 0);

    public string SizeText => HasShape ? $"{ResultSize.Width} × {ResultSize.Height} px" : "Drag over the image, then line the corners up with its edges";

    /// <summary>Puts a shape (e.g. the whole canvas, or from a test) in place.</summary>
    public void SetCorners(IReadOnlyList<(double X, double Y)> corners)
    {
        if (Locked || corners.Count != 4 || !IsConvex(corners)) return;
        _quad = corners.ToArray();
        Notify();
    }

    /// <summary>Removes the shape (Esc).</summary>
    public void Clear()
    {
        if (Locked) return;
        _quad = [];
        _drag = -1;
        Notify();
    }

    /// <summary>What a press at (x, y) grabs: a corner (0..3) or side (4..7) handle within <paramref name="tolerance"/>, the inside (<see cref="Move"/>), or a new box (<see cref="Draw"/>).</summary>
    public int HitTest(double x, double y, double tolerance)
    {
        if (!HasShape) return Draw;
        int best = -1;
        double bestDist = tolerance;
        for (int i = 0; i < 8; i++)
        {
            var (hx, hy) = HandlePosition(i);
            double d = Math.Max(Math.Abs(hx - x), Math.Abs(hy - y));
            if (d <= bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        if (best >= 0) return best;
        return Contains(x, y) ? Move : Draw;
    }

    /// <summary>Where handle <paramref name="i"/> sits: corners 0..3, then the middles of the sides.</summary>
    public (double X, double Y) HandlePosition(int i)
    {
        if (i < 4) return _quad[i];
        var (a, b) = (_quad[i - 4], _quad[(i - 3) % 4]);
        return ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    }

    public void BeginDrag(int handle, double x, double y)
    {
        if (Locked) return;
        _drag = handle;
        _start = _quad.ToArray();
        _startPoint = (x, y);
    }

    public void DragTo(double x, double y)
    {
        if (Locked || _drag < 0) return;
        double dx = x - _startPoint.X, dy = y - _startPoint.Y;
        (double X, double Y)[] next;
        if (_drag == Draw)
        {
            double l = Math.Min(x, _startPoint.X), r = Math.Max(x, _startPoint.X), t = Math.Min(y, _startPoint.Y), b = Math.Max(y, _startPoint.Y);
            if (r - l < 1 || b - t < 1) return;
            next = [(l, t), (r, t), (r, b), (l, b)];
        }
        else
        {
            next = _start.ToArray();
            void Shift(int i) => next[i] = (_start[i].X + dx, _start[i].Y + dy);
            if (_drag < 4) Shift(_drag);
            else if (_drag < 8)
            {
                Shift(_drag - 4);
                Shift((_drag - 3) % 4);
            }
            else
                for (int i = 0; i < 4; i++) Shift(i);
        }
        if (!IsConvex(next)) return;
        _quad = next;
        Notify();
    }

    public void EndDrag() => _drag = -1;

    private bool Contains(double x, double y)
    {
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var (a, b) = (_quad[i], _quad[(i + 1) % 4]);
            double cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            int s = Math.Sign(cross);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    /// <summary>True for a quadrilateral whose corners all turn the same way (clockwise on screen), with some area.</summary>
    public static bool IsConvex(IReadOnlyList<(double X, double Y)> q)
    {
        for (int i = 0; i < 4; i++)
        {
            var (a, b, c) = (q[i], q[(i + 1) % 4], q[(i + 2) % 4]);
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (cross <= 1e-6) return false; // y points down: clockwise turns are positive
        }
        return true;
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(HasShape));
        Changed?.Invoke();
    }
}
