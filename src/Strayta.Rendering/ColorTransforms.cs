using Strayta.Core;

namespace Strayta.Rendering;

/// <summary>A per-pixel color function compiled from an <see cref="Adjustment"/>, applied to straight RGB rows.</summary>
internal abstract class ColorTransform
{
    /// <summary>Transforms <paramref name="rgb"/> (3 floats per pixel, 0..1) in place.</summary>
    public abstract void ApplyRow(Span<float> rgb);

    /// <summary>True when the result is only an approximation of Photoshop's.</summary>
    public virtual bool Approximate => false;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Adjustment, ColorTransform?[]> Cache = new();

    /// <summary>
    /// The transform for <paramref name="adjustment"/>, built once per settings object (adjustments are immutable), so
    /// renders during a drag do not re-parse LUT files or re-sample gradients.
    /// </summary>
    public static ColorTransform? Create(Adjustment? adjustment)
    {
        if (adjustment is null) return null;
        return Cache.GetValue(adjustment, a => [Build(a)])[0];
    }

    private static ColorTransform? Build(Adjustment? adjustment) => adjustment switch
    {
        ExposureAdjustment e => LutTransform.FromFunction(v => AdjustmentMath.Exposure(e, v), approximate: true),
        VibranceAdjustment v => v is { Vibrance: 0, Saturation: 0 } ? IdentityTransform.Instance : new VibranceTransform(v),
        ColorBalanceAdjustment c => new ColorBalanceTransform(c),
        BlackWhiteAdjustment bw => new BlackWhiteTransform(bw),
        PhotoFilterAdjustment p => new PhotoFilterTransform(p),
        ChannelMixerAdjustment m => new ChannelMixerTransform(m),
        SelectiveColorAdjustment s => s.Ranges.All(r => r.IsZero) ? IdentityTransform.Instance : new SelectiveColorTransform(s),
        GradientMapAdjustment g => new GradientMapTransform(g),
        ColorLookupAdjustment { Kind: ColorLookupKind.Lut3D } l => LookupTable3D.Parse(l.Data, l.Format) is { } table ? new ColorLookupTransform(table) : null,

        LevelsAdjustment l => LutTransform.FromLevels(l),
        CurvesAdjustment c => LutTransform.FromCurves(c),
        HueSaturationAdjustment h => new HueSaturationTransform(h),
        BrightnessContrastAdjustment b => LutTransform.FromBrightnessContrast(b),
        InvertAdjustment => LutTransform.FromFunction(v => 1f - v),
        PosterizeAdjustment p => LutTransform.FromFunction(v => MathF.Round(v * (p.Levels - 1)) / (p.Levels - 1)),
        ThresholdAdjustment t => new ThresholdTransform(t.Level / 255f),
        _ => null,
    };
}

/// <summary>Leaves colors as they are (an adjustment at its neutral settings; its blend mode still applies).</summary>
internal sealed class IdentityTransform : ColorTransform
{
    public static IdentityTransform Instance { get; } = new();

    public override void ApplyRow(Span<float> rgb)
    {
    }
}

/// <summary>Independent per-channel tone curves, sampled into lookup tables.</summary>
internal sealed class LutTransform(float[] r, float[] g, float[] b, bool approximate = false) : ColorTransform
{
    private const int Size = 4096;

    public override bool Approximate => approximate;

    public override void ApplyRow(Span<float> rgb)
    {
        for (int i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = Lookup(r, rgb[i]);
            rgb[i + 1] = Lookup(g, rgb[i + 1]);
            rgb[i + 2] = Lookup(b, rgb[i + 2]);
        }
    }

    private static float Lookup(float[] lut, float v)
    {
        float x = Math.Clamp(v, 0f, 1f) * (Size - 1);
        int i = (int)x;
        if (i >= Size - 1) return lut[Size - 1];
        float f = x - i;
        return lut[i] + (lut[i + 1] - lut[i]) * f;
    }

    private static float[] Table(Func<float, float> f)
    {
        var t = new float[Size];
        for (int i = 0; i < Size; i++) t[i] = Math.Clamp(f(i / (float)(Size - 1)), 0f, 1f);
        return t;
    }

    public static LutTransform FromFunction(Func<float, float> f, bool approximate = false)
    {
        var t = Table(f);
        return new LutTransform(t, t, t, approximate);
    }

    /// <summary>A lookup table sampling <paramref name="f"/> on 0..1.</summary>
    internal static float[] TableOf(Func<float, float> f) => Table(f);

    /// <summary>Each channel runs through its own record first, then the master record.</summary>
    public static LutTransform FromLevels(LevelsAdjustment a)
    {
        Func<float, float> Channel(int c) =>
            c < a.Channels.Count ? v => Levels(a.Master, Levels(a.Channels[c], v)) : v => Levels(a.Master, v);
        return new LutTransform(Table(Channel(0)), Table(Channel(1)), Table(Channel(2)));
    }

    public static float Levels(LevelsChannel l, float v)
    {
        float x = v * 255f;
        float range = Math.Max(1, l.InputWhite - l.InputBlack);
        float t = Math.Clamp((x - l.InputBlack) / range, 0f, 1f);
        if (l.Gamma != 1f) t = MathF.Pow(t, 1f / l.Gamma);
        return (l.OutputBlack + t * (l.OutputWhite - l.OutputBlack)) / 255f;
    }

    public static LutTransform FromCurves(CurvesAdjustment a)
    {
        var master = a.Master is { Count: >= 2 } m ? Spline(m) : null;
        Func<float, float> Channel(int c)
        {
            var own = c < a.Channels.Count && a.Channels[c] is { Count: >= 2 } pts ? Spline(pts) : null;
            return v =>
            {
                if (own is not null) v = own(v);
                return master is not null ? master(v) : v;
            };
        }
        return new LutTransform(Table(Channel(0)), Table(Channel(1)), Table(Channel(2)), approximate: true);
    }

    /// <summary>Natural cubic spline through the curve points, flat beyond the end points.</summary>
    public static Func<float, float> Spline(IReadOnlyList<CurvePoint> points)
    {
        int n = points.Count;
        var x = points.Select(p => p.Input / 255.0).ToArray();
        var y = points.Select(p => p.Output / 255.0).ToArray();
        var m = new double[n]; // second derivatives
        if (n > 2)
        {
            var u = new double[n];
            for (int i = 1; i < n - 1; i++)
            {
                double sig = (x[i] - x[i - 1]) / (x[i + 1] - x[i - 1]);
                double p = sig * m[i - 1] + 2;
                m[i] = (sig - 1) / p;
                u[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]) - (y[i] - y[i - 1]) / (x[i] - x[i - 1]);
                u[i] = (6 * u[i] / (x[i + 1] - x[i - 1]) - sig * u[i - 1]) / p;
            }
            for (int k = n - 2; k >= 0; k--) m[k] = m[k] * m[k + 1] + u[k];
        }

        return v =>
        {
            if (v <= x[0]) return (float)y[0];
            if (v >= x[n - 1]) return (float)y[n - 1];
            int hi = 1;
            while (x[hi] < v) hi++;
            int lo = hi - 1;
            double h = x[hi] - x[lo];
            if (h <= 0) return (float)y[hi];
            double a = (x[hi] - v) / h, b = (v - x[lo]) / h;
            return (float)(a * y[lo] + b * y[hi] + ((a * a * a - a) * m[lo] + (b * b * b - b) * m[hi]) * h * h / 6);
        };
    }

    /// <summary>
    /// Modern Photoshop Brightness/Contrast is not publicly specified; this uses the classic formulas
    /// (brightness as a lighten/darken toward white/black, contrast as a slope around mid-gray).
    /// </summary>
    public static LutTransform FromBrightnessContrast(BrightnessContrastAdjustment a)
    {
        float br = Math.Clamp(a.Brightness / 150f, -1f, 1f);
        float slope = MathF.Tan((Math.Clamp(a.Contrast / 100f, -1f, 0.99f) + 1f) * MathF.PI / 4f);
        var t = Table(v =>
        {
            v = br < 0 ? v * (1f + br) : v + (1f - v) * br;
            return (v - 0.5f) * slope + 0.5f;
        });
        return new LutTransform(t, t, t, approximate: true);
    }
}

internal sealed class ThresholdTransform(float level) : ColorTransform
{
    public override void ApplyRow(Span<float> rgb)
    {
        for (int i = 0; i < rgb.Length; i += 3)
        {
            float lum = 0.299f * rgb[i] + 0.587f * rgb[i + 1] + 0.114f * rgb[i + 2];
            rgb[i] = rgb[i + 1] = rgb[i + 2] = lum >= level ? 1f : 0f;
        }
    }
}

/// <summary>Hue/Saturation in HSL space, using the widely used GIMP-compatible formulas.</summary>
internal sealed class HueSaturationTransform(HueSaturationAdjustment a) : ColorTransform
{
    public override bool Approximate => true;

    public override void ApplyRow(Span<float> rgb)
    {
        float hueShift = a.Hue / 360f;
        float sat = a.Saturation / 100f;
        float light = (a.Colorize ? a.ColorizeLightness : a.Lightness) / 100f;

        for (int i = 0; i < rgb.Length; i += 3)
        {
            float r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
            if (a.Colorize)
            {
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                (r, g, b) = HslToRgb(a.ColorizeHue / 360f, a.ColorizeSaturation / 100f, lum);
            }
            else
            {
                var (h, s, l) = RgbToHsl(r, g, b);
                h = (h + hueShift + 1f) % 1f;
                s = Math.Clamp(s * (1f + sat), 0f, 1f);
                (r, g, b) = HslToRgb(h, s, l);
            }

            if (light < 0) { r *= 1f + light; g *= 1f + light; b *= 1f + light; }
            else if (light > 0) { r += (1f - r) * light; g += (1f - g) * light; b += (1f - b) * light; }

            rgb[i] = r;
            rgb[i + 1] = g;
            rgb[i + 2] = b;
        }
    }

    private static (float H, float S, float L) RgbToHsl(float r, float g, float b)
    {
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float l = (max + min) / 2f;
        if (max - min < 1e-6f) return (0f, 0f, l);
        float d = max - min;
        float s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
        float h = max == r ? (g - b) / d + (g < b ? 6f : 0f) : max == g ? (b - r) / d + 2f : (r - g) / d + 4f;
        return (h / 6f, s, l);
    }

    private static (float, float, float) HslToRgb(float h, float s, float l)
    {
        if (s <= 0f) return (l, l, l);
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        return (Hue(p, q, h + 1f / 3f), Hue(p, q, h), Hue(p, q, h - 1f / 3f));
    }

    private static float Hue(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 0.5f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }
}
