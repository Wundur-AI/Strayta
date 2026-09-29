using System.Buffers;
using System.Collections.Concurrent;

namespace Strayta.Core.Paths;

/// <summary>Which points a set of contours covers where they overlap or wind around more than once.</summary>
public enum FillRule
{
    /// <summary>Covered where the contours wind around the point at all (Photoshop's shapes and strokes).</summary>
    NonZero,

    /// <summary>Covered where an odd number of contours surround the point.</summary>
    EvenOdd,
}

/// <summary>
/// Closed polygons filled together with one rule, combined with what came before by <see cref="Operation"/>.
/// </summary>
public sealed record FillGroup(IReadOnlyList<IReadOnlyList<PathPoint>> Polygons, FillRule Rule, PathOperation Operation);

/// <summary>
/// Anti-aliased scanline rasterizer with exact area coverage. Each row accumulates, for every edge crossing it, the
/// signed area the edge leaves to its right within each pixel, and a running sum across the row turns that into
/// coverage (the signed-area accumulation of Raph Levien's font-rs, Apache-2.0/MIT, written anew here). Coverage is
/// exact wherever edges do not cross inside a pixel; winding numbers of overlapping contours add up, so the nonzero
/// rule is |sum| clamped to 1 and even-odd a triangle wave of it.
/// <para>
/// Groups are combined row by row in order (Photoshop's order of evaluation for path operations): the area starts
/// empty (or full for an inverted path), and each group is added (union: a + b − ab), subtracted (a·(1 − b)),
/// intersected (a·b) or excluded (a + b − 2ab). Only the columns a group touches are visited, so rows cost the width
/// of the shapes, not of the canvas.
/// </para>
/// </summary>
public static class PathRasterizer
{
    /// <summary>Default flattening tolerance in pixels: well below what 8- or 16-bit coverage can show.</summary>
    public const double Tolerance = 0.02;

    /// <summary>
    /// The groups a path fills. Subpaths are flattened (open ones closed, as Photoshop fills them). A run of subpaths
    /// that combine with the ones before them is filled together with the nonzero rule, each turned to wind the same
    /// way, which is their exact union (shapes that touch leave no seam). Photoshop's files from before CS6 mark
    /// every subpath -1 (read as combine) and fill them all together, where a hole winds the other way; see
    /// <see cref="IsLegacy"/>.
    /// </summary>
    public static List<FillGroup> Groups(VectorPath path, double tolerance = Tolerance)
    {
        var groups = new List<FillGroup>();
        List<IReadOnlyList<PathPoint>>? mergeable = null;
        foreach (var component in Components(path))
        {
            var polys = component.Subpaths.Select(s => Flatten(s, tolerance)).Where(p => p.Count >= 3).ToList();
            if (polys.Count == 0) continue;
            if (polys.Count == 1 && component.Operation == PathOperation.Combine)
            {
                // Single-contour components that add to the area are filled together, each turned to wind the same
                // way: that is their exact union, so shapes that touch leave no seam.
                var poly = polys[0];
                if (SignedArea(poly) < 0) poly.Reverse();
                if (mergeable is not null)
                {
                    mergeable.Add(poly);
                    continue;
                }
                mergeable = [poly];
                groups.Add(new FillGroup(mergeable, FillRule.NonZero, PathOperation.Combine));
                continue;
            }
            mergeable = null;
            groups.Add(new FillGroup([.. polys], ComponentRule, component.Operation));
        }
        return groups;
    }

    /// <summary>
    /// The rule a multi-contour component (a shape with holes, a character outline) is filled with. Photoshop's holes
    /// wind against their outline, which both rules fill alike; nonzero also keeps overlapping outlines of one
    /// component solid. (Measured: both give identical results on every multi-contour component in the test corpora.)
    /// </summary>
    private const FillRule ComponentRule = FillRule.NonZero;

    /// <summary>
    /// Splits a path into Photoshop's path components. A subpath whose length record carries an operation (0–3)
    /// starts a component; one marked -1 continues the component before it (the holes of a custom shape or a
    /// character outline). Files from before CS6 mark every subpath -1, making the whole path one component.
    /// </summary>
    public static List<(PathOperation Operation, List<Subpath> Subpaths)> Components(VectorPath path)
    {
        var list = new List<(PathOperation, List<Subpath>)>();
        foreach (var s in path.Subpaths)
        {
            if (list.Count > 0 && ContinuesComponent(s)) list[^1].Item2.Add(s);
            else list.Add((s.Operation, [s]));
        }
        return list;
    }

    /// <summary>True when the subpath's length record has no operation of its own (-1): it belongs to the component before it.</summary>
    public static bool ContinuesComponent(Subpath s) => s.RecordTail is { Length: >= 2 } t && t[0] == 0xFF && t[1] == 0xFF;

    /// <summary>True for paths from before Photoshop CS6, whose subpath records carry no operation at all.</summary>
    public static bool IsLegacy(VectorPath path) => path.Subpaths.Count > 0 && path.Subpaths.All(ContinuesComponent);

    /// <summary>The points of a subpath's outline, closed implicitly (the first point is not repeated).</summary>
    public static List<PathPoint> Flatten(Subpath s, double tolerance = Tolerance)
    {
        var points = new List<PathPoint>();
        if (s.Knots.Count == 0) return points;
        points.Add(s.Knots[0].Anchor);
        int n = s.Knots.Count;
        // Filling closes an open subpath with a straight line, so only its own segments are curves.
        foreach (var (p0, c0, c1, p1) in s.Segments()) Bezier.Flatten(p0, c0, c1, p1, tolerance, points);
        if (s.Closed && n > 1 && points.Count > 1 && points[^1] == points[0]) points.RemoveAt(points.Count - 1);
        return points;
    }

    public static double SignedArea(IReadOnlyList<PathPoint> poly)
    {
        double a = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            a += (poly[j].X * poly[i].Y) - (poly[i].X * poly[j].Y);
        return a / 2;
    }

    /// <summary>Coverage (0..255) of <paramref name="path"/> over <paramref name="area"/>, row-major.</summary>
    public static byte[] Rasterize(VectorPath path, PixelRect area, CancellationToken cancel = default)
    {
        var o = new byte[(long)area.Width * area.Height];
        Rasterize(path, area, o, cancel);
        return o;
    }

    /// <summary>
    /// Coverage of <paramref name="path"/> into <paramref name="output"/>. An initially filled path starts from
    /// everything and its components cut into it by their operations; one from before CS6 (no operations) is the
    /// inverse of its area.
    /// </summary>
    public static void Rasterize(VectorPath path, PixelRect area, byte[] output, CancellationToken cancel = default)
    {
        bool invert = path.InitialFillAll == true && IsLegacy(path);
        Rasterize(Groups(path), path.InitialFillAll == true && !invert, area, output, cancel);
        if (invert)
        {
            long n = (long)area.Width * area.Height;
            for (long i = 0; i < n; i++) output[i] = (byte)(255 - output[i]);
        }
    }

    /// <summary>The smallest area holding the path's coverage within <paramref name="clip"/> (the whole clip for an inverted path).</summary>
    public static PixelRect Bounds(VectorPath path, PixelRect clip)
    {
        if (path.InitialFillAll == true) return clip;
        if (path.CurveBounds() is not var (l, t, r, b)) return PixelRect.Empty;
        return new PixelRect((int)Math.Floor(l), (int)Math.Floor(t), (int)Math.Ceiling(r), (int)Math.Ceiling(b)).Intersect(clip);
    }

    /// <summary>
    /// Fills <paramref name="output"/> (row-major over <paramref name="area"/>, 0..255) with the coverage of
    /// <paramref name="groups"/>, starting from nothing or, with <paramref name="initialFill"/>, from everything.
    /// </summary>
    public static void Rasterize(IReadOnlyList<FillGroup> groups, bool initialFill, PixelRect area, byte[] output, CancellationToken cancel = default)
    {
        int w = area.Width, h = area.Height;
        if (w <= 0 || h <= 0) return;
        if (output.LongLength < (long)w * h) throw new ArgumentException("The output is smaller than the area.", nameof(output));
        var prepared = groups.Select(g => Prepare(g, area)).ToArray();
        byte init = initialFill ? (byte)255 : (byte)0;
        var partitions = Partitioner.Create(0, h, Math.Max(8, h / (Environment.ProcessorCount * 4)));
        Parallel.ForEach(partitions, new ParallelOptions { CancellationToken = cancel }, range =>
        {
            float[] acc = ArrayPool<float>.Shared.Rent(w + 2);
            float[] row = ArrayPool<float>.Shared.Rent(w);
            try
            {
                for (int y = range.Item1; y < range.Item2; y++)
                {
                    var outRow = output.AsSpan(y * w, w);
                    if (EmptyRow(prepared, y))
                    {
                        outRow.Fill(init);
                        continue;
                    }
                    var r = row.AsSpan(0, w);
                    r.Fill(initialFill ? 1f : 0f);
                    foreach (var p in prepared) p.Apply(y, r, acc);
                    for (int x = 0; x < w; x++) outRow[x] = (byte)(r[x] * 255f + 0.5f);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(acc);
                ArrayPool<float>.Shared.Return(row);
            }
        });
    }

    /// <summary>True when no group touches row <paramref name="y"/> and none would clear it (intersect).</summary>
    private static bool EmptyRow(Prepared[] prepared, int y)
    {
        foreach (var p in prepared)
            if (!p.Rows(y).IsEmpty || p.Operation == PathOperation.Intersect) return false;
        return true;
    }

    /// <summary>
    /// Float coverage (0..1) of one set of polygons over <paramref name="area"/>, for callers that combine it
    /// themselves (strokes clipped to a fill).
    /// </summary>
    public static float[] Coverage(IReadOnlyList<IReadOnlyList<PathPoint>> polygons, FillRule rule, PixelRect area, CancellationToken cancel = default)
    {
        int w = area.Width, h = area.Height;
        var o = new float[(long)Math.Max(0, w) * Math.Max(0, h)];
        if (w <= 0 || h <= 0) return o;
        var p = Prepare(new FillGroup(polygons, rule, PathOperation.Combine), area);
        var partitions = Partitioner.Create(0, h, Math.Max(8, h / (Environment.ProcessorCount * 4)));
        Parallel.ForEach(partitions, new ParallelOptions { CancellationToken = cancel }, range =>
        {
            float[] acc = ArrayPool<float>.Shared.Rent(w + 2);
            try
            {
                for (int y = range.Item1; y < range.Item2; y++) p.Apply(y, o.AsSpan(y * w, w), acc);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(acc);
            }
        });
        return o;
    }

    // ---- Edges ----------------------------------------------------------------------------------------

    private readonly record struct Edge(double X0, double Y0, double X1, double Y1);

    /// <summary>A group's edges in area coordinates, indexed by the rows they cross.</summary>
    private sealed class Prepared
    {
        public required Edge[] Edges { get; init; }
        public required int[] RowStart { get; init; }
        public required int[] RowEdges { get; init; }
        public required int Width { get; init; }
        public FillRule Rule { get; init; }
        public PathOperation Operation { get; init; }

        public ReadOnlySpan<int> Rows(int y) => RowEdges.AsSpan(RowStart[y], RowStart[y + 1] - RowStart[y]);

        /// <summary>Combines this group's coverage of row <paramref name="y"/> into <paramref name="row"/>.</summary>
        public void Apply(int y, Span<float> row, float[] acc)
        {
            var list = Rows(y);
            int w = Width;
            if (list.IsEmpty)
            {
                if (Operation == PathOperation.Intersect) row.Clear();
                return;
            }
            int lo = w, hi = 0;
            foreach (int i in list)
            {
                var e = Edges[i];
                int a = (int)Math.Floor(Math.Min(e.X0, e.X1)), b = (int)Math.Ceiling(Math.Max(e.X0, e.X1));
                lo = Math.Min(lo, a);
                hi = Math.Max(hi, b);
            }
            lo = Math.Clamp(lo, 0, w);
            hi = Math.Clamp(hi + 1, 0, w + 1);
            if (lo >= w)
            {
                if (Operation == PathOperation.Intersect) row.Clear();
                return;
            }
            acc.AsSpan(lo, hi - lo + 1).Clear();
            foreach (int i in list) Accumulate(Edges[i], y, acc, w);
            if (Operation == PathOperation.Intersect)
            {
                row[..lo].Clear();
                if (hi < w) row[hi..].Clear();
            }
            // The running sum is the coverage; one loop per rule and operation keeps the inner loops branch-free.
            int end = Math.Min(hi, w);
            var cov = acc.AsSpan(lo, end - lo);
            float sum = 0f;
            if (Rule == FillRule.NonZero)
                for (int i = 0; i < cov.Length; i++)
                {
                    sum += cov[i];
                    cov[i] = Math.Min(Math.Abs(sum), 1f);
                }
            else
                for (int i = 0; i < cov.Length; i++)
                {
                    sum += cov[i];
                    cov[i] = EvenOdd(Math.Abs(sum));
                }
            var r = row[lo..end];
            switch (Operation)
            {
                case PathOperation.Combine:
                    for (int i = 0; i < r.Length; i++) r[i] += cov[i] - r[i] * cov[i];
                    break;
                case PathOperation.Subtract:
                    for (int i = 0; i < r.Length; i++) r[i] *= 1f - cov[i];
                    break;
                case PathOperation.Intersect:
                    for (int i = 0; i < r.Length; i++) r[i] *= cov[i];
                    break;
                default:
                    for (int i = 0; i < r.Length; i++) r[i] += cov[i] - 2f * r[i] * cov[i];
                    break;
            }
        }

        private static float EvenOdd(float c)
        {
            c %= 2f;
            return c > 1f ? 2f - c : c;
        }
    }

    private static Prepared Prepare(FillGroup group, PixelRect area)
    {
        var edges = new List<Edge>();
        foreach (var poly in group.Polygons)
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[j];
                var b = poly[i];
                if (a.Y == b.Y) continue;
                edges.Add(new Edge(a.X - area.Left, a.Y - area.Top, b.X - area.Left, b.Y - area.Top));
            }
        int h = area.Height;
        var counts = new int[h + 1];
        var span = new (int From, int To)[edges.Count];
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            int from = Math.Max(0, (int)Math.Floor(Math.Min(e.Y0, e.Y1)));
            int to = Math.Min(h, (int)Math.Ceiling(Math.Max(e.Y0, e.Y1)));
            span[i] = (from, to);
            for (int y = from; y < to; y++) counts[y]++;
        }
        var start = new int[h + 1];
        for (int y = 0; y < h; y++) start[y + 1] = start[y] + counts[y];
        var rowEdges = new int[start[h]];
        var fill = (int[])start.Clone();
        for (int i = 0; i < edges.Count; i++)
            for (int y = span[i].From; y < span[i].To; y++) rowEdges[fill[y]++] = i;
        return new Prepared
        {
            Edges = [.. edges], RowStart = start, RowEdges = rowEdges, Width = area.Width, Rule = group.Rule, Operation = group.Operation,
        };
    }

    /// <summary>Adds the part of edge <paramref name="e"/> inside row <paramref name="y"/> to the row's accumulation.</summary>
    private static void Accumulate(Edge e, int y, float[] acc, int w)
    {
        double dir = e.Y1 > e.Y0 ? 1 : -1;
        var (x0, y0, x1, y1) = e.Y0 < e.Y1 ? (e.X0, e.Y0, e.X1, e.Y1) : (e.X1, e.Y1, e.X0, e.Y0);
        double top = Math.Max(y0, y), bottom = Math.Min(y1, y + 1);
        if (bottom <= top) return;
        double slope = (x1 - x0) / (y1 - y0);
        double xa = x0 + (top - y0) * slope, xb = x0 + (bottom - y0) * slope;
        double d = (bottom - top) * dir;
        // Split where the edge leaves the area sideways: left of it the edge covers the whole row to its right
        // (as a vertical edge at 0 would); right of it, nothing inside.
        if (xa > xb) (xa, xb) = (xb, xa); // the covered area only depends on the x range and the height
        if (xb <= 0)
        {
            acc[0] += (float)d;
            return;
        }
        if (xa >= w) return;
        if (xa < 0)
        {
            double part = d * (-xa) / (xb - xa);
            acc[0] += (float)part;
            d -= part;
            xa = 0;
        }
        if (xb > w)
        {
            d *= (w - xa) / (xb - xa);
            xb = w;
        }
        int ia = (int)xa;
        if (xb - ia <= 1 || xa == xb)
        {
            double xm = (xa + xb) / 2 - ia;
            acc[ia] += (float)(d * (1 - xm));
            acc[ia + 1] += (float)(d * xm);
            return;
        }
        // Across several columns: the height in each is proportional to the width it spans there.
        double k = d / (xb - xa);
        double from = xa;
        for (int col = ia; from < xb; col++)
        {
            double to = Math.Min(col + 1, xb);
            double dd = (to - from) * k;
            double xm = (from + to) / 2 - col;
            acc[col] += (float)(dd * (1 - xm));
            acc[col + 1] += (float)(dd * xm);
            from = to;
        }
    }
}
