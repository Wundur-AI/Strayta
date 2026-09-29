using System.Runtime.CompilerServices;
using Strayta.Core;

namespace Strayta.Rendering;

// Gradient and pattern fills (overlays and strokes) and Satin.
internal static partial class EffectRenderer
{
    /// <summary>
    /// A gradient laid over <paramref name="mask"/>. Aligned with the layer, the gradient spans the bounding box of
    /// the masked pixels; otherwise the canvas. Shape Burst (strokes only) runs across the stroke, from the layer's
    /// edge outwards (or inwards for an inside stroke).
    /// </summary>
    private static (float[] Rgb, float[] Alpha) GradientFillField(GradientFill g, float[] mask, EffectField f, float[] shape,
        StrokeEffect? stroke = null)
    {
        int w = f.W, h = f.H;
        var box = g.AlignWithLayer ? ShapeBounds(mask, w, h)
            : new PixelRect(f.Target.Left - f.Area.Left, f.Target.Top - f.Area.Top, f.Target.Right - f.Area.Left, f.Target.Bottom - f.Area.Top);
        float bw = box.Width, bh = box.Height;
        float cx = box.Left + bw / 2f + g.OffsetX * bw;
        float cy = box.Top + bh / 2f + g.OffsetY * bh;

        double a = g.Angle * Math.PI / 180.0;
        float dirX = (float)Math.Cos(a), dirY = (float)-Math.Sin(a);
        float length = (MathF.Abs(bw * dirX) + MathF.Abs(bh * dirY)) * g.Scale;
        float radius = MathF.Sqrt(bw * bw + bh * bh) / 2f * g.Scale;
        if (length <= 0f) length = 1f;
        if (radius <= 0f) radius = 1f;

        float[]? burst = null;
        if (g.Style == GradientStyle.ShapeBurst)
        {
            burst = new float[mask.Length];
            float size = MathF.Max(stroke?.Size ?? 1f, 1e-3f);
            var toShape = f.DistanceToShape;
            var toOutside = f.DistanceToOutside;
            for (int i = 0; i < burst.Length; i++)
            {
                // Signed distance from the layer's edge, positive outside.
                float sd = shape[i] >= 0.5f ? -(toOutside[i] - 0.5f) : toShape[i] - 0.5f;
                burst[i] = stroke?.Position switch
                {
                    StrokePosition.Inside => -sd / size,
                    StrokePosition.Center => sd / size + 0.5f,
                    _ => sd / size,
                } / g.Scale;
            }
        }

        var rgb = new float[w * h * 3];
        var alpha = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (mask[i] <= 0f) continue;
                float px = x + 0.5f - cx, py = y + 0.5f - cy;
                float along = px * dirX + py * dirY, across = -px * dirY + py * dirX;
                float t = g.Style switch
                {
                    GradientStyle.Radial => MathF.Sqrt(px * px + py * py) / radius,
                    GradientStyle.Reflected => MathF.Abs(along) / (length / 2f),
                    GradientStyle.Diamond => (MathF.Abs(along) + MathF.Abs(across)) / (length / 2f),
                    GradientStyle.Angle => (float)((Math.Atan2(-across, along) / (2 * Math.PI) + 1) % 1),
                    GradientStyle.ShapeBurst => burst![i],
                    _ => along / length + 0.5f,
                };
                if (g.Reverse) t = 1f - t;
                var (color, opacity) = g.Gradient.Sample(t);
                rgb[i * 3] = color.R;
                rgb[i * 3 + 1] = color.G;
                rgb[i * 3 + 2] = color.B;
                alpha[i] = mask[i] * opacity;
            }
        });
        return (rgb, alpha);
    }

    private static PixelRect ShapeBounds(float[] shape, int w, int h)
    {
        int l = w, t = h, r = 0, b = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (shape[y * w + x] > 0.01f)
                {
                    l = Math.Min(l, x); r = Math.Max(r, x + 1);
                    t = Math.Min(t, y); b = Math.Max(b, y + 1);
                }
        return r > l ? new PixelRect(l, t, r, b) : new PixelRect(0, 0, w, h);
    }

    /// <summary>
    /// A pattern tiled over <paramref name="mask"/>: tiles start at the layer's top-left corner when linked with the
    /// layer (so they move with it), at the canvas origin otherwise, shifted by the phase and scaled; scaled tiles
    /// are sampled bilinearly.
    /// </summary>
    private static (float[] Rgb, float[] Alpha) PatternFillField(PatternFill fill, float[] mask, EffectField f)
    {
        var tile = PatternTile.Of(fill.Pattern.Pixels!);
        int w = f.W, h = f.H;
        float scale = MathF.Max(fill.Scale, 0.001f);
        float ox = (fill.LinkWithLayer ? f.ContentBounds.Left : 0) + fill.PhaseX;
        float oy = (fill.LinkWithLayer ? f.ContentBounds.Top : 0) + fill.PhaseY;
        var rgb = new float[w * h * 3];
        var alpha = new float[w * h];
        Parallel.For(0, h, y =>
        {
            Span<float> sample = stackalloc float[4];
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (mask[i] <= 0f) continue;
                float u = (f.Area.Left + x + 0.5f - ox) / scale - 0.5f;
                float v = (f.Area.Top + y + 0.5f - oy) / scale - 0.5f;
                tile.Sample(u, v, sample);
                rgb[i * 3] = sample[0];
                rgb[i * 3 + 1] = sample[1];
                rgb[i * 3 + 2] = sample[2];
                alpha[i] = mask[i] * sample[3];
            }
        });
        return (rgb, alpha);
    }

    /// <summary>
    /// Satin: the shape blurred by the size, as two copies offset by the distance in opposite directions along the
    /// angle; the satin is where they differ, reshaped by the contour and (by default) inverted, inside the shape.
    /// </summary>
    private static float[] Satin(EffectField f, SatinEffect s)
    {
        var blurred = FieldOps.Blur(f.Shape, f.W, f.H, s.Size);
        var (dx, dy) = Offset(s.Angle, s.Distance);
        var a = Shift(blurred, f.W, f.H, dx, dy, 0f);
        var b = Shift(blurred, f.W, f.H, -dx, -dy, 0f);
        var lut = s.Contour.IsIdentity ? null : ContourLut.Of(s.Contour, s.AntiAliased);
        for (int i = 0; i < a.Length; i++)
        {
            float v = MathF.Abs(a[i] - b[i]);
            if (lut is not null) v = lut.Map(v);
            if (s.Invert) v = 1f - v;
            a[i] = v * f.Shape[i];
        }
        return a;
    }
}

/// <summary>A pattern tile as straight RGBA floats, cached per raster, sampled with wrap-around.</summary>
internal sealed class PatternTile
{
    private static readonly ConditionalWeakTable<Raster, PatternTile> Cache = new();

    private readonly float[] _rgba;
    private readonly int _w, _h;

    private PatternTile(Raster r)
    {
        _w = r.Width;
        _h = r.Height;
        _rgba = new float[_w * _h * 4];
        bool gray = r.ColorPlanes.Count < 3;
        for (int i = 0; i < _w * _h; i++)
        {
            for (int c = 0; c < 3; c++) _rgba[i * 4 + c] = r.ColorPlanes[gray ? 0 : c].GetNormalized(i);
            _rgba[i * 4 + 3] = r.Alpha?.GetNormalized(i) ?? 1f;
        }
    }

    public static PatternTile Of(Raster r) => Cache.GetValue(r, x => new PatternTile(x));

    /// <summary>Bilinear sample at tile coordinates (<paramref name="u"/>, <paramref name="v"/>), pixel centers at integers.</summary>
    public void Sample(float u, float v, Span<float> rgba)
    {
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;
        int ax = Wrap(x0, _w), bx = Wrap(x0 + 1, _w), ay = Wrap(y0, _h), by = Wrap(y0 + 1, _h);
        float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        int p00 = (ay * _w + ax) * 4, p10 = (ay * _w + bx) * 4, p01 = (by * _w + ax) * 4, p11 = (by * _w + bx) * 4;
        // Colors are averaged weighted by alpha, so transparent texels do not darken the edge of opaque ones.
        float a00 = _rgba[p00 + 3] * w00, a10 = _rgba[p10 + 3] * w10, a01 = _rgba[p01 + 3] * w01, a11 = _rgba[p11 + 3] * w11;
        float a = a00 + a10 + a01 + a11;
        for (int c = 0; c < 3; c++)
        {
            float sum = _rgba[p00 + c] * a00 + _rgba[p10 + c] * a10 + _rgba[p01 + c] * a01 + _rgba[p11 + c] * a11;
            rgba[c] = a > 0f ? sum / a : _rgba[p00 + c];
        }
        rgba[3] = a;
    }

    /// <summary>The tile's brightness (luma × alpha) at a point: what a bevel texture presses into the surface.</summary>
    public float Height(float u, float v)
    {
        Span<float> s = stackalloc float[4];
        Sample(u, v, s);
        return (0.299f * s[0] + 0.587f * s[1] + 0.114f * s[2]) * s[3];
    }

    private static int Wrap(int v, int n) => ((v % n) + n) % n;
}
