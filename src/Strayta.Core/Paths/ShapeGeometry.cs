namespace Strayta.Core.Paths;

/// <summary>Corner radii of a rounded rectangle, in pixels.</summary>
public readonly record struct CornerRadii(double TopLeft, double TopRight, double BottomRight, double BottomLeft)
{
    public static CornerRadii All(double r) => new(r, r, r, r);

    public bool IsZero => TopLeft <= 0 && TopRight <= 0 && BottomRight <= 0 && BottomLeft <= 0;

    public bool IsUniform => TopLeft == TopRight && TopRight == BottomRight && BottomRight == BottomLeft;
}

/// <summary>An arrowhead at an end of a line, sized relative to the line's weight (Photoshop's percentages).</summary>
/// <param name="Width">Width as a fraction of the weight (5 = 500%).</param>
/// <param name="Length">Length as a fraction of the weight (10 = 1000%).</param>
/// <param name="Concavity">How far the back is pushed in, -0.5..0.5 of the length.</param>
public sealed record ArrowHead(double Width = 5, double Length = 10, double Concavity = 0);

/// <summary>
/// The outlines Photoshop's shape tools draw, as it stores them: knots run clockwise on screen, rectangles start at the
/// top-left corner, ellipses at the top with linked knots whose handles are κ = 0.5523 of the radius (the standard
/// four-arc circle), rounded rectangles start at the top edge's left end, eight linked knots.
/// </summary>
public static class ShapeGeometry
{
    /// <summary>Handle length of a quarter circle as a fraction of its radius: 4/3·(√2 − 1).</summary>
    public const double Kappa = 0.55228474983079356;

    public static Subpath Rectangle(double left, double top, double right, double bottom, PathOperation op = PathOperation.Combine) =>
        new([PathKnot.Corner(left, top), PathKnot.Corner(right, top), PathKnot.Corner(right, bottom), PathKnot.Corner(left, bottom)], true, op);

    /// <summary>A rectangle with rounded corners; radii larger than half a side are reduced, as Photoshop does.</summary>
    public static Subpath RoundedRectangle(double left, double top, double right, double bottom, CornerRadii radii, PathOperation op = PathOperation.Combine)
    {
        if (radii.IsZero) return Rectangle(left, top, right, bottom, op);
        double w = right - left, h = bottom - top, limit = Math.Min(w, h) / 2;
        double tl = Math.Clamp(radii.TopLeft, 0, limit), tr = Math.Clamp(radii.TopRight, 0, limit);
        double br = Math.Clamp(radii.BottomRight, 0, limit), bl = Math.Clamp(radii.BottomLeft, 0, limit);
        var k = new List<PathKnot>();
        // Top edge, left to right; each corner is an arc between the two knots at its ends.
        Add(new(left + tl, top), new(-1, 0), tl, false);
        Add(new(right - tr, top), new(1, 0), tr, true);
        Add(new(right, top + tr), new(0, -1), tr, false);
        Add(new(right, bottom - br), new(0, 1), br, true);
        Add(new(right - br, bottom), new(1, 0), br, false);
        Add(new(left + bl, bottom), new(-1, 0), bl, true);
        Add(new(left, bottom - bl), new(0, 1), bl, false);
        Add(new(left, top + tl), new(0, -1), tl, true);
        // Corners with no radius put two knots on one point; keep one corner knot there.
        var merged = new List<PathKnot>();
        foreach (var knot in k)
        {
            if (merged.Count > 0 && merged[^1].Anchor == knot.Anchor)
            {
                merged[^1] = new PathKnot(merged[^1].In, knot.Anchor, knot.Out, false);
                continue;
            }
            merged.Add(knot);
        }
        if (merged.Count > 1 && merged[^1].Anchor == merged[0].Anchor)
        {
            merged[0] = new PathKnot(merged[^1].In, merged[0].Anchor, merged[0].Out, false);
            merged.RemoveAt(merged.Count - 1);
        }
        return new Subpath(merged, true, op);

        // A knot on an edge where a corner arc of radius r ends: its handle points towards the corner.
        void Add(PathPoint at, PathPoint towardsCorner, double r, bool outHandle)
        {
            var handle = at + towardsCorner * (r * Kappa);
            k.Add(outHandle ? new PathKnot(at, at, handle, r > 0) : new PathKnot(handle, at, at, r > 0));
        }
    }

    public static Subpath Ellipse(double left, double top, double right, double bottom, PathOperation op = PathOperation.Combine)
    {
        double cx = (left + right) / 2, cy = (top + bottom) / 2, rx = (right - left) / 2, ry = (bottom - top) / 2;
        double hx = rx * Kappa, hy = ry * Kappa;
        return new Subpath(
        [
            new(new(cx - hx, top), new(cx, top), new(cx + hx, top), true),
            new(new(right, cy - hy), new(right, cy), new(right, cy + hy), true),
            new(new(cx + hx, bottom), new(cx, bottom), new(cx - hx, bottom), true),
            new(new(left, cy + hy), new(left, cy), new(left, cy - hy), true),
        ], true, op);
    }

    /// <summary>
    /// A regular polygon or star filling the box: <paramref name="sides"/> points, the first at the top;
    /// <paramref name="starIndent"/> (0..0.99) pulls every other vertex towards the centre (Photoshop's "Star Ratio"
    /// is 1 − indent); <paramref name="cornerRadius"/> rounds the outer corners.
    /// </summary>
    public static Subpath Polygon(double left, double top, double right, double bottom, int sides, double starIndent = 0, double cornerRadius = 0,
        PathOperation op = PathOperation.Combine)
    {
        sides = Math.Clamp(sides, 3, 100);
        starIndent = Math.Clamp(starIndent, 0, 0.99);
        bool star = starIndent > 0;
        int count = star ? sides * 2 : sides;
        var unit = new PathPoint[count];
        for (int i = 0; i < count; i++)
        {
            double a = -Math.PI / 2 + 2 * Math.PI * i / count;
            double r = star && i % 2 == 1 ? 1 - starIndent : 1;
            unit[i] = new PathPoint(Math.Cos(a) * r, Math.Sin(a) * r);
        }
        // Fit the vertices' bounds to the box, as Photoshop's polygon tool draws into the dragged box.
        double minX = unit.Min(p => p.X), maxX = unit.Max(p => p.X), minY = unit.Min(p => p.Y), maxY = unit.Max(p => p.Y);
        var pts = unit.Select(p => new PathPoint(left + (p.X - minX) / (maxX - minX) * (right - left), top + (p.Y - minY) / (maxY - minY) * (bottom - top))).ToArray();
        return RoundCorners(pts, star ? i => i % 2 == 0 : _ => true, cornerRadius, op);
    }

    /// <summary>An isosceles triangle in the box, apex at the top centre (Photoshop's Triangle tool).</summary>
    public static Subpath Triangle(double left, double top, double right, double bottom, double cornerRadius = 0, PathOperation op = PathOperation.Combine) =>
        RoundCorners([new((left + right) / 2, top), new(right, bottom), new(left, bottom)], _ => true, cornerRadius, op);

    /// <summary>A closed polygon whose chosen corners are rounded with circular arcs of <paramref name="radius"/>.</summary>
    public static Subpath RoundCorners(IReadOnlyList<PathPoint> pts, Func<int, bool> round, double radius, PathOperation op = PathOperation.Combine)
    {
        int n = pts.Count;
        if (radius <= 0) return new Subpath(pts.Select(PathKnot.Corner).ToArray(), true, op);
        var knots = new List<PathKnot>();
        for (int i = 0; i < n; i++)
        {
            var p = pts[i];
            if (!round(i))
            {
                knots.Add(PathKnot.Corner(p));
                continue;
            }
            var prev = pts[(i - 1 + n) % n];
            var next = pts[(i + 1) % n];
            var d1 = prev - p;
            var d2 = next - p;
            double l1 = d1.Length, l2 = d2.Length;
            var u1 = d1 * (1 / l1);
            var u2 = d2 * (1 / l2);
            double cos = Math.Clamp(u1.X * u2.X + u1.Y * u2.Y, -1, 1);
            double angle = Math.Acos(cos); // interior angle at the corner
            // The tangent points of an arc of this radius inscribed in the corner, no further than half each side.
            double t = radius / Math.Tan(angle / 2);
            t = Math.Min(t, Math.Min(l1, l2) / 2);
            double r = t * Math.Tan(angle / 2);
            var a = p + u1 * t;
            var b = p + u2 * t;
            // Handle length for the arc's sweep (π − angle): 4/3·tan(sweep/4)·r.
            double h = 4.0 / 3 * Math.Tan((Math.PI - angle) / 4) * r;
            knots.Add(new PathKnot(a, a, a + (p - a) * (h / Math.Max(t, 1e-12)), false));
            knots.Add(new PathKnot(b + (p - b) * (h / Math.Max(t, 1e-12)), b, b, false));
        }
        return new Subpath(knots, true, op);
    }

    /// <summary>
    /// A straight line of <paramref name="weight"/> pixels from <paramref name="a"/> to <paramref name="b"/> as a filled
    /// outline (Photoshop's Line tool), with optional arrowheads that extend beyond the ends.
    /// </summary>
    public static Subpath Line(PathPoint a, PathPoint b, double weight, ArrowHead? start = null, ArrowHead? end = null, PathOperation op = PathOperation.Combine)
    {
        var d = b - a;
        double len = d.Length;
        if (len < 1e-9) d = new PathPoint(1, 0);
        else d *= 1 / len;
        var n = new PathPoint(-d.Y, d.X);
        double hw = Math.Max(weight, 0.01) / 2;
        var pts = new List<PathPoint>();
        // Around the outline: the left side from a to b, round the end (or its arrow), back along the right side and
        // round the start (or its arrow). An arrow's base sits `length` back from its tip; concavity pushes the base's
        // middle towards the tip.
        double StartBack(ArrowHead h) => h.Length * hw * 2;
        if (start is { } sa)
        {
            var baseA = a + d * StartBack(sa);
            pts.Add(baseA - d * (sa.Concavity * StartBack(sa)) + n * hw);
        }
        else pts.Add(a + n * hw);
        if (end is { } ea)
        {
            double l = ea.Length * hw * 2, w = ea.Width * hw;
            var baseB = b - d * l;
            pts.Add(baseB + d * (ea.Concavity * l) + n * hw);
            pts.Add(baseB + n * w);
            pts.Add(b);
            pts.Add(baseB - n * w);
            pts.Add(baseB + d * (ea.Concavity * l) - n * hw);
        }
        else
        {
            pts.Add(b + n * hw);
            pts.Add(b - n * hw);
        }
        if (start is { } sb)
        {
            double l = StartBack(sb), w = sb.Width * hw;
            var baseA = a + d * l;
            pts.Add(baseA - d * (sb.Concavity * l) - n * hw);
            pts.Add(baseA - n * w);
            pts.Add(a);
            pts.Add(baseA + n * w);
        }
        else pts.Add(a - n * hw);
        return new Subpath(pts.Select(PathKnot.Corner).ToArray(), true, op);
    }

    // ---- Custom shapes ------------------------------------------------------------------------------------

    /// <summary>The built-in custom shapes, by name.</summary>
    public static IReadOnlyList<string> CustomShapeNames { get; } = ["Heart", "Star", "Arrow", "Check Mark", "Speech Bubble", "Ring", "Lightning"];

    /// <summary>
    /// A built-in custom shape stretched to the box. Shapes with holes (the ring) carry them as further subpaths that
    /// continue the first one's component and wind the other way, as Photoshop stores custom shapes.
    /// </summary>
    public static IReadOnlyList<Subpath> CustomShape(string name, double left, double top, double right, double bottom, PathOperation op = PathOperation.Combine)
    {
        double w = right - left, h = bottom - top;
        PathPoint P(double x, double y) => new(left + x * w, top + y * h);
        Subpath Poly(params (double X, double Y)[] p) => new(p.Select(q => PathKnot.Corner(P(q.X, q.Y))).ToArray(), true, op);
        switch (name)
        {
            case "Heart":
                return
                [
                    new Subpath(
                    [
                        new(P(0.5, 0.26), P(0.5, 0.26), P(0.56, 0.08), false),
                        new(P(0.66, 0.0), P(0.77, 0.0), P(0.9, 0.0), true),
                        new(P(1.0, 0.16), P(1.0, 0.3), P(1.0, 0.5), true),
                        new(P(0.75, 0.72), P(0.5, 1.0), P(0.5, 1.0), false),
                        new(P(0.5, 1.0), P(0.5, 1.0), P(0.25, 0.72), false),
                        new(P(0.0, 0.5), P(0.0, 0.3), P(0.0, 0.16), true),
                        new(P(0.1, 0.0), P(0.23, 0.0), P(0.34, 0.0), true),
                        new(P(0.44, 0.08), P(0.5, 0.26), P(0.5, 0.26), false),
                    ], true, op),
                ];
            case "Star":
                return [Polygon(left, top, right, bottom, 5, 0.5, 0, op)];
            case "Arrow":
                return [Poly((0, 0.3), (0.6, 0.3), (0.6, 0), (1, 0.5), (0.6, 1), (0.6, 0.7), (0, 0.7))];
            case "Check Mark":
                return [Poly((0, 0.55), (0.14, 0.41), (0.36, 0.63), (0.86, 0.13), (1, 0.27), (0.36, 0.91))];
            case "Speech Bubble":
                var bubble = RoundedRectangle(left, top, right, top + h * 0.75, CornerRadii.All(Math.Min(w, h) * 0.12), op);
                var tail = new Subpath([PathKnot.Corner(P(0.2, 0.7)), PathKnot.Corner(P(0.42, 0.7)), PathKnot.Corner(P(0.16, 1.0))], true, PathOperation.Combine);
                return [bubble, tail];
            case "Ring":
                var outer = Ellipse(left, top, right, bottom, op);
                var inner = Ellipse(left + w * 0.25, top + h * 0.25, right - w * 0.25, bottom - h * 0.25);
                return [outer, Reverse(inner) with { RecordTail = ContinuationTail }];
            case "Lightning":
                return [Poly((0.55, 0), (0.15, 0.58), (0.45, 0.58), (0.3, 1), (0.85, 0.35), (0.55, 0.35), (0.75, 0))];
            default:
                throw new ArgumentException($"No custom shape named \"{name}\".", nameof(name));
        }
    }

    /// <summary>
    /// The length-record tail Photoshop gives a subpath that continues the component before it (operation -1).
    /// </summary>
    public static byte[] ContinuationTail => [0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>The same outline traversed the other way (handles swap sides).</summary>
    public static Subpath Reverse(Subpath s) =>
        s with { Knots = s.Knots.Reverse().Select(k => new PathKnot(k.Out, k.Anchor, k.In, k.Linked)).ToArray() };
}
