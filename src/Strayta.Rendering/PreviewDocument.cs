using System.Runtime.CompilerServices;
using Strayta.Core;

namespace Strayta.Rendering;

/// <summary>
/// A reduced-resolution mirror of a document for interactive rendering. Layer pixels and masks are
/// downsampled once per scale factor and cached; each <see cref="Sync"/> only copies properties
/// (visibility, opacity, blend mode, position, order) from the source document.
/// </summary>
public sealed class PreviewDocument
{
    private readonly Document _source;
    private readonly Dictionary<LayerNode, LayerNode> _proxies = new(ReferenceEqualityComparer.Instance);
    private List<(LayerNode Node, LayerGroup? Parent)> _structure = [];

    public PreviewDocument(Document source, int factor)
    {
        if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor));
        _source = source;
        Factor = factor;
        Proxy = new Document(Math.Max(1, Ceil(source.Width, factor)), Math.Max(1, Ceil(source.Height, factor)), source.ColorMode, source.BitDepth)
        {
            Palette = source.Palette,
        };
    }

    public int Factor { get; }
    public Document Proxy { get; }

    /// <summary>
    /// Scale factor (a power of two) for an interactive preview at <paramref name="zoom"/> (screen points per
    /// image pixel). A preview pixel may cover up to 1.5 screen points: slightly soft while dragging, but a
    /// quarter of the work of the next finer level.
    /// </summary>
    public static int FactorForZoom(double zoom)
    {
        int f = 1;
        while (f < 16 && zoom * f * 2 <= 1.5) f *= 2;
        return f;
    }

    /// <summary>Maps a stroke on a source layer (or its mask) onto its proxy layer (call after <see cref="Sync"/>).</summary>
    public StrokeOverlay? MapStroke(Strayta.Core.Painting.PaintStroke? stroke) =>
        stroke is not null && _proxies.TryGetValue(stroke.Owner, out var p) && (stroke.TargetsMask || p is PixelLayer)
            ? new StrokeOverlay(stroke, p, Factor)
            : null;

    /// <summary>The proxy standing in for <paramref name="source"/> (call after <see cref="Sync"/>), e.g. to hide it in a render.</summary>
    public LayerNode? ProxyOf(LayerNode source) => _proxies.GetValueOrDefault(source);

    /// <summary>Brings the proxy up to date with the source document and returns it.</summary>
    public Document Sync()
    {
        var structure = _source.Root.Descendants().Select(n => (n, n.Parent)).ToList();
        if (!structure.SequenceEqual(_structure, StructureComparer.Instance))
        {
            RebuildTree(_source.Root, Proxy.Root);
            _structure = structure;
            foreach (var stale in _proxies.Keys.Except(structure.Select(s => s.n), ReferenceEqualityComparer.Instance).ToList())
                _proxies.Remove((LayerNode)stale!);
        }

        foreach (var (node, _) in structure) CopyProperties(node, _proxies[node]);
        return Proxy;
    }

    private void RebuildTree(LayerGroup source, LayerGroup proxy)
    {
        foreach (var child in proxy.Children.ToList()) proxy.Remove(child);
        foreach (var child in source.Children)
        {
            if (!_proxies.TryGetValue(child, out var p))
            {
                p = child switch
                {
                    LayerGroup => new LayerGroup(),
                    AdjustmentLayer => new AdjustmentLayer(),
                    _ => new PixelLayer(),
                };
                _proxies[child] = p;
            }
            proxy.Add(p);
            if (child is LayerGroup g) RebuildTree(g, (LayerGroup)p);
        }
    }

    private void CopyProperties(LayerNode s, LayerNode p)
    {
        p.Name = s.Name;
        p.Visible = s.Visible;
        p.Opacity = s.Opacity;
        p.FillOpacity = s.FillOpacity;
        p.BlendMode = s.BlendMode;
        p.Clipped = s.Clipped;
        p.Effects = s.Effects is null ? null : ScaledEffects(s.Effects);
        if (p.Tags.Count != s.Tags.Count) foreach (var t in s.Tags) p.Tags.Add(t);

        switch (s, p)
        {
            case (PixelLayer sl, PixelLayer pl):
                pl.Pixels = sl.Pixels is null ? null : Downsample(sl.Pixels);
                pl.Bounds = pl.Pixels is null || sl.Bounds.IsEmpty ? PixelRect.Empty : Place(sl.Bounds, pl.Pixels.Width, pl.Pixels.Height);
                pl.Mask = ScaledMask(sl.Mask);
                break;
            case (AdjustmentLayer sa, AdjustmentLayer pa):
                pa.Adjustment = sa.Adjustment;
                pa.Kind = sa.Kind;
                pa.Mask = ScaledMask(sa.Mask);
                break;
            case (LayerGroup sg, LayerGroup pg):
                pg.Mask = ScaledMask(sg.Mask);
                break;
        }
    }

    /// <summary>Positions a downsampled block; rounding the origin keeps movement smooth at preview scale.</summary>
    private PixelRect Place(PixelRect sourceBounds, int width, int height)
    {
        int left = (int)Math.Round(sourceBounds.Left / (double)Factor);
        int top = (int)Math.Round(sourceBounds.Top / (double)Factor);
        return new PixelRect(left, top, left + width, top + height);
    }

    private LayerMask? ScaledMask(LayerMask? m)
    {
        if (m is null) return null;
        var pixels = m.Pixels is null ? null : Downsample(m.Pixels);
        var bounds = pixels is null
            ? new PixelRect(Floor(m.Bounds.Left, Factor), Floor(m.Bounds.Top, Factor), Ceil(m.Bounds.Right, Factor), Ceil(m.Bounds.Bottom, Factor))
            : Place(m.Bounds, pixels.Width, pixels.Height);
        return new LayerMask { Bounds = bounds, Pixels = pixels, DefaultColor = m.DefaultColor, Disabled = m.Disabled, PositionRelativeToLayer = m.PositionRelativeToLayer };
    }

    // ---- Cached downsampling ---------------------------------------------------------------------

    private static readonly ConditionalWeakTable<Raster, Dictionary<int, Raster>> RasterCache = new();
    private static readonly ConditionalWeakTable<Plane, Dictionary<int, Plane>> PlaneCache = new();
    private static readonly ConditionalWeakTable<LayerEffects, Dictionary<int, LayerEffects>> EffectsCache = new();

    private Raster Downsample(Raster r)
    {
        if (Factor == 1) return r;
        var byFactor = RasterCache.GetOrCreateValue(r);
        lock (byFactor)
        {
            if (!byFactor.TryGetValue(Factor, out var scaled))
                byFactor[Factor] = scaled = DownsampleRaster(r, Factor);
            return scaled;
        }
    }

    private Plane Downsample(Plane p)
    {
        if (Factor == 1) return p;
        var byFactor = PlaneCache.GetOrCreateValue(p);
        lock (byFactor)
        {
            if (!byFactor.TryGetValue(Factor, out var scaled))
                byFactor[Factor] = scaled = BoxDownsample(p, Factor, weights: null);
            return scaled;
        }
    }

    /// <summary>Effect sizes shrink with the image; the result is cached so its identity stays stable for the render cache.</summary>
    private LayerEffects ScaledEffects(LayerEffects fx)
    {
        if (Factor == 1) return fx;
        var byFactor = EffectsCache.GetOrCreateValue(fx);
        lock (byFactor)
        {
            if (byFactor.TryGetValue(Factor, out var scaled)) return scaled;
            scaled = fx.Scaled(1f / Factor);
            byFactor[Factor] = scaled;
            return scaled;
        }
    }

    /// <summary>Averages color weighted by alpha so transparent pixels do not darken edges.</summary>
    private static Raster DownsampleRaster(Raster r, int f)
    {
        var alpha = r.Alpha is null ? null : BoxDownsample(r.Alpha, f, weights: null);
        var planes = r.ColorPlanes.Select(p => BoxDownsample(p, f, r.Alpha)).ToArray();
        return new Raster(r.ColorMode, planes, alpha);
    }

    private static Plane BoxDownsample(Plane p, int f, Plane? weights)
    {
        if (p.BitDepth == 8 && weights is null or { BitDepth: 8 }) return BoxDownsample8(p, f, weights);
        int w = Ceil(p.Width, f), h = Ceil(p.Height, f);
        var o = Plane.Create(Math.Max(1, w), Math.Max(1, h), p.BitDepth);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float sum = 0, wsum = 0;
                int count = 0;
                for (int sy = y * f; sy < Math.Min(p.Height, y * f + f); sy++)
                    for (int sx = x * f; sx < Math.Min(p.Width, x * f + f); sx++)
                    {
                        int i = sy * p.Width + sx;
                        float wt = weights?.GetNormalized(i) ?? 1f;
                        sum += p.GetNormalized(i) * wt;
                        wsum += wt;
                        count++;
                    }
                float v = wsum > 0 ? sum / wsum : 0f;
                int oi = y * w + x;
                switch (p.BitDepth)
                {
                    case 8: o.Data[oi] = RgbaConverter.ToByte(v); break;
                    case 16: o.AsUInt16()[oi] = (ushort)Math.Clamp(MathF.Round(v * 65535f), 0f, 65535f); break;
                    default: o.AsSingle()[oi] = v; break;
                }
            }
        });
        return o;
    }

    /// <summary>8-bit fast path: integer sums over each block, alpha-weighted when weights are given.</summary>
    private static Plane BoxDownsample8(Plane p, int f, Plane? weights)
    {
        int sw = p.Width, sh = p.Height;
        int w = Math.Max(1, Ceil(sw, f)), h = Math.Max(1, Ceil(sh, f));
        var o = Plane.Create(w, h, 8);
        byte[] src = p.Data, dst = o.Data;
        byte[]? wt = weights?.Data;
        Parallel.For(0, h, y =>
        {
            int y0 = y * f, y1 = Math.Min(sh, y0 + f);
            for (int x = 0; x < w; x++)
            {
                int x0 = x * f, x1 = Math.Min(sw, x0 + f);
                long sum = 0, wsum = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * sw;
                    if (wt is null)
                    {
                        for (int sx = x0; sx < x1; sx++) sum += src[row + sx];
                        wsum += x1 - x0;
                    }
                    else
                    {
                        for (int sx = x0; sx < x1; sx++)
                        {
                            int a = wt[row + sx];
                            sum += src[row + sx] * a;
                            wsum += a;
                        }
                    }
                }
                dst[y * w + x] = wsum == 0 ? (byte)0 : (byte)((sum + wsum / 2) / wsum);
            }
        });
        return o;
    }

    private static int Ceil(int v, int f) => (int)Math.Ceiling(v / (double)f);
    private static int Floor(int v, int f) => (int)Math.Floor(v / (double)f);

    private sealed class StructureComparer : IEqualityComparer<(LayerNode Node, LayerGroup? Parent)>
    {
        public static readonly StructureComparer Instance = new();
        public bool Equals((LayerNode Node, LayerGroup? Parent) a, (LayerNode Node, LayerGroup? Parent) b) =>
            ReferenceEquals(a.Node, b.Node) && ReferenceEquals(a.Parent, b.Parent);
        public int GetHashCode((LayerNode Node, LayerGroup? Parent) v) => RuntimeHelpers.GetHashCode(v.Node);
    }
}
