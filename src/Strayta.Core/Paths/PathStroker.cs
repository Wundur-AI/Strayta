namespace Strayta.Core.Paths;

/// <summary>How the ends of open subpaths and dashes are drawn.</summary>
public enum LineCap
{
    Butt,
    Round,
    Square,
}

/// <summary>How a stroke turns at a corner anchor.</summary>
public enum LineJoin
{
    Miter,
    Round,
    Bevel,
}

/// <summary>Where a shape's stroke lies relative to its outline.</summary>
public enum StrokeAlignment
{
    Inside,
    Center,
    Outside,
}

/// <summary>Settings for turning a path into a stroke outline.</summary>
/// <param name="Width">Stroke width in pixels.</param>
/// <param name="MiterLimit">Longest miter as a multiple of the stroke width (SVG's rule); longer ones become bevels.</param>
/// <param name="Dashes">Dash and gap lengths as multiples of the width (Photoshop's dash set), or empty for a solid line.</param>
/// <param name="DashOffset">Where the pattern starts, in multiples of the width.</param>
public sealed record StrokeGeometry(double Width, LineCap Cap = LineCap.Butt, LineJoin Join = LineJoin.Miter, double MiterLimit = 4,
    IReadOnlyList<double>? Dashes = null, double DashOffset = 0);

/// <summary>
/// Turns paths into the polygons their stroke covers, for <see cref="PathRasterizer"/> with the nonzero rule. Each
/// straight piece of the flattened path becomes a quadrilateral; along curves neighbouring pieces share their ends
/// (offset along the averaged normal), so a curve's stroke has no seams or overlaps; at corner anchors the pieces meet
/// with a miter, round or bevel join, and open ends get the cap. Every polygon winds the same way, so their nonzero
/// union is the stroke.
/// </summary>
public static class PathStroker
{
    private readonly record struct Vertex(PathPoint P, bool Corner);

    /// <summary>Stroke outline polygons of <paramref name="path"/>.</summary>
    public static List<IReadOnlyList<PathPoint>> Outline(VectorPath path, StrokeGeometry g, double tolerance = PathRasterizer.Tolerance)
    {
        var output = new List<IReadOnlyList<PathPoint>>();
        if (g.Width <= 0) return output;
        foreach (var s in path.Subpaths)
        {
            var (points, closed) = Polyline(s, tolerance);
            if (points.Count == 0) continue;
            if (g.Dashes is { Count: > 0 } dashes && dashes.Any(d => d > 0))
                foreach (var piece in Dash(points, closed, dashes.Select(d => d * g.Width).ToArray(), g.DashOffset * g.Width))
                    StrokePolyline(piece, closed: false, g, tolerance, output);
            else StrokePolyline(points, closed, g, tolerance, output);
        }
        return output;
    }

    /// <summary>A subpath flattened, with its anchors marked as corners where the curve turns sharply there.</summary>
    private static (List<Vertex> Points, bool Closed) Polyline(Subpath s, double tolerance)
    {
        var list = new List<Vertex>();
        int n = s.Knots.Count;
        if (n == 0) return (list, s.Closed);
        list.Add(new Vertex(s.Knots[0].Anchor, IsCorner(s, 0)));
        int count = s.Closed ? n : n - 1;
        var buffer = new List<PathPoint>();
        for (int i = 0; i < count; i++)
        {
            var a = s.Knots[i];
            var b = s.Knots[(i + 1) % n];
            buffer.Clear();
            Bezier.Flatten(a.Anchor, a.Out, b.In, b.Anchor, tolerance, buffer);
            for (int j = 0; j < buffer.Count; j++)
            {
                bool end = j == buffer.Count - 1;
                list.Add(new Vertex(buffer[j], end && IsCorner(s, (i + 1) % n)));
            }
        }
        // Drop repeated points (retracted handles and coincident anchors give zero-length pieces).
        var clean = new List<Vertex>(list.Count);
        foreach (var v in list)
        {
            if (clean.Count > 0 && PathPoint.Distance(clean[^1].P, v.P) < 1e-9)
            {
                clean[^1] = clean[^1] with { Corner = clean[^1].Corner || v.Corner };
                continue;
            }
            clean.Add(v);
        }
        if (s.Closed && clean.Count > 1 && PathPoint.Distance(clean[0].P, clean[^1].P) < 1e-9)
        {
            clean[0] = clean[0] with { Corner = clean[0].Corner || clean[^1].Corner };
            clean.RemoveAt(clean.Count - 1);
        }
        return (clean, s.Closed);
    }

    /// <summary>
    /// An anchor is a corner unless its handles continue each other in a straight line (a smooth point); a corner
    /// between two straight segments is always one.
    /// </summary>
    private static bool IsCorner(Subpath s, int i)
    {
        var k = s.Knots[i];
        int n = s.Knots.Count;
        if (!s.Closed && (i == 0 || i == n - 1)) return true;
        var prev = s.Knots[(i - 1 + n) % n];
        var next = s.Knots[(i + 1) % n];
        var dIn = k.Anchor - (k.HasIn ? k.In : prev.HasOut ? prev.Out : prev.Anchor);
        var dOut = (k.HasOut ? k.Out : next.HasIn ? next.In : next.Anchor) - k.Anchor;
        double li = dIn.Length, lo = dOut.Length;
        if (li < 1e-9 || lo < 1e-9) return true;
        double cross = (dIn.X * dOut.Y - dIn.Y * dOut.X) / (li * lo);
        double dot = (dIn.X * dOut.X + dIn.Y * dOut.Y) / (li * lo);
        return !(dot > 0 && Math.Abs(cross) < 0.02);
    }

    /// <summary>Splits a polyline into dashes (lengths alternate dash, gap), keeping corner marks.</summary>
    private static IEnumerable<List<Vertex>> Dash(List<Vertex> points, bool closed, double[] pattern, double offset)
    {
        if (pattern.Length % 2 == 1) pattern = [.. pattern, .. pattern];
        double total = pattern.Sum();
        if (total <= 1e-9) yield break;
        var pts = closed ? [.. points, points[0]] : points;
        int index = 0;
        double left = pattern[0];
        double start = ((offset % total) + total) % total;
        while (start > 0)
        {
            if (start >= left)
            {
                start -= left;
                index = (index + 1) % pattern.Length;
                left = pattern[index];
            }
            else
            {
                left -= start;
                start = 0;
            }
        }
        List<Vertex>? current = index % 2 == 0 ? [pts[0]] : null;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i].P;
            var b = pts[i + 1].P;
            double len = PathPoint.Distance(a, b), at = 0;
            while (len - at > left)
            {
                at += left;
                var p = PathPoint.Lerp(a, b, at / len);
                if (current is not null)
                {
                    current.Add(new Vertex(p, true));
                    if (current.Count > 1) yield return current;
                    current = null;
                }
                else current = [new Vertex(p, true)];
                index = (index + 1) % pattern.Length;
                left = pattern[index];
            }
            left -= len - at;
            current?.Add(pts[i + 1]);
        }
        if (current is { Count: > 1 }) yield return current;
    }

    private static void StrokePolyline(List<Vertex> v, bool closed, StrokeGeometry g, double tolerance, List<IReadOnlyList<PathPoint>> output)
    {
        double hw = g.Width / 2;
        int n = v.Count;
        if (n == 1)
        {
            // A lone point shows only with round or square caps.
            if (g.Cap == LineCap.Round) output.Add(Circle(v[0].P, hw, tolerance));
            else if (g.Cap == LineCap.Square) output.Add(Oriented([new(v[0].P.X - hw, v[0].P.Y - hw), new(v[0].P.X + hw, v[0].P.Y - hw), new(v[0].P.X + hw, v[0].P.Y + hw), new(v[0].P.X - hw, v[0].P.Y + hw)]));
            return;
        }
        int segments = closed ? n : n - 1;
        var normals = new PathPoint[segments];
        var dirs = new PathPoint[segments];
        for (int i = 0; i < segments; i++)
        {
            var d = v[(i + 1) % n].P - v[i].P;
            double len = d.Length;
            dirs[i] = d * (1 / len);
            normals[i] = new PathPoint(-dirs[i].Y, dirs[i].X);
        }

        // Offsets at each segment's ends: shared (averaged) at smooth vertices, the segment's own normal at corners and ends.
        var startOffset = new PathPoint[segments];
        var endOffset = new PathPoint[segments];
        var smooth = new bool[n];
        for (int i = 0; i < n; i++)
        {
            bool interior = closed || (i > 0 && i < n - 1);
            if (!interior || v[i].Corner) continue;
            var nPrev = normals[(i - 1 + segments) % segments];
            var nNext = normals[i % segments];
            var sum = nPrev + nNext;
            double dot = nPrev.X * nNext.X + nPrev.Y * nNext.Y;
            if (dot < 0.5 || sum.Length < 1e-9) continue; // turns too sharply for a shared offset: join it
            smooth[i] = true;
        }
        for (int i = 0; i < segments; i++)
        {
            int a = i, b = (i + 1) % n;
            startOffset[i] = smooth[a] ? Miter(normals[(a - 1 + segments) % segments], normals[i]) * hw : normals[i] * hw;
            endOffset[i] = smooth[b] ? Miter(normals[i], normals[b % segments]) * hw : normals[i] * hw;
        }

        for (int i = 0; i < segments; i++)
        {
            var a = v[i].P;
            var b = v[(i + 1) % n].P;
            var sa = startOffset[i];
            var eb = endOffset[i];
            // A curve tighter than the stroke folds the inner offsets past each other; fall back to the plain quad
            // and a round join so it stays filled.
            double along = ((b - eb) - (a - sa)).X * dirs[i].X + ((b - eb) - (a - sa)).Y * dirs[i].Y;
            double alongOuter = ((b + eb) - (a + sa)).X * dirs[i].X + ((b + eb) - (a + sa)).Y * dirs[i].Y;
            if (along < 0 || alongOuter < 0)
            {
                sa = eb = normals[i] * hw;
                output.Add(Circle(a, hw, tolerance));
                output.Add(Circle(b, hw, tolerance));
            }
            output.Add(Oriented([a + sa, b + eb, b - eb, a - sa]));
        }

        // Joins at corners (and at vertices too sharp for a shared offset).
        for (int i = 0; i < n; i++)
        {
            bool interior = closed || (i > 0 && i < n - 1);
            if (!interior || smooth[i]) continue;
            var nPrev = normals[(i - 1 + segments) % segments];
            var nNext = normals[i % segments];
            var dPrev = dirs[(i - 1 + segments) % segments];
            var dNext = dirs[i % segments];
            double turn = dPrev.X * dNext.Y - dPrev.Y * dNext.X;
            if (Math.Abs(turn) < 1e-12 && dPrev.X * dNext.X + dPrev.Y * dNext.Y > 0) continue; // straight on
            var p = v[i].P;
            // The outer side of the turn is opposite to the direction it turns towards.
            double side = turn > 0 ? -1 : 1;
            var p1 = p + nPrev * (hw * side);
            var p2 = p + nNext * (hw * side);
            // A sharp turn inside a curve (not at an anchor) is rounded; anchors use the stroke's join.
            var join = v[i].Corner ? g.Join : LineJoin.Round;
            if (join == LineJoin.Miter)
            {
                double cosTheta = nPrev.X * nNext.X + nPrev.Y * nNext.Y; // angle between the normals
                double miterRatio = 1 / Math.Sqrt(Math.Max(1e-12, (1 + cosTheta) / 2)); // miter length / stroke width
                if (miterRatio > g.MiterLimit || cosTheta < -0.9999) join = LineJoin.Bevel;
                else
                {
                    var tip = p + Miter(nPrev, nNext) * (hw * side);
                    output.Add(Oriented([p, p1, tip, p2]));
                    continue;
                }
            }
            if (join == LineJoin.Round) output.Add(Circle(p, hw, tolerance));
            else output.Add(Oriented([p, p1, p2]));
        }

        if (!closed)
        {
            Cap(v[0].P, dirs[0] * -1, normals[0], hw, g.Cap, tolerance, output);
            Cap(v[n - 1].P, dirs[segments - 1], normals[segments - 1], hw, g.Cap, tolerance, output);
        }
    }

    private static void Cap(PathPoint p, PathPoint outward, PathPoint normal, double hw, LineCap cap, double tolerance, List<IReadOnlyList<PathPoint>> output)
    {
        switch (cap)
        {
            case LineCap.Round:
                output.Add(Circle(p, hw, tolerance));
                break;
            case LineCap.Square:
                var e = outward * hw;
                output.Add(Oriented([p + normal * hw, p + normal * hw + e, p - normal * hw + e, p - normal * hw]));
                break;
        }
    }

    /// <summary>The miter vector for two unit normals: along their bisector, long enough to reach both offset lines.</summary>
    private static PathPoint Miter(PathPoint n1, PathPoint n2)
    {
        var sum = n1 + n2;
        double len = sum.Length;
        if (len < 1e-9) return n1;
        var m = sum * (1 / len);
        double cos = m.X * n1.X + m.Y * n1.Y;
        return m * (1 / Math.Max(cos, 1e-3));
    }

    /// <summary>A circle as a polygon fine enough for <paramref name="tolerance"/>, wound positively.</summary>
    public static IReadOnlyList<PathPoint> Circle(PathPoint c, double r, double tolerance = PathRasterizer.Tolerance)
    {
        int n = r <= tolerance ? 8 : Math.Clamp((int)Math.Ceiling(Math.PI / Math.Acos(Math.Max(-1, 1 - tolerance / r))), 8, 1024);
        var pts = new PathPoint[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            pts[i] = new PathPoint(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }
        return pts;
    }

    /// <summary>The polygon wound positively (so all stroke pieces add up under the nonzero rule).</summary>
    private static IReadOnlyList<PathPoint> Oriented(PathPoint[] poly)
    {
        if (PathRasterizer.SignedArea(poly) < 0) Array.Reverse(poly);
        return poly;
    }
}
