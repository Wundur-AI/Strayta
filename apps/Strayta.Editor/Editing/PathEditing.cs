using Strayta.Core.Paths;

namespace Strayta.Editor.Editing;

/// <summary>An anchor point of a path: subpath index and knot index.</summary>
public readonly record struct KnotRef(int Subpath, int Knot);

/// <summary>What a point on the canvas is over, for the pen and selection tools.</summary>
public enum PathPart
{
    None,
    Anchor,
    InHandle,
    OutHandle,
    Segment,
}

/// <summary>A hit on a path: the part, the knot (for a segment, the knot it starts from) and, on a segment, where along it.</summary>
public readonly record struct PathHit(PathPart Part, KnotRef Knot, double T = 0)
{
    public static PathHit None => new(PathPart.None, default);
}

/// <summary>
/// The path editing operations of the Pen, Add / Delete Anchor, Convert Point, Path Selection and Direct Selection
/// tools, on immutable <see cref="VectorPath"/>s (each returns a new path). Coordinates are document pixels.
/// </summary>
public static class PathEditing
{
    // ---- Hit testing -----------------------------------------------------------------------------------

    /// <summary>
    /// What is under <paramref name="p"/> within <paramref name="tolerance"/>: handles of the <paramref name="handlesOf"/>
    /// knots first (they are drawn on top), then anchors, then segments.
    /// </summary>
    public static PathHit HitTest(VectorPath path, PathPoint p, double tolerance, IReadOnlySet<KnotRef>? handlesOf = null)
    {
        if (handlesOf is not null)
            foreach (var r in handlesOf)
            {
                if (!Valid(path, r)) continue;
                var k = path.Subpaths[r.Subpath].Knots[r.Knot];
                if (k.HasOut && PathPoint.Distance(k.Out, p) <= tolerance) return new PathHit(PathPart.OutHandle, r);
                if (k.HasIn && PathPoint.Distance(k.In, p) <= tolerance) return new PathHit(PathPart.InHandle, r);
            }
        double best = tolerance;
        var hit = PathHit.None;
        for (int s = path.Subpaths.Count - 1; s >= 0; s--)
        {
            var knots = path.Subpaths[s].Knots;
            for (int k = 0; k < knots.Count; k++)
            {
                double d = PathPoint.Distance(knots[k].Anchor, p);
                if (d <= best)
                {
                    best = d;
                    hit = new PathHit(PathPart.Anchor, new KnotRef(s, k));
                }
            }
        }
        if (hit.Part != PathPart.None) return hit;
        return SegmentAt(path, p, tolerance) is { } seg ? new PathHit(PathPart.Segment, new KnotRef(seg.Subpath, seg.Segment), seg.T) : PathHit.None;
    }

    /// <summary>The nearest segment within <paramref name="tolerance"/>, its start knot and the curve parameter there.</summary>
    public static (int Subpath, int Segment, double T, double Distance)? SegmentAt(VectorPath path, PathPoint p, double tolerance)
    {
        (int, int, double, double)? best = null;
        double bestD = tolerance;
        for (int s = path.Subpaths.Count - 1; s >= 0; s--)
        {
            int i = 0;
            foreach (var (p0, c0, c1, p1) in path.Subpaths[s].Segments())
            {
                // Coarse samples, then refine around the best one.
                const int N = 48;
                double bt = 0, bd = double.MaxValue;
                for (int j = 0; j <= N; j++)
                {
                    double t = (double)j / N;
                    double d = PathPoint.Distance(Bezier.At(p0, c0, c1, p1, t), p);
                    if (d < bd) (bd, bt) = (d, t);
                }
                double step = 1.0 / N;
                for (int iter = 0; iter < 20; iter++)
                {
                    step /= 2;
                    foreach (double t in (ReadOnlySpan<double>)[bt - step, bt + step])
                    {
                        if (t < 0 || t > 1) continue;
                        double d = PathPoint.Distance(Bezier.At(p0, c0, c1, p1, t), p);
                        if (d < bd) (bd, bt) = (d, t);
                    }
                }
                if (bd <= bestD)
                {
                    bestD = bd;
                    best = (s, i, bt, bd);
                }
                i++;
            }
        }
        return best;
    }

    private static bool Valid(VectorPath path, KnotRef r) =>
        r.Subpath >= 0 && r.Subpath < path.Subpaths.Count && r.Knot >= 0 && r.Knot < path.Subpaths[r.Subpath].Knots.Count;

    // ---- Edits -------------------------------------------------------------------------------------------

    private static VectorPath WithSubpath(VectorPath path, int s, Subpath sub)
    {
        var list = path.Subpaths.ToArray();
        list[s] = sub;
        return path with { Subpaths = list };
    }

    private static VectorPath WithKnot(VectorPath path, KnotRef r, PathKnot k)
    {
        var knots = path.Subpaths[r.Subpath].Knots.ToArray();
        knots[r.Knot] = k;
        return WithSubpath(path, r.Subpath, path.Subpaths[r.Subpath] with { Knots = knots });
    }

    /// <summary>Moves anchors (with their handles) by an offset.</summary>
    public static VectorPath MoveKnots(VectorPath path, IEnumerable<KnotRef> knots, double dx, double dy)
    {
        var set = knots.ToHashSet();
        var subs = path.Subpaths.Select((s, si) => set.Any(r => r.Subpath == si)
            ? s with { Knots = s.Knots.Select((k, ki) => set.Contains(new KnotRef(si, ki)) ? k.Offset(dx, dy) : k).ToArray() }
            : s).ToArray();
        return path with { Subpaths = subs };
    }

    /// <summary>Moves whole subpaths by an offset.</summary>
    public static VectorPath MoveSubpaths(VectorPath path, IEnumerable<int> subpaths, double dx, double dy)
    {
        var set = subpaths.ToHashSet();
        return path with { Subpaths = path.Subpaths.Select((s, i) => set.Contains(i) ? s.Offset(dx, dy) : s).ToArray() };
    }

    /// <summary>
    /// Puts a direction handle at <paramref name="to"/>. On a smooth (linked) point the other handle turns to stay
    /// opposite, keeping its length, unless <paramref name="breakLink"/> (Option) makes it a corner.
    /// </summary>
    public static VectorPath MoveHandle(VectorPath path, KnotRef r, bool outHandle, PathPoint to, bool breakLink)
    {
        var k = path.Subpaths[r.Subpath].Knots[r.Knot];
        var a = k.Anchor;
        PathPoint newIn = k.In, newOut = k.Out;
        if (outHandle) newOut = to;
        else newIn = to;
        bool linked = k.Linked && !breakLink;
        if (linked)
        {
            var moved = (outHandle ? newOut : newIn) - a;
            double len = moved.Length;
            var other = outHandle ? k.In : k.Out;
            double otherLen = (other - a).Length;
            if (len > 1e-9 && otherLen > 1e-9)
            {
                var opposite = a - moved * (otherLen / len);
                if (outHandle) newIn = opposite;
                else newOut = opposite;
            }
        }
        return WithKnot(path, r, new PathKnot(newIn, a, newOut, linked));
    }

    /// <summary>Convert Point on an anchor: a smooth point becomes a corner (both handles retracted).</summary>
    public static VectorPath MakeCorner(VectorPath path, KnotRef r)
    {
        var a = path.Subpaths[r.Subpath].Knots[r.Knot].Anchor;
        return WithKnot(path, r, PathKnot.Corner(a));
    }

    /// <summary>Convert Point dragged from an anchor: a smooth point whose leaving handle follows the pointer.</summary>
    public static VectorPath PullHandles(VectorPath path, KnotRef r, PathPoint to)
    {
        var a = path.Subpaths[r.Subpath].Knots[r.Knot].Anchor;
        return WithKnot(path, r, PathKnot.Smooth(a, to));
    }

    /// <summary>Add Anchor Point: splits the segment starting at <paramref name="segment"/> at <paramref name="t"/>, keeping the curve's shape.</summary>
    public static (VectorPath Path, KnotRef Added) InsertKnot(VectorPath path, int subpath, int segment, double t)
    {
        var s = path.Subpaths[subpath];
        int n = s.Knots.Count;
        var a = s.Knots[segment];
        var b = s.Knots[(segment + 1) % n];
        var (left, right) = Bezier.Split(a.Anchor, a.Out, b.In, b.Anchor, t);
        var knots = s.Knots.ToList();
        knots[segment] = a with { Out = left.Item2 };
        var added = new PathKnot(left.Item3, left.Item4, right.Item2, a.HasOut || b.HasIn);
        int bi = (segment + 1) % n;
        knots[bi] = knots[bi] with { In = right.Item3 };
        knots.Insert(segment + 1, added);
        return (WithSubpath(path, subpath, s with { Knots = knots }), new KnotRef(subpath, segment + 1));
    }

    /// <summary>Delete Anchor Point / Delete: removes anchors; subpaths left with fewer than two anchors go too.</summary>
    public static VectorPath DeleteKnots(VectorPath path, IEnumerable<KnotRef> knots)
    {
        var set = knots.ToHashSet();
        var subs = new List<Subpath>();
        for (int si = 0; si < path.Subpaths.Count; si++)
        {
            var s = path.Subpaths[si];
            var kept = s.Knots.Where((_, ki) => !set.Contains(new KnotRef(si, ki))).ToArray();
            if (kept.Length == s.Knots.Count) subs.Add(s);
            else if (kept.Length >= 2) subs.Add(s with { Knots = kept });
            else if (si + 1 < path.Subpaths.Count && PathRasterizer.ContinuesComponent(path.Subpaths[si + 1]) && !PathRasterizer.ContinuesComponent(s))
            {
                // The component's first subpath carries its operation: hand it to the next one.
                path = WithSubpath(path, si + 1, path.Subpaths[si + 1] with { RecordTail = s.RecordTail, Operation = s.Operation });
            }
        }
        return path with { Subpaths = subs };
    }

    /// <summary>Removes whole subpaths (Path Selection and Delete).</summary>
    public static VectorPath DeleteSubpaths(VectorPath path, IEnumerable<int> subpaths)
    {
        var set = subpaths.ToHashSet();
        return path with { Subpaths = path.Subpaths.Where((_, i) => !set.Contains(i)).ToArray() };
    }

    /// <summary>The subpaths of the component <paramref name="subpath"/> belongs to (a shape with holes moves as one).</summary>
    public static IEnumerable<int> ComponentOf(VectorPath path, int subpath)
    {
        int start = subpath;
        while (start > 0 && PathRasterizer.ContinuesComponent(path.Subpaths[start])) start--;
        int end = subpath;
        while (end + 1 < path.Subpaths.Count && PathRasterizer.ContinuesComponent(path.Subpaths[end + 1])) end++;
        return Enumerable.Range(start, end - start + 1);
    }

    /// <summary>Anchors inside a rectangle (Direct Selection's marquee).</summary>
    public static IEnumerable<KnotRef> KnotsIn(VectorPath path, double left, double top, double right, double bottom)
    {
        for (int s = 0; s < path.Subpaths.Count; s++)
            for (int k = 0; k < path.Subpaths[s].Knots.Count; k++)
            {
                var a = path.Subpaths[s].Knots[k].Anchor;
                if (a.X >= left && a.X <= right && a.Y >= top && a.Y <= bottom) yield return new KnotRef(s, k);
            }
    }

    /// <summary>Subpaths touching a rectangle (Path Selection's marquee): any anchor inside, or the rectangle inside its bounds.</summary>
    public static IEnumerable<int> SubpathsIn(VectorPath path, double left, double top, double right, double bottom)
    {
        for (int s = 0; s < path.Subpaths.Count; s++)
        {
            var sub = path.Subpaths[s];
            if (sub.Knots.Count == 0) continue;
            var (l, t, r, b) = sub.ControlBounds();
            if (r < left || l > right || b < top || t > bottom) continue;
            yield return s;
        }
    }

    /// <summary>Every knot of the given subpaths.</summary>
    public static IEnumerable<KnotRef> KnotsOf(VectorPath path, IEnumerable<int> subpaths) =>
        subpaths.Where(s => s >= 0 && s < path.Subpaths.Count).SelectMany(s => Enumerable.Range(0, path.Subpaths[s].Knots.Count).Select(k => new KnotRef(s, k)));

    /// <summary>A point constrained to 45° steps from <paramref name="from"/> (Shift with the pen).</summary>
    public static PathPoint Constrain45(PathPoint from, PathPoint to)
    {
        var d = to - from;
        double len = d.Length;
        if (len < 1e-9) return to;
        double angle = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 4)) * (Math.PI / 4);
        return new PathPoint(from.X + Math.Cos(angle) * len, from.Y + Math.Sin(angle) * len);
    }

    // ---- Shape drags -------------------------------------------------------------------------------------

    /// <summary>
    /// The box a shape drag from <paramref name="start"/> to <paramref name="end"/> draws: Shift makes it square,
    /// Option draws from the centre (Photoshop's modifiers).
    /// </summary>
    public static (double Left, double Top, double Right, double Bottom) DragBox(PathPoint start, PathPoint end, bool square, bool fromCenter)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        if (square)
        {
            double m = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = Math.Sign(dx == 0 ? 1 : dx) * m;
            dy = Math.Sign(dy == 0 ? 1 : dy) * m;
        }
        if (fromCenter) return (start.X - Math.Abs(dx), start.Y - Math.Abs(dy), start.X + Math.Abs(dx), start.Y + Math.Abs(dy));
        return (Math.Min(start.X, start.X + dx), Math.Min(start.Y, start.Y + dy), Math.Max(start.X, start.X + dx), Math.Max(start.Y, start.Y + dy));
    }
}
