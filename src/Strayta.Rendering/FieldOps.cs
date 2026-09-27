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
