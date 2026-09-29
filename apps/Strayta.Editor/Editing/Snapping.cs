namespace Strayta.Editor.Editing;

/// <summary>What View › Snap To lets drags snap to (Photoshop's list; Slices have no counterpart here).</summary>
[Flags]
public enum SnapTargets
{
    None = 0,
    Guides = 1,
    Grid = 2,
    Layers = 4,
    DocumentBounds = 8,
    All = Guides | Grid | Layers | DocumentBounds,
}

/// <summary>What a coordinate snapped to, for feedback such as highlighting the line.</summary>
public enum SnapKind
{
    None,
    Guide,
    Grid,
    Layer,
    DocumentBounds,
}

/// <summary>
/// What a drag asks a snapper for: the zoom it starts at, whether the selected layer is the thing moving (so its own
/// edges are not targets), and a guide being dragged (-1: none), which must not snap to where it was.
/// </summary>
public readonly record struct SnapRequest(double Zoom, bool ExcludeSelectedLayer = false, int ExcludeGuide = -1);

/// <summary>One snapped axis: the new coordinate and what it landed on.</summary>
public readonly record struct SnapHit(double Value, SnapKind Kind)
{
    public bool Snapped => Kind != SnapKind.None;
}

/// <summary>
/// The shared snapping service (View › Snap, View › Snap To). A tool asks the document for one when a drag starts
/// (<c>DocumentViewModel.CreateSnapper(zoom)</c>, or <c>ImageCanvas.BeginSnap()</c> inside the canvas, which also
/// honors the Control key that turns snapping off for a drag, as in Photoshop) and then feeds it document coordinates:
/// <list type="bullet">
/// <item><see cref="SnapPoint"/> / <see cref="SnapX"/> / <see cref="SnapY"/> for a dragged point (marquee and crop
/// corners, transform handles, a text box corner, shape corners and anchor points);</item>
/// <item><see cref="SnapRect"/> for a moved box (the Move tool, Free Transform's box), which returns the offset that
/// lands the nearest of its edges or center on a target.</item>
/// </list>
/// Targets are gathered once per drag, so each call is a scan of a few dozen numbers. The distance is Photoshop's
/// 8 screen pixels, converted to document pixels at the zoom the drag started at. A snapper with nothing enabled
/// (<see cref="Off"/>) returns its input unchanged.
/// </summary>
public sealed class Snapper
{
    /// <summary>Photoshop snaps within this many screen pixels.</summary>
    public const double ScreenDistance = 8;

    private readonly (double Value, SnapKind Kind)[] _xs, _ys;
    private readonly double _gridStep;

    /// <param name="tolerance">Snap distance in document pixels.</param>
    /// <param name="xs">Vertical lines (x positions) to snap to.</param>
    /// <param name="ys">Horizontal lines (y positions) to snap to.</param>
    /// <param name="gridStep">Spacing of the grid's finest lines in document pixels (0: no grid), an infinite target set.</param>
    public Snapper(double tolerance, IEnumerable<(double Value, SnapKind Kind)> xs, IEnumerable<(double Value, SnapKind Kind)> ys, double gridStep = 0)
    {
        Tolerance = tolerance;
        _xs = [.. xs];
        _ys = [.. ys];
        _gridStep = gridStep > 0 && double.IsFinite(gridStep) ? gridStep : 0;
    }

    /// <summary>Snaps nothing.</summary>
    public static Snapper Off { get; } = new(0, [], [], 0);

    /// <summary>Snap distance in document pixels.</summary>
    public double Tolerance { get; }

    public bool IsActive => Tolerance > 0 && (_xs.Length > 0 || _ys.Length > 0 || _gridStep > 0);

    public SnapHit SnapX(double x) => Snap(x, _xs);

    public SnapHit SnapY(double y) => Snap(y, _ys);

    /// <summary>A dragged point, each axis snapped on its own.</summary>
    public (double X, double Y) SnapPoint(double x, double y) => (SnapX(x).Value, SnapY(y).Value);

    /// <summary>
    /// For a box being moved: the offset that puts whichever of its left edge, center or right edge (and top, middle,
    /// bottom) is nearest a target on it, or 0 on an axis where nothing is within reach.
    /// </summary>
    public (double Dx, double Dy) SnapRect(double left, double top, double right, double bottom) =>
        (Offset(_xs, left, (left + right) / 2, right), Offset(_ys, top, (top + bottom) / 2, bottom));

    private double Offset((double Value, SnapKind Kind)[] targets, double a, double b, double c)
    {
        double best = double.PositiveInfinity;
        foreach (double v in (ReadOnlySpan<double>)[a, b, c])
        {
            var hit = Snap(v, targets);
            if (hit.Snapped && Math.Abs(hit.Value - v) < Math.Abs(best)) best = hit.Value - v;
        }
        return double.IsFinite(best) ? best : 0;
    }

    private SnapHit Snap(double v, (double Value, SnapKind Kind)[] targets)
    {
        if (Tolerance <= 0) return new SnapHit(v, SnapKind.None);
        double bestDistance = Tolerance;
        var best = new SnapHit(v, SnapKind.None);
        foreach (var (value, kind) in targets)
        {
            double d = Math.Abs(value - v);
            // Guides win ties over other targets, as they are what someone placed on purpose.
            if (d < bestDistance || (d == bestDistance && kind == SnapKind.Guide && best.Snapped))
            {
                bestDistance = d;
                best = new SnapHit(value, kind);
            }
        }
        if (_gridStep > 0)
        {
            double line = Math.Round(v / _gridStep) * _gridStep;
            double d = Math.Abs(line - v);
            if (d < bestDistance) best = new SnapHit(line, SnapKind.Grid);
        }
        return best;
    }
}
