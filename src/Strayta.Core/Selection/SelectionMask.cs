using System.Numerics;

namespace Strayta.Core.Selection;

/// <summary>How a new selection shape combines with the current selection (Photoshop's marquee modifiers).</summary>
public enum SelectionMode
{
    Replace,
    Add,
    Subtract,
    Intersect,
}

/// <summary>
/// A document-space selection: 8-bit coverage (255 = fully selected) over <see cref="Bounds"/>, nothing selected
/// outside. Instances are immutable; every operation returns a new mask, so undo only swaps references.
/// "No selection" is represented by null, never by an empty mask: operations that leave nothing selected return null.
/// </summary>
public sealed class SelectionMask
{
    // Null when every pixel in Bounds is fully selected, so rectangles and Select All cost no memory.
    private readonly byte[]? _mask;

    private SelectionMask(PixelRect bounds, byte[]? mask)
    {
        Bounds = bounds;
        _mask = mask;
    }

    /// <summary>Smallest rectangle containing every selected pixel.</summary>
    public PixelRect Bounds { get; }

    /// <summary>True when the selection is exactly <see cref="Bounds"/> with hard edges.</summary>
    public bool IsRectangular => _mask is null;

    /// <summary>Coverage at a document pixel, 0..255.</summary>
    public byte CoverageAt(int x, int y)
    {
        var b = Bounds;
        if (x < b.Left || x >= b.Right || y < b.Top || y >= b.Bottom) return 0;
        return _mask is null ? (byte)255 : _mask[(y - b.Top) * b.Width + (x - b.Left)];
    }

    /// <summary>Writes the coverage of document row <paramref name="y"/>, columns [x0, x0 + dst.Length), into <paramref name="dst"/>.</summary>
    public void CopyRow(int y, int x0, Span<byte> dst)
    {
        var b = Bounds;
        int n = dst.Length;
        if (y < b.Top || y >= b.Bottom)
        {
            dst.Clear();
            return;
        }
        int from = Math.Clamp(b.Left - x0, 0, n), to = Math.Clamp(b.Right - x0, 0, n);
        if (to <= from)
        {
            dst.Clear();
            return;
        }
        dst[..from].Clear();
        dst[to..].Clear();
        if (_mask is null) dst[from..to].Fill(255);
        else _mask.AsSpan((y - b.Top) * b.Width + (x0 + from - b.Left), to - from).CopyTo(dst[from..to]);
    }

    // ---- Shapes ----------------------------------------------------------------------------------

    /// <summary>Selects the whole canvas.</summary>
    public static SelectionMask All(PixelRect canvas) => new(canvas, null);

    /// <summary>
    /// A selection from per-pixel coverage computed elsewhere (e.g. a segmentation model): <paramref name="coverage"/>
    /// is row-major over <paramref name="bounds"/>. Clipped to the canvas and trimmed; null when nothing is selected.
    /// The array is adopted, not copied, so the caller must not change it afterwards.
    /// </summary>
    public static SelectionMask? FromCoverage(PixelRect bounds, byte[] coverage, PixelRect canvas)
    {
        if (coverage.Length != (long)bounds.Width * bounds.Height)
            throw new ArgumentException($"Expected {bounds.Width * bounds.Height} coverage values for {bounds}.", nameof(coverage));
        var clipped = bounds.Intersect(canvas);
        if (clipped.IsEmpty) return null;
        if (clipped == bounds) return Trim(bounds, coverage);
        var copy = new byte[clipped.Width * clipped.Height];
        for (int y = clipped.Top; y < clipped.Bottom; y++)
            coverage.AsSpan((y - bounds.Top) * bounds.Width + (clipped.Left - bounds.Left), clipped.Width)
                .CopyTo(copy.AsSpan((y - clipped.Top) * clipped.Width, clipped.Width));
        return Trim(clipped, copy);
    }

    /// <summary>A hard-edged rectangle (the marquee snaps to whole pixels), clipped to the canvas.</summary>
    public static SelectionMask? Rectangle(PixelRect rect, PixelRect canvas)
    {
        var r = rect.Intersect(canvas);
        return r.IsEmpty ? null : new SelectionMask(r, null);
    }

    /// <summary>An anti-aliased ellipse inscribed in <paramref name="box"/>, clipped to the canvas.</summary>
    public static SelectionMask? Ellipse(PixelRect box, PixelRect canvas)
    {
        var bounds = box.Intersect(canvas);
        if (bounds.IsEmpty) return null;
        float cx = (box.Left + box.Right) / 2f, cy = (box.Top + box.Bottom) / 2f;
        float rx = box.Width / 2f, ry = box.Height / 2f;
        float irx2 = 1f / (rx * rx), iry2 = 1f / (ry * ry);
        int w = bounds.Width;
        var mask = new byte[w * bounds.Height];

        Parallel.For(bounds.Top, bounds.Bottom, y =>
        {
            float dy = y + 0.5f - cy;
            float fy = dy * dy * iry2, gy = dy * iry2;
            int row = (y - bounds.Top) * w - bounds.Left;
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                float dx = x + 0.5f - cx;
                // Signed distance to the edge, approximated as f / |∇f| for f = (dx/rx)² + (dy/ry)² - 1. That is
                // accurate within the one-pixel band that gets anti-aliased, which is all that matters.
                float f = dx * dx * irx2 + fy - 1f;
                float gx = dx * irx2;
                float grad = 2f * MathF.Sqrt(gx * gx + gy * gy);
                float d = grad > 0f ? f / grad : -1e6f;
                mask[row + x] = ToByte(0.5f - d);
            }
        });
        return Trim(bounds, mask);
    }

    private readonly record struct Edge(float Top, float Bottom, float X0, float Slope, int Dir);

    /// <summary>
    /// An anti-aliased polygon (lasso), filled with the nonzero winding rule so loops drawn over themselves stay
    /// selected. Coverage is exact horizontally and sampled on 16 sub-rows vertically.
    /// </summary>
    public static SelectionMask? Polygon(IReadOnlyList<Vector2> points, PixelRect canvas)
    {
        if (points.Count < 3) return null;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in points)
        {
            minX = MathF.Min(minX, p.X);
            minY = MathF.Min(minY, p.Y);
            maxX = MathF.Max(maxX, p.X);
            maxY = MathF.Max(maxY, p.Y);
        }
        var bounds = new PixelRect((int)MathF.Floor(minX), (int)MathF.Floor(minY), (int)MathF.Ceiling(maxX), (int)MathF.Ceiling(maxY))
            .Intersect(canvas);
        if (bounds.IsEmpty) return null;

        // Edges sorted by top, so each sub-row only considers the edges that can cross it.
        var edges = new List<Edge>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (a.Y == b.Y) continue;
            edges.Add(a.Y < b.Y
                ? new Edge(a.Y, b.Y, a.X, (b.X - a.X) / (b.Y - a.Y), 1)
                : new Edge(b.Y, a.Y, b.X, (a.X - b.X) / (a.Y - b.Y), -1));
        }
        edges.Sort((p, q) => p.Top.CompareTo(q.Top));

        const int Sub = 16;
        const float Weight = 1f / Sub;
        int w = bounds.Width;
        var mask = new byte[w * bounds.Height];
        // Per row: partial coverage of span end pixels, plus a difference array for the fully covered interiors,
        // so a span costs O(1) no matter how wide it is.
        var partial = new float[w];
        var diff = new float[w + 1];
        var active = new List<Edge>();
        var crossings = new List<(float X, int Dir)>();
        int next = 0;

        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int s = 0; s < Sub; s++)
            {
                float sy = y + (s + 0.5f) * Weight;
                while (next < edges.Count && edges[next].Top <= sy) active.Add(edges[next++]);
                active.RemoveAll(e => e.Bottom <= sy);
                if (active.Count < 2) continue;

                crossings.Clear();
                foreach (var e in active) crossings.Add((e.X0 + (sy - e.Top) * e.Slope, e.Dir));
                crossings.Sort((p, q) => p.X.CompareTo(q.X));

                int winding = 0;
                for (int i = 0; i < crossings.Count - 1; i++)
                {
                    winding += crossings[i].Dir;
                    if (winding != 0) AddSpan(crossings[i].X - bounds.Left, crossings[i + 1].X - bounds.Left);
                }
            }

            int row = (y - bounds.Top) * w;
            float full = 0f;
            for (int x = 0; x < w; x++)
            {
                full += diff[x];
                mask[row + x] = ToByte(full + partial[x]);
            }
            Array.Clear(partial);
            Array.Clear(diff);
        }
        return Trim(bounds, mask);

        void AddSpan(float xa, float xb)
        {
            xa = Math.Clamp(xa, 0f, w);
            xb = Math.Clamp(xb, 0f, w);
            if (xb <= xa) return;
            int ia = (int)xa, ib = (int)xb;
            if (ia == ib)
            {
                partial[ia] += (xb - xa) * Weight;
                return;
            }
            partial[ia] += (ia + 1 - xa) * Weight;
            diff[ia + 1] += Weight;
            diff[ib] -= Weight;
            if (ib < w) partial[ib] += (xb - ib) * Weight;
        }
    }

    /// <summary>
    /// A selection from computed coverage over <paramref name="bounds"/> (row-major, 255 = selected), for tools that
    /// build masks themselves (magic wand, quick selection). Takes ownership of the array; null if nothing is selected.
    /// </summary>
    public static SelectionMask? FromCoverage(PixelRect bounds, byte[] coverage) => FromCoverage(bounds, coverage, bounds);

    // ---- Operations ------------------------------------------------------------------------------

    /// <summary>
    /// Combines <paramref name="shape"/> with <paramref name="current"/>: add takes the maximum coverage, subtract
    /// scales the current coverage by the shape's complement, intersect takes the minimum.
    /// </summary>
    public static SelectionMask? Combine(SelectionMask? current, SelectionMask? shape, SelectionMode mode)
    {
        switch (mode)
        {
            case SelectionMode.Replace:
                return shape;
            case SelectionMode.Add when current is null:
                return shape;
            case SelectionMode.Add when shape is null:
                return current;
            case SelectionMode.Subtract when current is null || shape is null:
                return current;
            case SelectionMode.Intersect when current is null || shape is null:
                return null;
        }

        var a = current!;
        var b = shape!;
        var bounds = mode switch
        {
            SelectionMode.Add => Union(a.Bounds, b.Bounds),
            SelectionMode.Subtract => a.Bounds,
            _ => a.Bounds.Intersect(b.Bounds),
        };
        if (bounds.IsEmpty) return null;
        if (mode == SelectionMode.Intersect && a.IsRectangular && b.IsRectangular) return new SelectionMask(bounds, null);
        if (mode == SelectionMode.Subtract && b.Bounds.Intersect(a.Bounds).IsEmpty) return a;

        int w = bounds.Width;
        var mask = new byte[w * bounds.Height];
        Parallel.For(bounds.Top, bounds.Bottom, () => (new byte[w], new byte[w]), (y, _, rows) =>
        {
            var (ra, rb) = rows;
            a.CopyRow(y, bounds.Left, ra);
            b.CopyRow(y, bounds.Left, rb);
            var dst = mask.AsSpan((y - bounds.Top) * w, w);
            for (int x = 0; x < w; x++)
            {
                dst[x] = mode switch
                {
                    SelectionMode.Add => Math.Max(ra[x], rb[x]),
                    SelectionMode.Subtract => (byte)((ra[x] * (255 - rb[x]) + 127) / 255),
                    _ => Math.Min(ra[x], rb[x]),
                };
            }
            return rows;
        }, _ => { });
        return Trim(bounds, mask);
    }

    /// <summary>Select > Inverse: everything on the canvas that was not selected.</summary>
    public static SelectionMask? Invert(SelectionMask? selection, PixelRect canvas)
    {
        if (selection is null) return All(canvas);
        if (selection.IsRectangular && selection.Bounds == canvas) return null;
        int w = canvas.Width;
        var mask = new byte[w * canvas.Height];
        Parallel.For(canvas.Top, canvas.Bottom, y =>
        {
            var dst = mask.AsSpan((y - canvas.Top) * w, w);
            selection.CopyRow(y, canvas.Left, dst);
            for (int x = 0; x < w; x++) dst[x] = (byte)(255 - dst[x]);
        });
        return Trim(canvas, mask);
    }

    /// <summary>
    /// Shrinks a freshly computed mask to the pixels actually selected, and drops the mask entirely when it is a
    /// solid rectangle. Returns null when nothing is selected.
    /// </summary>
    private static SelectionMask? Trim(PixelRect bounds, byte[] mask)
    {
        int w = bounds.Width, h = bounds.Height;
        int top = -1, bottom = -1, left = w, right = -1;
        for (int y = 0; y < h; y++)
        {
            var row = mask.AsSpan(y * w, w);
            int first = row.IndexOfAnyExcept((byte)0);
            if (first < 0) continue;
            if (top < 0) top = y;
            bottom = y;
            left = Math.Min(left, first);
            right = Math.Max(right, row.LastIndexOfAnyExcept((byte)0));
        }
        if (top < 0) return null;

        var trimmed = new PixelRect(bounds.Left + left, bounds.Top + top, bounds.Left + right + 1, bounds.Top + bottom + 1);
        int tw = trimmed.Width;
        bool solid = true;
        for (int y = top; y <= bottom && solid; y++)
            solid = !mask.AsSpan(y * w + left, tw).ContainsAnyExcept((byte)255);
        if (solid) return new SelectionMask(trimmed, null);
        if (trimmed == bounds) return new SelectionMask(bounds, mask);

        var copy = new byte[tw * trimmed.Height];
        for (int y = 0; y < trimmed.Height; y++)
            mask.AsSpan((top + y) * w + left, tw).CopyTo(copy.AsSpan(y * tw, tw));
        return new SelectionMask(trimmed, copy);
    }

    private static byte ToByte(float coverage) =>
        coverage <= 0f ? (byte)0 : coverage >= 1f ? (byte)255 : (byte)(coverage * 255f + 0.5f);

    private static PixelRect Union(PixelRect a, PixelRect b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
}
