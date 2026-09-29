using Strayta.Core;

namespace Strayta.Rendering;

// Glows and the Quality settings shared by shadows, glows and satin: technique, contour, range, noise and jitter.
internal static partial class EffectRenderer
{
    /// <summary>
    /// Outer glow: the shape grown by the spread and faded over the rest of the size, either blurred (Softer) or by
    /// exact distance (Precise); then the Quality settings. A gradient glow takes its colors (and opacities) from the
    /// gradient along that falloff, from the edge outwards.
    /// </summary>
    private static (float[] Alpha, float[]? Rgb) OuterGlow(EffectField f, OuterGlowEffect g)
    {
        float[] falloff;
        if (g.Technique == GlowTechnique.Precise)
        {
            var d = f.DistanceToShape;
            float solid = g.Spread * g.Size, fade = MathF.Max(g.Size * (1f - g.Spread), 1e-3f);
            falloff = new float[d.Length];
            for (int i = 0; i < d.Length; i++)
                falloff[i] = MathF.Max(f.Shape[i], Math.Clamp(1f - (d[i] - 0.5f - solid) / fade, 0f, 1f));
        }
        else falloff = FieldOps.Blur(FieldOps.Dilate(f.Shape, f.W, f.H, g.Spread * g.Size), f.W, f.H, g.Size * (1f - g.Spread));
        return GlowColors(f, falloff, g.Gradient, g.Contour, g.AntiAliased, g.Noise, g.Range, g.Jitter, inside: false, edge: g.Technique == GlowTechnique.Precise ? 1f : 0.5f);
    }

    /// <summary>Inner glow from the edges (or from the center), inside the shape; see <see cref="OuterGlow"/>.</summary>
    private static (float[] Alpha, float[]? Rgb) InnerGlow(EffectField f, InnerGlowEffect g)
    {
        float[] edge;
        if (g.Technique == GlowTechnique.Precise)
        {
            var d = f.DistanceToOutside;
            float solid = g.Choke * g.Size, fade = MathF.Max(g.Size * (1f - g.Choke), 1e-3f);
            edge = new float[d.Length];
            for (int i = 0; i < d.Length; i++) edge[i] = Math.Clamp(1f - (d[i] - 0.5f - solid) / fade, 0f, 1f);
        }
        else edge = Outside(f.Shape, f.W, f.H, g.Choke * g.Size, g.Size * (1f - g.Choke), 0, 0);
        if (g.FromCenter)
            for (int i = 0; i < edge.Length; i++) edge[i] = 1f - edge[i];
        var (alpha, rgb) = GlowColors(f, edge, g.Gradient, g.Contour, g.AntiAliased, g.Noise, g.Range, g.Jitter, inside: true, edge: g.Technique == GlowTechnique.Precise ? 1f : 0.5f);
        for (int i = 0; i < alpha.Length; i++) alpha[i] *= f.Shape[i];
        return (alpha, rgb);
    }

    /// <summary>
    /// Range, contour and noise applied to a glow's falloff; for a gradient glow, the gradient sampled along it
    /// (jittered) gives the color and the opacity, and the glow ends where the falloff does.
    /// </summary>
    private static (float[] Alpha, float[]? Rgb) GlowColors(EffectField f, float[] falloff, Gradient? gradient, Contour contour,
        bool antiAliased, float noise, float range, float jitter, bool inside, float edge)
    {
        int n = falloff.Length;
        if (RangeMap.For(contour, antiAliased, range) is { } map)
            for (int i = 0; i < n; i++) falloff[i] = map.Apply(falloff[i]);

        if (gradient is null)
        {
            if (noise > 0f) Noise(falloff, noise, f, salt: inside ? 3u : 2u);
            return (falloff, null);
        }

        var rgb = new float[n * 3];
        var alpha = new float[n];
        int w = f.W;
        Parallel.For(0, f.H, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float a = falloff[i];
                if (a <= 0f) continue;
                float t = 1f - MathF.Min(1f, a / edge);
                if (jitter > 0f) t += jitter * (Hash(f.Area.Left + x, f.Area.Top + y, 7u) - 0.5f);
                var (c, o) = gradient.Sample(Math.Clamp(t, 0f, 1f));
                rgb[i * 3] = c.R;
                rgb[i * 3 + 1] = c.G;
                rgb[i * 3 + 2] = c.B;
                // Opaque gradients make a solid ring the size of the glow; its outer edge stays soft for a pixel or so.
                alpha[i] = o * Math.Clamp(a * 16f, 0f, 1f);
            }
        });
        if (noise > 0f) Noise(alpha, noise, f, salt: inside ? 3u : 2u);
        return (alpha, rgb);
    }

    /// <summary>A shadow's contour and noise, applied to its alpha in place.</summary>
    private static void Quality(float[] alpha, Contour contour, bool antiAliased, float noise, EffectField f)
    {
        if (!contour.IsIdentity)
        {
            var lut = ContourLut.Of(contour, antiAliased);
            for (int i = 0; i < alpha.Length; i++) alpha[i] = lut.Map(alpha[i]);
        }
        if (noise > 0f) Noise(alpha, noise, f, salt: 1u);
    }

    /// <summary>
    /// Photoshop's Noise: each pixel's opacity varies randomly by up to <paramref name="amount"/> of itself, keeping the
    /// average. The grain is fixed to document pixels, so it does not crawl while the layer or a slider moves.
    /// </summary>
    private static void Noise(float[] alpha, float amount, EffectField f, uint salt)
    {
        int w = f.W, left = f.Area.Left, top = f.Area.Top;
        Parallel.For(0, f.H, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (alpha[i] <= 0f) continue;
                alpha[i] = Math.Clamp(alpha[i] * (1f + amount * (2f * Hash(left + x, top + y, salt) - 1f)), 0f, 1f);
            }
        });
    }

    /// <summary>A repeatable pseudo-random value in [0, 1) for a pixel.</summary>
    internal static float Hash(int x, int y, uint salt)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u + salt * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216f;
    }
}

/// <summary>
/// A glow's (or bevel's) contour together with its Range. At 50% the contour spans the whole falloff. Below 50% the
/// falloff is stretched (range 25% maps the first half of the contour onto the whole falloff and saturates beyond), so
/// the glow gets harder and fuller, as Photoshop's does. Above 50% the contour's input starts partway up its curve and
/// the value it starts from is taken off, so a Linear contour leaves the glow as it is (which matches glows Photoshop
/// rendered at 100%) while shaped contours move outwards.
/// </summary>
internal sealed class RangeMap
{
    private readonly ContourLut _lut;
    private readonly float _low, _span, _start;

    private RangeMap(Contour contour, bool antiAliased, float range)
    {
        _lut = ContourLut.Of(contour, antiAliased);
        float r = Math.Clamp(range, 0.01f, 1f);
        _span = 2f * r;
        _low = r > 0.5f ? 1f - _span : 0f;
        _start = _low < 0f ? _lut.Map(-_low / _span) : 0f;
    }

    /// <summary>Null when the falloff stays as it is (a Linear contour at 50% or more).</summary>
    public static RangeMap? For(Contour contour, bool antiAliased, float range) =>
        contour.IsIdentity && !antiAliased && range >= 0.5f - 1e-4f ? null : new RangeMap(contour, antiAliased, range);

    public float Apply(float g)
    {
        if (g <= 0f) return 0f;
        float v = _lut.Map(Math.Clamp((g - _low) / _span, 0f, 1f));
        if (_start <= 0f) return v;
        return _start < 0.999f ? Math.Clamp((v - _start) / (1f - _start), 0f, 1f) : 0f;
    }
}

/// <summary>
/// A contour as a lookup table on 0..1: natural cubic splines between corner points (Photoshop's contour editor draws
/// smooth curves through its points and breaks them at corners), clamped to 0..1. Anti-aliasing softens corners and
/// steps by averaging the table over a small window. Tables are cached per contour.
/// </summary>
internal sealed class ContourLut
{
    private const int Size = 1024;
    private readonly float[] _table = new float[Size + 1];

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Contour, ContourLut[]> Cache = new();

    public static ContourLut Of(Contour contour, bool antiAliased)
    {
        var both = Cache.GetValue(contour, c => new ContourLut[2]);
        int k = antiAliased ? 1 : 0;
        lock (both) return both[k] ??= new ContourLut(contour, antiAliased);
    }

    private ContourLut(Contour contour, bool antiAliased)
    {
        var points = contour.Points.OrderBy(p => p.X).ToList();
        if (points.Count < 2) points = [.. Contour.Linear.Points];
        // Smooth pieces run between corners (and the end points).
        int start = 0;
        for (int i = 1; i < points.Count; i++)
        {
            if (!points[i].Corner && i < points.Count - 1) continue;
            Fill(points.GetRange(start, i - start + 1));
            start = i;
        }
        float x0 = points[0].X / 255f, x1 = points[^1].X / 255f;
        for (int i = 0; i <= Size; i++)
        {
            float x = i / (float)Size;
            if (x < x0) _table[i] = points[0].Y / 255f;
            else if (x > x1) _table[i] = points[^1].Y / 255f;
        }
        if (antiAliased)
        {
            var copy = (float[])_table.Clone();
            const int r = 12;
            for (int i = 0; i <= Size; i++)
            {
                float sum = 0;
                int count = 0;
                for (int j = Math.Max(0, i - r); j <= Math.Min(Size, i + r); j++, count++) sum += copy[j];
                _table[i] = sum / count;
            }
        }
    }

    private void Fill(List<ContourPoint> pts)
    {
        int n = pts.Count;
        var x = pts.Select(p => p.X / 255.0).ToArray();
        var y = pts.Select(p => p.Y / 255.0).ToArray();
        var m = new double[n];
        if (n > 2)
        {
            var u = new double[n];
            for (int i = 1; i < n - 1; i++)
            {
                double sig = (x[i] - x[i - 1]) / Math.Max(x[i + 1] - x[i - 1], 1e-9);
                double p = sig * m[i - 1] + 2;
                m[i] = (sig - 1) / p;
                u[i] = (y[i + 1] - y[i]) / Math.Max(x[i + 1] - x[i], 1e-9) - (y[i] - y[i - 1]) / Math.Max(x[i] - x[i - 1], 1e-9);
                u[i] = (6 * u[i] / Math.Max(x[i + 1] - x[i - 1], 1e-9) - sig * u[i - 1]) / p;
            }
            for (int k = n - 2; k >= 0; k--) m[k] = m[k] * m[k + 1] + u[k];
        }
        int from = (int)Math.Ceiling(x[0] * Size), to = (int)Math.Floor(x[^1] * Size);
        int hi = 1;
        for (int i = Math.Max(0, from); i <= Math.Min(Size, to); i++)
        {
            double v = i / (double)Size;
            while (hi < n - 1 && x[hi] < v) hi++;
            int lo = hi - 1;
            double h = x[hi] - x[lo];
            double r;
            if (h <= 1e-9) r = y[hi];
            else
            {
                double a = (x[hi] - v) / h, b = (v - x[lo]) / h;
                r = a * y[lo] + b * y[hi] + ((a * a * a - a) * m[lo] + (b * b * b - b) * m[hi]) * h * h / 6;
            }
            _table[i] = (float)Math.Clamp(r, 0, 1);
        }
    }

    /// <summary>The contour at <paramref name="v"/> (0..1), interpolated between table entries.</summary>
    public float Map(float v)
    {
        float p = Math.Clamp(v, 0f, 1f) * Size;
        int i = (int)p;
        if (i >= Size) return _table[Size];
        float t = p - i;
        return _table[i] + (_table[i + 1] - _table[i]) * t;
    }
}
