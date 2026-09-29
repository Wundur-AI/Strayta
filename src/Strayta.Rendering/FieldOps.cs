namespace Strayta.Rendering;

/// <summary>Operations on single-channel float fields (width × height, row-major).</summary>
internal static class FieldOps
{
    /// <summary>
    /// Approximates a Gaussian with three box blurs whose combined reach is <paramref name="size"/> pixels,
    /// matching how far a Photoshop shadow or glow of that size fades out.
    /// </summary>
    public static float[] Blur(float[] src, int w, int h, float size)
    {
        int r = (int)MathF.Round(size / 3f);
        if (r <= 0) return (float[])src.Clone();
        var a = (float[])src.Clone();
        var b = new float[a.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxHorizontal(a, b, w, h, r);
            BoxVertical(b, a, w, h, r);
        }
        return a;
    }

    /// <summary>
    /// A tent blur (two chained box blurs) reaching <paramref name="radius"/> pixels and no further: the blur of a Softer
    /// glow. Chained box blurs of a rounded radius overshoot (by up to a pixel per box), which small glows show. Above a
    /// pixel the tent is blended from whole tents of the two nearest sizes (above eight, the nearest one), in constant
    /// time per pixel.
    /// </summary>
    public static float[] TentBlur(float[] src, int w, int h, float radius)
    {
        if (radius <= 0.01f) return (float[])src.Clone();
        if (radius > 1f) return LargeTent(src, w, h, radius);
        // Under a pixel: the tent integrated over the center pixel and its neighbours.
        float side = radius > 0.5f ? (radius - 0.5f) * (radius - 0.5f) / 2f : 0f;
        float center = radius > 0.5f ? radius * radius - 2f * side : radius * radius;
        float total = center + 2f * side;
        return Convolve(src, w, h, [center / total, side / total]);
    }

    /// <summary>A tent of reach <paramref name="radius"/>: whole tents of the two nearest sizes, blended by the fraction.</summary>
    private static float[] LargeTent(float[] src, int w, int h, float radius)
    {
        // The whole tent of n reaches n + 1 pixels (its weights n + 1 - |k| end there).
        float r = radius - 1f;
        int n = (int)r;
        float frac = r - n;
        // Past a few pixels, the nearest whole tent is as good (and half the work).
        if (radius > 8f) return Tent(src, w, h, frac < 0.5f ? n : n + 1);
        var lo = Tent(src, w, h, n);
        if (frac < 1e-3f) return lo;
        var hi = Tent(src, w, h, n + 1);
        for (int i = 0; i < lo.Length; i++) lo[i] += (hi[i] - lo[i]) * frac;
        return lo;
    }

    /// <summary>The discrete tent with weights n + 1 − |k| for |k| ≤ n: a box of n + 1 pixels applied twice, mirrored.</summary>
    private static float[] Tent(float[] src, int w, int h, int n)
    {
        if (n == 0) return (float[])src.Clone();
        int a = n / 2, b = n - a;
        var t1 = new float[src.Length];
        var t2 = new float[src.Length];
        AsymmetricBox(src, t1, w, h, a, b, horizontal: true);
        AsymmetricBox(t1, t2, w, h, b, a, horizontal: true);
        AsymmetricBox(t2, t1, w, h, a, b, horizontal: false);
        AsymmetricBox(t1, t2, w, h, b, a, horizontal: false);
        return t2;
    }

    /// <summary>dst = the mean of src from <paramref name="before"/> pixels before to <paramref name="after"/> after, along rows or columns; zero beyond the edges.</summary>
    private static void AsymmetricBox(float[] src, float[] dst, int w, int h, int before, int after, bool horizontal)
    {
        float inv = 1f / (before + after + 1);
        if (horizontal)
        {
            Parallel.For(0, h, y =>
            {
                int start = y * w;
                float sum = 0f;
                for (int k = 0; k <= Math.Min(after, w - 1); k++) sum += src[start + k];
                for (int x = 0; x < w; x++)
                {
                    dst[start + x] = sum * inv;
                    int add = x + after + 1, remove = x - before;
                    if (add < w) sum += src[start + add];
                    if (remove >= 0) sum -= src[start + remove];
                }
            });
            return;
        }

        // Down the columns a band at a time, keeping one running sum per column: whole rows are read and written in
        // order, which is much faster than walking each column.
        const int Band = 256;
        Parallel.For(0, (w + Band - 1) / Band, band =>
        {
            int x0 = band * Band, n = Math.Min(Band, w - x0);
            Span<float> sum = stackalloc float[Band];
            sum = sum[..n];
            sum.Clear();
            for (int k = 0; k <= Math.Min(after, h - 1); k++)
            {
                var row = src.AsSpan(k * w + x0, n);
                for (int i = 0; i < n; i++) sum[i] += row[i];
            }
            for (int y = 0; y < h; y++)
            {
                var o = dst.AsSpan(y * w + x0, n);
                for (int i = 0; i < n; i++) o[i] = sum[i] * inv;
                int add = y + after + 1, remove = y - before;
                if (add < h)
                {
                    var row = src.AsSpan(add * w + x0, n);
                    for (int i = 0; i < n; i++) sum[i] += row[i];
                }
                if (remove >= 0)
                {
                    var row = src.AsSpan(remove * w + x0, n);
                    for (int i = 0; i < n; i++) sum[i] -= row[i];
                }
            }
        });
    }

    /// <summary>Separable convolution with the symmetric kernel <paramref name="kernel"/> (index 0 is the center).</summary>
    public static float[] Convolve(float[] src, int w, int h, float[] kernel)
    {
        int r = kernel.Length - 1;
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float sum = src[row + x] * kernel[0];
                for (int k = 1; k <= r; k++)
                {
                    if (x - k >= 0) sum += src[row + x - k] * kernel[k];
                    if (x + k < w) sum += src[row + x + k] * kernel[k];
                }
                tmp[row + x] = sum;
            }
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float sum = tmp[y * w + x] * kernel[0];
                for (int k = 1; k <= r; k++)
                {
                    if (y - k >= 0) sum += tmp[(y - k) * w + x] * kernel[k];
                    if (y + k < h) sum += tmp[(y + k) * w + x] * kernel[k];
                }
                dst[y * w + x] = sum;
            }
        });
        return dst;
    }

    private static void BoxHorizontal(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            float sum = 0;
            for (int x = -r; x <= r; x++) sum += At(src, row, w, x);
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = sum * inv;
                sum += At(src, row, w, x + r + 1) - At(src, row, w, x - r);
            }
        });

        static float At(float[] s, int row, int w, int x) => x < 0 || x >= w ? 0f : s[row + x];
    }

    private static void BoxVertical(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, w, x =>
        {
            float sum = 0;
            for (int y = -r; y <= r; y++) sum += At(src, w, h, x, y);
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = sum * inv;
                sum += At(src, w, h, x, y + r + 1) - At(src, w, h, x, y - r);
            }
        });

        static float At(float[] s, int w, int h, int x, int y) => y < 0 || y >= h ? 0f : s[y * w + x];
    }

    /// <summary>
    /// Euclidean distance from each pixel to the nearest pixel where <paramref name="inside"/> holds
    /// (Felzenszwalb &amp; Huttenlocher's linear-time transform). Inside pixels get 0.
    /// </summary>
    public static float[] DistanceTo(float[] field, int w, int h, Func<float, bool> inside)
    {
        const float Inf = 1e20f;
        var d = new float[w * h];
        for (int i = 0; i < d.Length; i++) d[i] = inside(field[i]) ? 0f : Inf;

        Parallel.For(0, w, () => (new float[h], new float[h], new int[h], new float[h + 1]), (x, _, buf) =>
        {
            var (f, o, v, z) = buf;
            for (int y = 0; y < h; y++) f[y] = d[y * w + x];
            Transform1D(f, o, v, z, h);
            for (int y = 0; y < h; y++) d[y * w + x] = o[y];
            return buf;
        }, _ => { });

        Parallel.For(0, h, () => (new float[w], new float[w], new int[w], new float[w + 1]), (y, _, buf) =>
        {
            var (f, o, v, z) = buf;
            Array.Copy(d, y * w, f, 0, w);
            Transform1D(f, o, v, z, w);
            for (int x = 0; x < w; x++) d[y * w + x] = MathF.Sqrt(o[x]);
            return buf;
        }, _ => { });
        return d;
    }

    /// <summary>1-D squared distance transform of sampled function f (lower envelope of parabolas).</summary>
    private static void Transform1D(float[] f, float[] d, int[] v, float[] z, int n)
    {
        int k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                int p = v[k];
                s = (f[q] + q * q - (f[p] + p * p)) / (2f * q - 2f * p);
                if (s > z[k] || k == 0) break;
                k--;
            }
            if (s <= z[k]) { v[0] = q; z[0] = float.NegativeInfinity; z[1] = float.PositiveInfinity; k = 0; continue; }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            float dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }

    /// <summary>Grows a coverage field outward by <paramref name="radius"/> pixels with an anti-aliased edge.</summary>
    public static float[] Dilate(float[] shape, int w, int h, float radius)
    {
        if (radius <= 0f) return (float[])shape.Clone();
        var dist = DistanceTo(shape, w, h, v => v >= 0.5f);
        var result = new float[shape.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = MathF.Max(shape[i], Math.Clamp(radius + 1f - dist[i], 0f, 1f)); // edge is 0.5 px nearer than the pixel center
        return result;
    }
}
