namespace Strayta.Core.Paths;

/// <summary>A point in document pixel space (x right, y down).</summary>
public readonly record struct PathPoint(double X, double Y)
{
    public static PathPoint operator +(PathPoint a, PathPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static PathPoint operator -(PathPoint a, PathPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static PathPoint operator *(PathPoint a, double k) => new(a.X * k, a.Y * k);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public static double Distance(PathPoint a, PathPoint b) => (a - b).Length;

    public static PathPoint Lerp(PathPoint a, PathPoint b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}

/// <summary>
/// An anchor point with its two direction handles, as Photoshop's path knot records store them: the control point
/// the curve arrives through (<see cref="In"/>), the anchor, and the control point it leaves through
/// (<see cref="Out"/>). A handle equal to its anchor is retracted (a corner on that side). <see cref="Linked"/> is
/// Photoshop's "linked" knot (a smooth point: moving one handle turns the other to stay opposite).
/// </summary>
public readonly record struct PathKnot(PathPoint In, PathPoint Anchor, PathPoint Out, bool Linked)
{
    /// <summary>A corner point with both handles retracted.</summary>
    public static PathKnot Corner(double x, double y) => new(new(x, y), new(x, y), new(x, y), false);

    public static PathKnot Corner(PathPoint p) => new(p, p, p, false);

    /// <summary>A smooth point whose handles mirror each other about the anchor.</summary>
    public static PathKnot Smooth(PathPoint anchor, PathPoint outHandle) =>
        new(anchor * 2 - outHandle, anchor, outHandle, true);

    public bool HasIn => In != Anchor;
    public bool HasOut => Out != Anchor;

    public PathKnot Offset(double dx, double dy) =>
        new(new(In.X + dx, In.Y + dy), new(Anchor.X + dx, Anchor.Y + dy), new(Out.X + dx, Out.Y + dy), Linked);

    public PathKnot Map(Func<PathPoint, PathPoint> f) => new(f(In), f(Anchor), f(Out), Linked);
}

/// <summary>
/// How a path component combines with the ones before it (Photoshop's path operations; the values are those of the
/// PSD subpath length record).
/// </summary>
public enum PathOperation
{
    /// <summary>Exclude Overlapping Shapes (XOR).</summary>
    Exclude = 0,

    /// <summary>Combine Shapes (union).</summary>
    Combine = 1,

    /// <summary>Subtract Front Shape.</summary>
    Subtract = 2,

    /// <summary>Intersect Shape Areas.</summary>
    Intersect = 3,
}

/// <summary>
/// One subpath: a run of knots, closed back to the first or open. A subpath starts a path component and carries the
/// operation that combines it with everything before it, unless its record says it continues the component before
/// it (Photoshop stores -1 there, see <see cref="PathRasterizer.ContinuesComponent"/>): a custom shape with holes or a
/// character outline is one component of several subpaths, filled together with the nonzero rule, its holes winding
/// the other way.
/// </summary>
public sealed record Subpath(IReadOnlyList<PathKnot> Knots, bool Closed, PathOperation Operation = PathOperation.Combine)
{
    /// <summary>
    /// The bytes of the length record after the knot count (22 bytes in Photoshop's files: the operation, then fields
    /// whose meaning is not published, such as the index of the live shape this subpath came from). Kept so an
    /// unedited path is written back byte for byte; null for subpaths made here.
    /// </summary>
    public byte[]? RecordTail { get; init; }

    /// <summary>
    /// The live shape (index into the layer's origination list) this subpath belongs to, or -1. Several subpaths
    /// may share one (a custom shape, a line with an arrowhead).
    /// </summary>
    public int OriginIndex { get; init; } = -1;

    public Subpath WithKnots(IReadOnlyList<PathKnot> knots) => this with { Knots = knots };

    public Subpath Map(Func<PathPoint, PathPoint> f) => this with { Knots = Knots.Select(k => k.Map(f)).ToArray() };

    public Subpath Offset(double dx, double dy) => this with { Knots = Knots.Select(k => k.Offset(dx, dy)).ToArray() };

    /// <summary>Bezier segments in order: (start anchor, start's out handle, end's in handle, end anchor).</summary>
    public IEnumerable<(PathPoint P0, PathPoint C0, PathPoint C1, PathPoint P1)> Segments()
    {
        int n = Knots.Count;
        if (n == 0) yield break;
        int count = Closed ? n : n - 1;
        for (int i = 0; i < count; i++)
        {
            var a = Knots[i];
            var b = Knots[(i + 1) % n];
            yield return (a.Anchor, a.Out, b.In, b.Anchor);
        }
    }

    /// <summary>Bounds of the anchors and handles (the curve lies inside).</summary>
    public (double Left, double Top, double Right, double Bottom) ControlBounds()
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var k in Knots)
            foreach (var p in (ReadOnlySpan<PathPoint>)[k.In, k.Anchor, k.Out])
            {
                l = Math.Min(l, p.X); t = Math.Min(t, p.Y);
                r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
            }
        return (l, t, r, b);
    }
}

/// <summary>
/// A vector path in document pixels: what a vector mask, a shape layer's outline, a saved path or the work path
/// holds. Subpaths are evaluated in order, each combined with the result so far by its operation, starting from
/// nothing (or from everything when <see cref="InitialFillAll"/> is set: an inverted vector mask).
/// Immutable; edits return a new path.
/// </summary>
public sealed record VectorPath(IReadOnlyList<Subpath> Subpaths)
{
    public static VectorPath Empty { get; } = new([]);

    /// <summary>
    /// Photoshop's initial fill rule record: true when the area starts filled and subpaths cut into it (an inverted
    /// vector mask); false when it starts empty. Null when the file had no such record.
    /// </summary>
    public bool? InitialFillAll { get; init; }

    /// <summary>
    /// The clipboard record's bounds (top, left, bottom, right as fractions of the canvas) and resolution, kept for
    /// round trips; Photoshop writes it for paths it copied. Null when absent.
    /// </summary>
    public PathClipboard? Clipboard { get; init; }

    /// <summary>Order and kind of the non-knot records as they were read (for writing back unchanged).</summary>
    public string? RecordLayout { get; init; }

    public bool IsEmpty => Subpaths.All(s => s.Knots.Count == 0);

    public VectorPath Map(Func<PathPoint, PathPoint> f) => this with { Subpaths = Subpaths.Select(s => s.Map(f)).ToArray() };

    public VectorPath Offset(double dx, double dy) => this with { Subpaths = Subpaths.Select(s => s.Offset(dx, dy)).ToArray() };

    /// <summary>A path with <paramref name="subpaths"/> appended (e.g. a new shape drawn into the same layer).</summary>
    public VectorPath Append(IEnumerable<Subpath> subpaths) => this with { Subpaths = [.. Subpaths, .. subpaths] };

    /// <summary>Bounds of all anchors and handles, or null for an empty path.</summary>
    public (double Left, double Top, double Right, double Bottom)? ControlBounds()
    {
        (double, double, double, double)? r = null;
        foreach (var s in Subpaths)
        {
            if (s.Knots.Count == 0) continue;
            var b = s.ControlBounds();
            r = r is var (l, t, rr, bb) ? (Math.Min(l, b.Left), Math.Min(t, b.Top), Math.Max(rr, b.Right), Math.Max(bb, b.Bottom)) : b;
        }
        return r;
    }

    /// <summary>Exact bounds of the curves (extrema of each Bezier segment), or null for an empty path.</summary>
    public (double Left, double Top, double Right, double Bottom)? CurveBounds(IEnumerable<int>? subpaths = null)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        bool any = false;
        foreach (int si in subpaths ?? Enumerable.Range(0, Subpaths.Count))
        {
            var s = Subpaths[si];
            if (s.Knots.Count == 1)
            {
                var p = s.Knots[0].Anchor;
                Add(p);
                continue;
            }
            foreach (var (p0, c0, c1, p1) in s.Segments())
            {
                Add(p0);
                Add(p1);
                foreach (double tt in Bezier.Extrema(p0, c0, c1, p1)) Add(Bezier.At(p0, c0, c1, p1, tt));
            }
        }
        return any ? (l, t, r, b) : null;

        void Add(PathPoint p)
        {
            any = true;
            l = Math.Min(l, p.X); t = Math.Min(t, p.Y);
            r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
        }
    }
}

/// <summary>The clipboard record of a path: bounds as fractions of the canvas and a resolution (8.24 fixed values).</summary>
public sealed record PathClipboard(double Top, double Left, double Bottom, double Right, double Resolution);

/// <summary>Cubic Bezier helpers.</summary>
public static class Bezier
{
    public static PathPoint At(PathPoint p0, PathPoint c0, PathPoint c1, PathPoint p1, double t)
    {
        double u = 1 - t;
        double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        return new(a * p0.X + b * c0.X + c * c1.X + d * p1.X, a * p0.Y + b * c0.Y + c * c1.Y + d * p1.Y);
    }

    /// <summary>Parameters in (0, 1) where x or y has a turning point.</summary>
    public static IEnumerable<double> Extrema(PathPoint p0, PathPoint c0, PathPoint c1, PathPoint p1)
    {
        foreach (var t in Roots(p0.X, c0.X, c1.X, p1.X)) yield return t;
        foreach (var t in Roots(p0.Y, c0.Y, c1.Y, p1.Y)) yield return t;

        static IEnumerable<double> Roots(double a, double b, double c, double d)
        {
            // The derivative over 3, (b-a)(1-t)² + 2(c-b)(1-t)t + (d-c)t², as qa·t² + qb·t + qc.
            double qa = -a + 3 * b - 3 * c + d, qb = 2 * (a - 2 * b + c), qc = b - a;
            if (Math.Abs(qa) < 1e-12)
            {
                if (Math.Abs(qb) > 1e-12)
                {
                    double t = -qc / qb;
                    if (t > 0 && t < 1) yield return t;
                }
                yield break;
            }
            double disc = qb * qb - 4 * qa * qc;
            if (disc < 0) yield break;
            double sq = Math.Sqrt(disc);
            double t1 = (-qb + sq) / (2 * qa), t2 = (-qb - sq) / (2 * qa);
            if (t1 > 0 && t1 < 1) yield return t1;
            if (t2 > 0 && t2 < 1) yield return t2;
        }
    }

    /// <summary>Splits a cubic at <paramref name="t"/> (de Casteljau).</summary>
    public static ((PathPoint, PathPoint, PathPoint, PathPoint) Left, (PathPoint, PathPoint, PathPoint, PathPoint) Right) Split(
        PathPoint p0, PathPoint c0, PathPoint c1, PathPoint p1, double t)
    {
        var a = PathPoint.Lerp(p0, c0, t);
        var b = PathPoint.Lerp(c0, c1, t);
        var c = PathPoint.Lerp(c1, p1, t);
        var d = PathPoint.Lerp(a, b, t);
        var e = PathPoint.Lerp(b, c, t);
        var m = PathPoint.Lerp(d, e, t);
        return ((p0, a, d, m), (m, e, c, p1));
    }

    /// <summary>
    /// Appends points approximating the cubic (excluding <paramref name="p0"/>, including <paramref name="p1"/>) to
    /// <paramref name="output"/>, within <paramref name="tolerance"/> pixels. The number of pieces follows from the
    /// control polygon's second differences (the classic bound for uniform subdivision), so it is cheap and never
    /// under-samples.
    /// </summary>
    public static void Flatten(PathPoint p0, PathPoint c0, PathPoint c1, PathPoint p1, double tolerance, List<PathPoint> output)
    {
        if (c0 == p0 && c1 == p1)
        {
            output.Add(p1);
            return;
        }
        double ddx = Math.Max(Math.Abs(p0.X - 2 * c0.X + c1.X), Math.Abs(c0.X - 2 * c1.X + p1.X));
        double ddy = Math.Max(Math.Abs(p0.Y - 2 * c0.Y + c1.Y), Math.Abs(c0.Y - 2 * c1.Y + p1.Y));
        double dd = Math.Sqrt(ddx * ddx + ddy * ddy);
        // Uniform subdivision into n pieces keeps the error below (3/4)·dd/n² (after Wang's formula for cubics).
        int n = (int)Math.Ceiling(Math.Sqrt(0.75 * dd / Math.Max(tolerance, 1e-4)));
        n = Math.Clamp(n, 1, 4096);
        for (int i = 1; i < n; i++) output.Add(At(p0, c0, c1, p1, (double)i / n));
        output.Add(p1);
    }
}

/// <summary>What a document-level path is: the temporary Work Path or a named, saved path.</summary>
public enum DocumentPathKind
{
    Work,
    Saved,
}

/// <summary>A path stored with the document (Photoshop's Paths panel: the Work Path and saved paths).</summary>
/// <param name="Id">The format's identifier (for PSD, the image resource ID: 1025 for the work path, 2000–2997 for saved ones).</param>
public sealed record DocumentPath(string Name, VectorPath Path, DocumentPathKind Kind, int Id = 0)
{
    /// <summary>The stored bytes this path was read from, so an unchanged path is written back as it was.</summary>
    public object? SourceData { get; init; }
}
