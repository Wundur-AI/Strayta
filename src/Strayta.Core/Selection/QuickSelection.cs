using System.Numerics;

namespace Strayta.Core.Selection;

/// <summary>
/// A reduced copy of a <see cref="SampleImage"/> that Quick Selection grows regions on: block averages at most
/// <see cref="MaxWorkingSide"/> cells on the long side, so a drag stays interactive on any image size, plus an
/// estimate of the image's noise so edge strength can be judged relative to it. Build it once per sample image.
/// </summary>
public sealed class QuickSelectionImage
{
    public const int MaxWorkingSide = 1024;

    private QuickSelectionImage(SampleImage source, int factor, int width, int height, float[] color, float noise)
    {
        Source = source;
        Factor = factor;
        Width = width;
        Height = height;
        Color = color;
        Noise = noise;
    }

    public SampleImage Source { get; }

    /// <summary>Image pixels per working cell along each axis.</summary>
    public int Factor { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Premultiplied RGBA per cell, 0..255, 4 floats per cell.</summary>
    internal float[] Color { get; }

    /// <summary>Typical color difference between neighboring cells (the median), at least 2 levels.</summary>
    public float Noise { get; }

    public static QuickSelectionImage Build(SampleImage source, int maxSide = MaxWorkingSide)
    {
        int f = Math.Max(1, (Math.Max(source.Width, source.Height) + maxSide - 1) / maxSide);
        int w = (source.Width + f - 1) / f, h = (source.Height + f - 1) / f;
        var color = new float[w * h * 4];
        var rgba = source.Rgba;
        int sw = source.Width;
        Parallel.For(0, h, cy =>
        {
            int y0 = cy * f, y1 = Math.Min(y0 + f, source.Height);
            Span<int> sum = stackalloc int[4];
            for (int cx = 0; cx < w; cx++)
            {
                int x0 = cx * f, x1 = Math.Min(x0 + f, sw);
                sum.Clear();
                for (int y = y0; y < y1; y++)
                {
                    int i = (y * sw + x0) * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        sum[0] += rgba[i];
                        sum[1] += rgba[i + 1];
                        sum[2] += rgba[i + 2];
                        sum[3] += rgba[i + 3];
                    }
                }
                float inv = 1f / ((y1 - y0) * (x1 - x0));
                int o = (cy * w + cx) * 4;
                for (int c = 0; c < 4; c++) color[o + c] = sum[c] * inv;
            }
        });

        // Median neighbor difference from a histogram (differences are at most 2·255 = 510).
        var histogram = new long[512];
        long count = 0;
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
            {
                int p = (y * w + x) * 4;
                if (x + 1 < w) { histogram[(int)Distance(color, p, p + 4)]++; count++; }
                if (y + 1 < h) { histogram[(int)Distance(color, p, p + w * 4)]++; count++; }
            }
        float median = 0;
        for (long seen = 0; median < histogram.Length && (seen += histogram[(int)median]) * 2 < count;) median++;
        return new QuickSelectionImage(source, f, w, h, color, Math.Max(2f, median));
    }

    internal static float Distance(float[] c, int p, int q)
    {
        float d0 = c[p] - c[q], d1 = c[p + 1] - c[q + 1], d2 = c[p + 2] - c[q + 2], d3 = c[p + 3] - c[q + 3];
        return MathF.Sqrt(d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3);
    }
}

/// <summary>
/// One Quick Selection drag: the region grows from the brushed pixels to similar, connected pixels and stops at
/// edges. Add the brush path with <see cref="AddSegment"/> as the pointer moves, show <see cref="PreviewOutline"/>,
/// and call <see cref="Finish"/> on release. Not thread-safe: use it from one thread at a time.
/// </summary>
/// <remarks>
/// <para>The region is everything within a geodesic distance of the brushed cells on the working image. Each step
/// between neighboring cells costs its length times 1 + an edge term + a color term:</para>
/// <list type="bullet">
/// <item>edge: the color difference to the neighbor in units of the image's noise, ignored up to 2 (texture and
/// noise) and squared beyond, so real edges are nearly impassable while grainy areas are not;</item>
/// <item>color: how far the cell's color is from the brushed colors (a few running clusters), in units of each
/// cluster's spread, ignored up to 2.5 and squared beyond.</item>
/// </list>
/// <para>The distance limit scales with the brush and the image, so a click in a flat area grows a sizable blob, as
/// Photoshop's does, while strokes along an object fill it out to its edges. Distances only ever shrink as brushing
/// adds seeds, so each pointer move runs Dijkstra (with a bucket queue) from the new seeds only and touches just the
/// cells whose distance improves; that is what keeps a drag live on large images.</para>
/// <para>On release the working-resolution region is refined at full resolution in a band one cell wide around its
/// boundary: each pixel is placed between the local inside and outside colors (their means over nearby cells) by
/// projecting its color onto the line between them, which snaps the edge to the image's own edge within the cell.
/// Where the two sides barely differ (the region ended in a flat area) the cell labels are interpolated instead, so
/// such edges come out smooth rather than blocky. Auto-Enhance keeps that fractional coverage as a soft,
/// anti-aliased edge and smooths it; without it the edge is hard.</para>
/// </remarks>
public sealed class QuickSelectionStroke
{
    private const int Buckets = 4;          // bucket queue resolution: distance units per bucket index
    private const float EdgeFree = 2f;      // neighbor differences up to this many noise units are free
    private const float EdgeWeight = 20f;
    private const float ColorFree = 2.5f;   // colors within this many cluster spreads are free
    private const float ColorWeight = 10f;
    private const int MaxClusters = 8;

    private readonly QuickSelectionImage _image;
    private readonly int _w, _h;
    private readonly float[] _dist;
    private readonly bool[] _seeded;
    private readonly bool[]? _base;
    private readonly SelectionMask? _baseSelection;
    private readonly float _radius, _limit;
    private readonly List<int>[] _queue;
    private readonly List<Cluster> _clusters = [];
    private float[] _clusterMean = [], _clusterInvSpread = [];
    private readonly float[] _colorCost;
    private readonly int[] _colorStamp;
    private int _stamp;
    private int _minX = int.MaxValue, _minY = int.MaxValue, _maxX = -1, _maxY = -1;

    private sealed class Cluster
    {
        public double N, SumSq;
        public readonly double[] Sum = new double[4];
    }

    /// <param name="brushDiameter">Brush size in image pixels.</param>
    /// <param name="baseSelection">The selection before the drag, combined with the grown region by <paramref name="mode"/>.</param>
    public QuickSelectionStroke(QuickSelectionImage image, float brushDiameter, SelectionMask? baseSelection, SelectionMode mode)
    {
        _image = image;
        _w = image.Width;
        _h = image.Height;
        _dist = new float[_w * _h];
        _dist.AsSpan().Fill(float.PositiveInfinity);
        _seeded = new bool[_w * _h];
        _colorCost = new float[_w * _h];
        _colorStamp = new int[_w * _h];
        _radius = Math.Max(0.5f, brushDiameter / 2f / image.Factor);
        _limit = Math.Max(4f * _radius, Math.Max(_w, _h) / 5f);
        _queue = new List<int>[(int)(_limit * Buckets) + 2];
        for (int i = 0; i < _queue.Length; i++) _queue[i] = [];
        Mode = mode;
        _baseSelection = baseSelection;
        if (baseSelection is not null && mode != SelectionMode.Replace) _base = Downsample(baseSelection);
    }

    public SelectionMode Mode { get; }

    /// <summary>True until the brush has touched the image.</summary>
    public bool IsEmpty => _maxX < 0;

    /// <summary>Cells relaxed so far (for benchmarks).</summary>
    public long Work { get; private set; }

    /// <summary>Brushes from <paramref name="from"/> to <paramref name="to"/> (image coordinates) and grows the region. False if nothing new was brushed.</summary>
    public bool AddSegment(Vector2 from, Vector2 to)
    {
        float f = _image.Factor;
        Vector2 a = from / f, b = to / f;
        float r = _radius;
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, b.X) - r)), x1 = Math.Min(_w - 1, (int)MathF.Ceiling(MathF.Max(a.X, b.X) + r));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, b.Y) - r)), y1 = Math.Min(_h - 1, (int)MathF.Ceiling(MathF.Max(a.Y, b.Y) + r));
        var seeds = new List<int>();
        var ab = b - a;
        float len2 = ab.LengthSquared();
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float t = len2 > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
                if (Vector2.DistanceSquared(p, a + t * ab) <= r * r) Seed(y * _w + x);
            }
        // A brush smaller than a cell still marks the cells it passes through.
        foreach (var e in (ReadOnlySpan<Vector2>)[a, b])
            if (e.X >= 0 && e.Y >= 0 && e.X < _w && e.Y < _h) Seed((int)e.Y * _w + (int)e.X);
        if (seeds.Count == 0) return false;

        foreach (int s in seeds) Learn(s);
        UpdateModel();
        foreach (int s in seeds)
        {
            _dist[s] = 0;
            _queue[0].Add(s);
        }
        Propagate();
        return true;

        void Seed(int i)
        {
            if (_seeded[i]) return;
            _seeded[i] = true;
            seeds.Add(i);
        }
    }

    // ---- Color model --------------------------------------------------------------------------------

    /// <summary>Adds a brushed cell's color to the nearest cluster, or starts a new cluster if it is far from all of them.</summary>
    private void Learn(int cell)
    {
        var c = _image.Color;
        int o = cell * 4;
        Cluster? best = null;
        double bestD = double.MaxValue;
        foreach (var k in _clusters)
        {
            double d = 0;
            for (int i = 0; i < 4; i++)
            {
                double diff = c[o + i] - k.Sum[i] / k.N;
                d += diff * diff;
            }
            if (d < bestD) (best, bestD) = (k, d);
        }
        float merge = Math.Max(3f * _image.Noise, 20f);
        if (best is null || (bestD > merge * merge && _clusters.Count < MaxClusters)) _clusters.Add(best = new Cluster());
        best.N++;
        double sq = 0;
        for (int i = 0; i < 4; i++)
        {
            best.Sum[i] += c[o + i];
            sq += c[o + i] * c[o + i];
        }
        best.SumSq += sq;
    }

    private void UpdateModel()
    {
        int n = _clusters.Count;
        _clusterMean = new float[n * 4];
        _clusterInvSpread = new float[n];
        float floor = Math.Max(1.5f * _image.Noise, 6f);
        for (int k = 0; k < n; k++)
        {
            var cl = _clusters[k];
            double m2 = 0;
            for (int i = 0; i < 4; i++)
            {
                double m = cl.Sum[i] / cl.N;
                _clusterMean[k * 4 + i] = (float)m;
                m2 += m * m;
            }
            float spread = (float)Math.Sqrt(Math.Max(0, cl.SumSq / cl.N - m2));
            _clusterInvSpread[k] = 1f / Math.Max(spread, floor);
        }
        _stamp++; // cached color costs used the old model
    }

    /// <summary>The color term for a cell: zero for colors like the brushed ones, growing quickly beyond.</summary>
    private float ColorCost(int cell)
    {
        if (_colorStamp[cell] == _stamp) return _colorCost[cell];
        var c = _image.Color;
        int o = cell * 4;
        float best = float.MaxValue;
        for (int k = 0; k < _clusterInvSpread.Length; k++)
        {
            float d0 = c[o] - _clusterMean[k * 4], d1 = c[o + 1] - _clusterMean[k * 4 + 1];
            float d2 = c[o + 2] - _clusterMean[k * 4 + 2], d3 = c[o + 3] - _clusterMean[k * 4 + 3];
            best = Math.Min(best, MathF.Sqrt(d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3) * _clusterInvSpread[k]);
        }
        float excess = Math.Max(0f, best - ColorFree);
        float cost = ColorWeight * excess * excess;
        _colorStamp[cell] = _stamp;
        _colorCost[cell] = cost;
        return cost;
    }

    // ---- Growing ------------------------------------------------------------------------------------

    private static readonly (int Dx, int Dy, float Length)[] Neighbors =
    [
        (1, 0, 1f), (-1, 0, 1f), (0, 1, 1f), (0, -1, 1f),
        (1, 1, MathF.Sqrt(2)), (-1, 1, MathF.Sqrt(2)), (1, -1, MathF.Sqrt(2)), (-1, -1, MathF.Sqrt(2)),
    ];

    /// <summary>
    /// Dijkstra from the queued cells with a bucket queue. Every step costs at least one unit, more than a bucket's
    /// width, so relaxed cells always land in a later bucket and each bucket is final once reached.
    /// </summary>
    private void Propagate()
    {
        var color = _image.Color;
        float invNoise = 1f / _image.Noise;
        for (int b = 0; b < _queue.Length; b++)
        {
            var bucket = _queue[b];
            for (int i = 0; i < bucket.Count; i++)
            {
                int p = bucket[i];
                float d = _dist[p];
                if ((int)(d * Buckets) != b) continue; // superseded by a shorter path
                Work++;
                int px = p % _w, py = p / _w;
                if (px < _minX) _minX = px;
                if (px > _maxX) _maxX = px;
                if (py < _minY) _minY = py;
                if (py > _maxY) _maxY = py;
                foreach (var (dx, dy, length) in Neighbors)
                {
                    int qx = px + dx, qy = py + dy;
                    if ((uint)qx >= (uint)_w || (uint)qy >= (uint)_h) continue;
                    int q = qy * _w + qx;
                    if (_dist[q] <= d + length) continue; // cannot improve: every step costs at least its length
                    float edge = Math.Max(0f, QuickSelectionImage.Distance(color, p * 4, q * 4) * invNoise - EdgeFree);
                    float nd = d + length * (1f + EdgeWeight * edge * edge + ColorCost(q));
                    if (nd >= _dist[q] || nd >= _limit) continue;
                    _dist[q] = nd;
                    _queue[(int)(nd * Buckets)].Add(q);
                }
            }
            bucket.Clear();
        }
    }

    private bool InRegion(int cell) => _dist[cell] < _limit;

    private bool[] Downsample(SelectionMask selection)
    {
        var inside = new bool[_w * _h];
        int f = _image.Factor, sw = _image.Source.Width, sh = _image.Source.Height;
        Parallel.For(0, _h, () => new byte[_w * f], (cy, _, row) =>
        {
            int y = Math.Min(cy * f + f / 2, sh - 1);
            selection.CopyRow(y, 0, row.AsSpan(0, sw));
            for (int cx = 0; cx < _w; cx++) inside[cy * _w + cx] = row[Math.Min(cx * f + f / 2, sw - 1)] >= 128;
            return row;
        }, _ => { });
        return inside;
    }

    // ---- Results ------------------------------------------------------------------------------------

    /// <summary>
    /// The outline of the selection as it would be if the drag ended now (the region combined with the selection
    /// before the drag), at working resolution, as closed loops in image coordinates.
    /// </summary>
    public IReadOnlyList<Vector2[]> PreviewOutline()
    {
        var mask = new byte[_w * _h];
        var mode = Mode;
        var baseCells = _base;
        Parallel.For(0, _h, y =>
        {
            for (int i = y * _w; i < (y + 1) * _w; i++)
            {
                bool region = InRegion(i), before = baseCells is not null && baseCells[i];
                bool selected = mode switch
                {
                    SelectionMode.Subtract => before && !region,
                    SelectionMode.Intersect => before && region,
                    SelectionMode.Add => before || region,
                    _ => region,
                };
                mask[i] = selected ? (byte)255 : (byte)0;
            }
        });
        var loops = SelectionOutline.Trace(SelectionMask.FromCoverage(new PixelRect(0, 0, _w, _h), mask));
        int f = _image.Factor, sw = _image.Source.Width, sh = _image.Source.Height;
        if (f == 1) return loops;
        foreach (var loop in loops)
            for (int i = 0; i < loop.Length; i++)
                loop[i] = new Vector2(Math.Min(loop[i].X * f, sw), Math.Min(loop[i].Y * f, sh));
        return loops;
    }

    /// <summary>The final selection: the refined region combined with the selection before the drag.</summary>
    public SelectionMask? Finish(bool autoEnhance)
    {
        var region = Region(autoEnhance);
        return Mode == SelectionMode.Replace ? region : SelectionMask.Combine(_baseSelection, region, Mode);
    }

    /// <summary>The grown region alone at full resolution, refined along its boundary (see the remarks).</summary>
    public SelectionMask? Region(bool autoEnhance)
    {
        if (IsEmpty) return null;
        int f = _image.Factor, w = _w, h = _h;
        var source = _image.Source;
        var color = _image.Color;

        // Cells that can be selected lie within the relaxed area; one more cell around it holds the refinement band.
        int cx0 = Math.Max(0, _minX - 1), cy0 = Math.Max(0, _minY - 1), cx1 = Math.Min(w, _maxX + 2), cy1 = Math.Min(h, _maxY + 2);
        var bounds = new PixelRect(cx0 * f, cy0 * f, Math.Min(cx1 * f, source.Width), Math.Min(cy1 * f, source.Height));
        int cw = cx1 - cx0, ch = cy1 - cy0;

        // Boundary cells (a neighbor in the grid has the other label) get the local inside/outside colors.
        var band = new int[cw * ch];
        var local = new List<(Vector4 Inside, Vector4 Outside, float Weight)>();
        float threshold = Math.Max(3f * _image.Noise, 10f);
        for (int cy = cy0; cy < cy1; cy++)
            for (int cx = cx0; cx < cx1; cx++)
            {
                int slot = (cy - cy0) * cw + (cx - cx0);
                band[slot] = -1;
                if (!IsBoundary(cx, cy)) continue;
                Vector4 fg = default, bg = default;
                int nf = 0, nb = 0;
                // Prefer cells away from the boundary: boundary cells average both sides.
                for (int pass = 0; pass < 2 && (nf == 0 || nb == 0); pass++)
                    for (int y = Math.Max(0, cy - 2); y <= Math.Min(h - 1, cy + 2); y++)
                        for (int x = Math.Max(0, cx - 2); x <= Math.Min(w - 1, cx + 2); x++)
                        {
                            if (pass == 0 && IsBoundary(x, y)) continue;
                            int o = (y * w + x) * 4;
                            var c = new Vector4(color[o], color[o + 1], color[o + 2], color[o + 3]);
                            bool inside = InRegion(y * w + x);
                            if (inside && (pass == 0 || nf == 0)) { fg += c; nf++; }
                            else if (!inside && (pass == 0 || nb == 0)) { bg += c; nb++; }
                        }
                if (nf == 0 || nb == 0) continue;
                fg /= nf;
                bg /= nb;
                float contrast = Vector4.Distance(fg, bg);
                band[slot] = local.Count;
                local.Add((fg, bg, Math.Clamp((contrast - threshold) / threshold, 0f, 1f)));
            }

        int bw = bounds.Width;
        var coverage = new byte[bw * bounds.Height];
        var rgba = source.Rgba;
        Parallel.For(bounds.Top, bounds.Bottom, y =>
        {
            int cy = y / f;
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                int cx = x / f;
                int slot = band[(cy - cy0) * cw + (cx - cx0)];
                float alpha;
                if (slot < 0) alpha = InRegion(cy * w + cx) ? 1f : 0f;
                else
                {
                    var (fg, bg, weight) = local[slot];
                    float geometric = Interpolate(x, y);
                    if (weight > 0f)
                    {
                        int o = (y * source.Width + x) * 4;
                        var c = new Vector4(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]);
                        var axis = fg - bg;
                        float projected = Math.Clamp(Vector4.Dot(c - bg, axis) / axis.LengthSquared(), 0f, 1f);
                        alpha = weight * projected + (1f - weight) * geometric;
                    }
                    else alpha = geometric;
                }
                coverage[(y - bounds.Top) * bw + (x - bounds.Left)] = autoEnhance
                    ? (byte)(alpha * 255f + 0.5f)
                    : alpha >= 0.5f ? (byte)255 : (byte)0;
            }
        });
        if (autoEnhance) coverage = MagicWand.SmoothEdges(coverage, bw, bounds.Height);
        return SelectionMask.FromCoverage(bounds, coverage);

        bool IsBoundary(int cx, int cy)
        {
            bool inside = InRegion(cy * w + cx);
            for (int y = Math.Max(0, cy - 1); y <= Math.Min(h - 1, cy + 1); y++)
                for (int x = Math.Max(0, cx - 1); x <= Math.Min(w - 1, cx + 1); x++)
                    if (InRegion(y * w + x) != inside) return true;
            return false;
        }

        // Bilinear interpolation of the inside/outside labels of the cells around an image pixel's center.
        float Interpolate(int x, int y)
        {
            float u = (x + 0.5f) / f - 0.5f, v = (y + 0.5f) / f - 0.5f;
            int ux = (int)MathF.Floor(u), vy = (int)MathF.Floor(v);
            float fx = u - ux, fy = v - vy;
            float Label(int cx, int cy) => InRegion(Math.Clamp(cy, 0, h - 1) * w + Math.Clamp(cx, 0, w - 1)) ? 1f : 0f;
            return (Label(ux, vy) * (1 - fx) + Label(ux + 1, vy) * fx) * (1 - fy) + (Label(ux, vy + 1) * (1 - fx) + Label(ux + 1, vy + 1) * fx) * fy;
        }
    }
}
