using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Strayta.Core.Selection;

/// <summary>
/// What a filter sees beyond each side of the grid it works on: a fixed coverage value (0 = unselected,
/// 255 = selected), or for blurs, optionally the grid's own edge repeated.
/// </summary>
internal readonly record struct GridEdges(byte Left, byte Top, byte Right, byte Bottom, bool Repeat = false)
{
    public static GridEdges All(byte value) => new(value, value, value, value);
    public GridEdges Inverted => new((byte)(255 - Left), (byte)(255 - Top), (byte)(255 - Right), (byte)(255 - Bottom), Repeat);

    /// <summary>
    /// The edges of <paramref name="region"/> within <paramref name="canvas"/>: sides inside the canvas see unselected
    /// pixels (the grid was made large enough that nothing beyond matters); sides on the canvas edge see
    /// <paramref name="beyondCanvas"/>.
    /// </summary>
    public static GridEdges For(PixelRect region, PixelRect canvas, byte beyondCanvas) => new(
        region.Left <= canvas.Left ? beyondCanvas : (byte)0,
        region.Top <= canvas.Top ? beyondCanvas : (byte)0,
        region.Right >= canvas.Right ? beyondCanvas : (byte)0,
        region.Bottom >= canvas.Bottom ? beyondCanvas : (byte)0);
}

/// <summary>
/// The image-processing kernels behind Select › Modify and Select and Mask, on row-major coverage grids:
/// flat disc dilation and erosion (grayscale morphology), Gaussian blur, and helpers to move between grids and
/// selections. All run in parallel and cost the same whatever the selection's shape.
/// </summary>
internal static class MaskFilters
{
    // ---- Grids ------------------------------------------------------------------------------------

    /// <summary>The selection's coverage over <paramref name="region"/> (zero where nothing is selected).</summary>
    public static byte[] Extract(SelectionMask? selection, PixelRect region)
    {
        int w = region.Width;
        var grid = new byte[(long)w * region.Height];
        if (selection is null) return grid;
        Parallel.For(region.Top, region.Bottom, y => selection.CopyRow(y, region.Left, grid.AsSpan((y - region.Top) * w, w)));
        return grid;
    }

    public static PixelRect Inflate(PixelRect r, int by) => new(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);

    public static void Invert(byte[] grid)
    {
        var v = MemoryMarshal.Cast<byte, Vector<byte>>(grid.AsSpan());
        for (int i = 0; i < v.Length; i++) v[i] = ~v[i];
        for (int i = v.Length * Vector<byte>.Count; i < grid.Length; i++) grid[i] = (byte)~grid[i];
    }

    // ---- Morphology -------------------------------------------------------------------------------

    /// <summary>
    /// Flat dilation by a disc of <paramref name="radius"/> pixels (offsets with dx² + dy² ≤ (radius + ½)²): every
    /// pixel takes the highest coverage within the disc, so soft edges keep their softness as they move outward.
    /// </summary>
    /// <remarks>
    /// The disc is a stack of horizontal runs, one per row offset. For each input row a sparse table holds the maximum
    /// of every run of 2^k pixels, so the maximum over any run is the larger of two table entries (the two
    /// power-of-two runs that overlap to cover it). An output row is then 2·(2r+1) vectorized maximum passes over
    /// input rows, with no per-pixel branching. The table costs log₂(2r+1) copies of the grid, padded by r on each
    /// side so runs never need clipping; rows beyond the top or bottom only contribute their edge value.
    /// </remarks>
    public static byte[] Dilate(byte[] src, int w, int h, int radius, GridEdges edges)
    {
        if (radius <= 0) return (byte[])src.Clone();
        int r = radius;
        long r2 = (long)r * r + r; // (r + ½)², rounded down for integer offsets
        var half = new int[r + 1];
        for (int dy = 0; dy <= r; dy++) half[dy] = (int)Math.Sqrt(r2 - (long)dy * dy);

        int pw = w + 2 * r;
        int levels = Log2(2 * r + 1) + 1;
        var table = new byte[levels][];
        table[0] = new byte[(long)pw * h];
        var rowMax = new byte[h];
        Parallel.For(0, h, y =>
        {
            var row = table[0].AsSpan(y * pw, pw);
            row[..r].Fill(edges.Left);
            var s = src.AsSpan(y * w, w);
            s.CopyTo(row[r..]);
            row[(r + w)..].Fill(edges.Right);
            rowMax[y] = Max(row);
        });
        for (int k = 1; k < levels; k++)
        {
            var prev = table[k - 1];
            var cur = table[k] = new byte[(long)pw * h];
            int step = 1 << (k - 1);
            Parallel.For(0, h, y =>
            {
                if (rowMax[y] == 0) return; // stays all zero
                var p = prev.AsSpan(y * pw, pw);
                var c = cur.AsSpan(y * pw, pw);
                MaxOf(p[..(pw - step)], p[step..], c[..(pw - step)]);
                p[(pw - step)..].CopyTo(c[(pw - step)..]);
            });
        }

        var dst = new byte[(long)w * h];
        Parallel.For(0, h, y =>
        {
            var acc = dst.AsSpan(y * w, w);
            byte beyond = 0;
            if (y - r < 0) beyond = edges.Top;
            if (y + r >= h) beyond = Math.Max(beyond, edges.Bottom);
            acc.Fill(beyond);
            for (int dy = -r; dy <= r; dy++)
            {
                int yy = y + dy;
                if ((uint)yy >= (uint)h || rowMax[yy] <= beyond) continue;
                int hw = half[Math.Abs(dy)], len = 2 * hw + 1, k = Log2(len), span = 1 << k;
                var t = table[k].AsSpan(yy * pw, pw);
                MaxInto(acc, t.Slice(r - hw, w));
                if (span != len) MaxInto(acc, t.Slice(r + hw - span + 1, w));
            }
        });
        return dst;
    }

    /// <summary>Flat erosion by a disc: the lowest coverage within <paramref name="radius"/> (dilation of the complement).</summary>
    public static byte[] Erode(byte[] src, int w, int h, int radius, GridEdges edges)
    {
        if (radius <= 0) return (byte[])src.Clone();
        var inverted = (byte[])src.Clone();
        Invert(inverted);
        var result = Dilate(inverted, w, h, radius, edges.Inverted);
        Invert(result);
        return result;
    }

    // ---- Blur -------------------------------------------------------------------------------------

    /// <summary>
    /// Gaussian blur with standard deviation <paramref name="sigma"/> pixels, in floats (0..255). Small sigmas use the
    /// sampled kernel directly; larger ones three box blurs whose combined variance is σ², which costs the same for
    /// any radius. Beyond the grid, <paramref name="edges"/> gives the value, or with Repeat the edge row or column.
    /// </summary>
    public static float[] Blur(float[] src, int w, int h, float sigma, GridEdges edges)
    {
        if (sigma < 0.2f) return (float[])src.Clone();
        if (sigma <= 3f) return KernelBlur(src, w, h, sigma, edges);
        var radii = BoxRadii(sigma);
        if (edges.Repeat)
        {
            // Every pass repeats its own edge: the selection simply carries on beyond the grid.
            var r = src;
            foreach (int box in radii) r = BoxHorizontal(r, w, h, box, edges);
            foreach (int box in radii) r = BoxVertical(r, w, h, box, edges);
            return r;
        }
        // With fixed values beyond the edge, each pass would clip what the previous one spread past it, so pad once
        // by the passes' total reach, blur with the (constant) padding repeated, and crop.
        int pad = radii.Sum();
        int pw = w + 2 * pad, ph = h + 2 * pad;
        var a = Pad(src, w, h, pad, edges);
        var constant = GridEdges.All(0) with { Repeat = true };
        foreach (int box in radii) a = BoxHorizontal(a, pw, ph, box, constant);
        foreach (int box in radii) a = BoxVertical(a, pw, ph, box, constant);
        var result = new float[src.Length];
        Parallel.For(0, h, y => a.AsSpan((y + pad) * pw + pad, w).CopyTo(result.AsSpan(y * w, w)));
        return result;
    }

    private static float[] Pad(float[] src, int w, int h, int pad, GridEdges edges)
    {
        int pw = w + 2 * pad, ph = h + 2 * pad;
        var dst = new float[(long)pw * ph];
        Parallel.For(0, ph, py =>
        {
            int y = py - pad;
            var row = dst.AsSpan(py * pw, pw);
            if (y < 0 || y >= h)
            {
                if (edges.Repeat) Pad(src.AsSpan(Math.Clamp(y, 0, h - 1) * w, w), row, pad, edges);
                else row.Fill(y < 0 ? edges.Top : edges.Bottom);
                return;
            }
            Pad(src.AsSpan(y * w, w), row, pad, edges);
        });
        return dst;

        static void Pad(ReadOnlySpan<float> line, Span<float> row, int pad, GridEdges edges)
        {
            row[..pad].Fill(edges.Repeat ? line[0] : edges.Left);
            line.CopyTo(row[pad..]);
            row[(pad + line.Length)..].Fill(edges.Repeat ? line[^1] : edges.Right);
        }
    }

    /// <summary>
    /// Radii of three boxes whose variances add up to about σ². A box of odd width n has variance (n² − 1) / 12, so
    /// the ideal equal width is √(12σ²/3 + 1); boxes of the odd widths just below and above it are mixed so the
    /// total comes closest to σ².
    /// </summary>
    private static int[] BoxRadii(float sigma)
    {
        const int n = 3;
        double ideal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0) lower--;
        int upper = lower + 2;
        // m boxes of the lower width and n − m of the upper: n(u² − 1)/12 − m(u² − l²)/12 = σ².
        double m = (n * (upper * upper - 1) - 12 * sigma * sigma) / (upper * upper - (double)lower * lower);
        int lowerCount = Math.Clamp((int)Math.Round(m), 0, n);
        var radii = new int[n];
        for (int i = 0; i < n; i++) radii[i] = ((i < lowerCount ? lower : upper) - 1) / 2;
        return radii;
    }

    private static float Beyond(ReadOnlySpan<float> line, bool before, GridEdges edges, byte fixedValue) =>
        edges.Repeat ? (before ? line[0] : line[^1]) : fixedValue;

    private static float[] BoxHorizontal(float[] src, int w, int h, int r, GridEdges edges)
    {
        if (r <= 0) return src;
        var dst = new float[src.Length];
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, h, y =>
        {
            var line = src.AsSpan(y * w, w);
            var output = dst.AsSpan(y * w, w);
            float left = Beyond(line, true, edges, edges.Left), right = Beyond(line, false, edges, edges.Right);
            // Running sum over [x − r, x + r], reading beyond-edge values where the window sticks out.
            double sum = 0;
            for (int i = -r; i <= r; i++) sum += i < 0 ? left : i >= w ? right : line[i];
            for (int x = 0; x < w; x++)
            {
                output[x] = (float)(sum * inv);
                int add = x + r + 1, drop = x - r;
                sum += (add >= w ? right : line[add]) - (drop < 0 ? left : line[drop]);
            }
        });
        return dst;
    }

    private static float[] BoxVertical(float[] src, int w, int h, int r, GridEdges edges)
    {
        if (r <= 0) return src;
        var dst = new float[src.Length];
        float inv = 1f / (2 * r + 1);
        const int chunk = 256;
        int chunks = (w + chunk - 1) / chunk;
        // Column strips, each a running sum of whole row segments: cache friendly and vectorizable.
        Parallel.For(0, chunks, c =>
        {
            int x0 = c * chunk, n = Math.Min(chunk, w - x0);
            var sum = new double[n];
            var top = new float[n];
            var bottom = new float[n];
            for (int i = 0; i < n; i++)
            {
                top[i] = edges.Repeat ? src[x0 + i] : edges.Top;
                bottom[i] = edges.Repeat ? src[(h - 1) * w + x0 + i] : edges.Bottom;
            }
            ReadOnlySpan<float> Row(int y, float[] t, float[] b) => y < 0 ? t : y >= h ? b : src.AsSpan(y * w + x0, n);
            for (int y = -r; y <= r; y++)
            {
                var row = Row(y, top, bottom);
                for (int i = 0; i < n; i++) sum[i] += row[i];
            }
            for (int y = 0; y < h; y++)
            {
                var output = dst.AsSpan(y * w + x0, n);
                for (int i = 0; i < n; i++) output[i] = (float)(sum[i] * inv);
                var add = Row(y + r + 1, top, bottom);
                var drop = Row(y - r, top, bottom);
                for (int i = 0; i < n; i++) sum[i] += add[i] - drop[i];
            }
        });
        return dst;
    }

    private static float[] KernelBlur(float[] src, int w, int h, float sigma, GridEdges edges)
    {
        int r = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[2 * r + 1];
        float total = 0;
        for (int i = -r; i <= r; i++) total += kernel[i + r] = MathF.Exp(-i * i / (2 * sigma * sigma));
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= total;

        var mid = new float[src.Length];
        Parallel.For(0, h, y =>
        {
            var line = src.AsSpan(y * w, w);
            var output = mid.AsSpan(y * w, w);
            float left = Beyond(line, true, edges, edges.Left), right = Beyond(line, false, edges, edges.Right);
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                if (x >= r && x + r < w)
                    for (int i = -r; i <= r; i++) s += kernel[i + r] * line[x + i];
                else
                    for (int i = -r; i <= r; i++)
                    {
                        int xx = x + i;
                        s += kernel[i + r] * (xx < 0 ? left : xx >= w ? right : line[xx]);
                    }
                output[x] = s;
            }
        });

        var dst = new float[src.Length];
        var topRow = new float[w];
        var bottomRow = new float[w];
        for (int x = 0; x < w; x++)
        {
            topRow[x] = edges.Repeat ? mid[x] : edges.Top;
            bottomRow[x] = edges.Repeat ? mid[(h - 1) * w + x] : edges.Bottom;
        }
        Parallel.For(0, h, y =>
        {
            var output = dst.AsSpan(y * w, w);
            for (int i = -r; i <= r; i++)
            {
                int yy = y + i;
                ReadOnlySpan<float> row = yy < 0 ? topRow : yy >= h ? bottomRow : mid.AsSpan(yy * w, w);
                AddScaled(output, row, kernel[i + r]);
            }
        });
        return dst;
    }

    public static float[] ToFloat(byte[] grid)
    {
        var f = new float[grid.Length];
        Parallel.For(0, (grid.Length + 65535) / 65536, c =>
        {
            int start = c * 65536, end = Math.Min(start + 65536, grid.Length);
            for (int i = start; i < end; i++) f[i] = grid[i];
        });
        return f;
    }

    public static byte[] ToBytes(float[] values)
    {
        var b = new byte[values.Length];
        Parallel.For(0, (values.Length + 65535) / 65536, c =>
        {
            int start = c * 65536, end = Math.Min(start + 65536, values.Length);
            for (int i = start; i < end; i++) b[i] = ClampByte(values[i]);
        });
        return b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ClampByte(float v) => v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + 0.5f);

    // ---- Vector helpers ---------------------------------------------------------------------------

    private static int Log2(int n) => BitOperations.Log2((uint)n);

    private static byte Max(ReadOnlySpan<byte> s)
    {
        int i = 0, vc = Vector<byte>.Count;
        var acc = Vector<byte>.Zero;
        for (; i <= s.Length - vc; i += vc) acc = Vector.Max(acc, new Vector<byte>(s[i..]));
        byte m = 0;
        for (int j = 0; j < vc; j++) m = Math.Max(m, acc[j]);
        for (; i < s.Length; i++) m = Math.Max(m, s[i]);
        return m;
    }

    private static void MaxOf(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst)
    {
        int i = 0, vc = Vector<byte>.Count;
        for (; i <= dst.Length - vc; i += vc) Vector.Max(new Vector<byte>(a[i..]), new Vector<byte>(b[i..])).CopyTo(dst[i..]);
        for (; i < dst.Length; i++) dst[i] = Math.Max(a[i], b[i]);
    }

    private static void MaxInto(Span<byte> acc, ReadOnlySpan<byte> src)
    {
        int i = 0, vc = Vector<byte>.Count;
        for (; i <= acc.Length - vc; i += vc) Vector.Max(new Vector<byte>(acc[i..]), new Vector<byte>(src[i..])).CopyTo(acc[i..]);
        for (; i < acc.Length; i++) acc[i] = Math.Max(acc[i], src[i]);
    }

    private static void AddScaled(Span<float> acc, ReadOnlySpan<float> src, float k)
    {
        int i = 0, vc = Vector<float>.Count;
        var kv = new Vector<float>(k);
        for (; i <= acc.Length - vc; i += vc) (new Vector<float>(acc[i..]) + new Vector<float>(src[i..]) * kv).CopyTo(acc[i..]);
        for (; i < acc.Length; i++) acc[i] += src[i] * k;
    }
}
