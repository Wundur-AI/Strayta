using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Rendering;

/// <summary>
/// The color functions of Exposure, Vibrance, Color Balance, Black &amp; White, Photo Filter, Channel Mixer, Selective
/// Color, Gradient Map and Color Lookup. Each is public as a single-color function (<see cref="Apply"/>) so editors
/// and tests can evaluate exactly what the renderer draws.
/// </summary>
public static class AdjustmentMath
{
    /// <summary>Applies <paramref name="adjustment"/> to one straight RGB color (0..1). Unsupported adjustments return it unchanged.</summary>
    public static (float R, float G, float B) Apply(Adjustment adjustment, float r, float g, float b)
    {
        if (ColorTransform.Create(adjustment) is not { } t) return (r, g, b);
        Span<float> px = [r, g, b];
        t.ApplyRow(px);
        return (px[0], px[1], px[2]);
    }

    /// <summary>
    /// Photoshop's Black &amp; White conversion of one color: the color is split into its gray part (the smallest channel),
    /// a secondary part (yellow, cyan or magenta: the middle channel above the smallest) and a primary part (red,
    /// green or blue: the largest channel above the middle), and each part is weighted by its slider.
    /// </summary>
    public static float BlackWhiteGray(BlackWhiteAdjustment a, float r, float g, float b)
    {
        float max, mid, min, primary, secondary;
        if (r >= g && r >= b)
        {
            primary = a.Reds;
            if (g >= b) { (max, mid, min) = (r, g, b); secondary = a.Yellows; }
            else { (max, mid, min) = (r, b, g); secondary = a.Magentas; }
        }
        else if (g >= b)
        {
            primary = a.Greens;
            if (r >= b) { (max, mid, min) = (g, r, b); secondary = a.Yellows; }
            else { (max, mid, min) = (g, b, r); secondary = a.Cyans; }
        }
        else
        {
            primary = a.Blues;
            if (r >= g) { (max, mid, min) = (b, r, g); secondary = a.Magentas; }
            else { (max, mid, min) = (b, g, r); secondary = a.Cyans; }
        }
        return Math.Clamp(min + (mid - min) * secondary / 100f + (max - mid) * primary / 100f, 0f, 1f);
    }

    /// <summary>
    /// How much of each Selective Color range a color belongs to (0..1), in <see cref="SelectiveColorRange"/> order:
    /// a hue range by how far its channel(s) stand out, whites and blacks by how far the color is above or below
    /// middle gray, neutrals by how close it is to gray.
    /// </summary>
    public static void SelectiveColorWeights(float r, float g, float b, Span<float> weights)
    {
        weights.Clear();
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float mid = r + g + b - max - min;
        float primary = max - mid, secondary = mid - min;
        if (primary > 0)
        {
            if (r == max) weights[(int)SelectiveColorRange.Reds] = primary;
            else if (g == max) weights[(int)SelectiveColorRange.Greens] = primary;
            else weights[(int)SelectiveColorRange.Blues] = primary;
        }
        if (secondary > 0)
        {
            if (b == min) weights[(int)SelectiveColorRange.Yellows] = secondary;
            else if (r == min) weights[(int)SelectiveColorRange.Cyans] = secondary;
            else weights[(int)SelectiveColorRange.Magentas] = secondary;
        }
        if (min > 0.5f) weights[(int)SelectiveColorRange.Whites] = (min - 0.5f) * 2f;
        if (max < 0.5f) weights[(int)SelectiveColorRange.Blacks] = (0.5f - max) * 2f;
        weights[(int)SelectiveColorRange.Neutrals] = MathF.Max(0f, 1f - (MathF.Abs(max - 0.5f) + MathF.Abs(min - 0.5f)));
    }

    /// <summary>
    /// Color Balance's per-channel tone function for one channel's three sliders (-100..100): shadows move the
    /// black end (up by raising the output black, down by raising the input black), highlights the white end, and
    /// midtones bend the middle with a gamma (±100 = a factor of two).
    /// </summary>
    public static float ColorBalanceChannel(float v, int shadows, int midtones, int highlights)
    {
        const float Reach = 0.3f; // how far a full slider moves an end point
        float s = shadows / 100f * Reach, h = highlights / 100f * Reach;
        if (s > 0) v = s + v * (1f - s);
        else if (s < 0) v = Math.Clamp((v + s) / (1f + s), 0f, 1f);
        if (midtones != 0) v = MathF.Pow(Math.Clamp(v, 0f, 1f), MathF.Pow(2f, -midtones / 100f));
        if (h > 0) v = Math.Clamp(v / (1f - h), 0f, 1f);
        else if (h < 0) v *= 1f + h;
        return v;
    }

    /// <summary>
    /// The exposure tone function: in linear light the value is scaled by 2^exposure, the offset added, and the result
    /// raised to the gamma (Photoshop's Gamma Correction runs from 9.99, darkest, to 0.01, brightest; negative values
    /// are mirrored around zero).
    /// </summary>
    public static float Exposure(ExposureAdjustment a, float v)
    {
        float l = GradientLut.SrgbToLinear(Math.Clamp(v, 0f, 1f));
        l = l * MathF.Pow(2f, a.Exposure) + a.Offset;
        float gamma = Math.Clamp(a.Gamma, 0.01f, 9.99f);
        l = l < 0 ? -MathF.Pow(-l, gamma) : MathF.Pow(l, gamma);
        return GradientLut.LinearToSrgb(Math.Clamp(l, 0f, 1f));
    }
}

/// <summary>A per-pixel function built from a per-color function.</summary>
internal abstract class PixelTransform : ColorTransform
{
    protected abstract (float, float, float) Map(float r, float g, float b);

    public override void ApplyRow(Span<float> rgb)
    {
        for (int i = 0; i < rgb.Length; i += 3)
            (rgb[i], rgb[i + 1], rgb[i + 2]) = Map(rgb[i], rgb[i + 1], rgb[i + 2]);
    }

    protected static float Luma(float r, float g, float b) => BlendFunctions.Lum(r, g, b);
}

/// <summary>Vibrance and saturation around each color's luminosity, saturation limited so no channel clips.</summary>
internal sealed class VibranceTransform(VibranceAdjustment a) : PixelTransform
{
    public override bool Approximate => true;

    protected override (float, float, float) Map(float r, float g, float b)
    {
        float l = Luma(r, g, b);
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float chroma = max - min;
        if (chroma <= 1e-6f) return (r, g, b);

        // Scaling (c - l) by k keeps the luminosity; this is the largest k that keeps every channel inside 0..1.
        float limit = MathF.Min(max > l ? (1f - l) / (max - l) : float.MaxValue, min < l ? l / (l - min) : float.MaxValue);

        float k = 1f;
        float v = a.Vibrance / 100f;
        if (v != 0)
        {
            // Vibrance acts mostly on muted colors, and less on skin tones (red above green above blue).
            float muted = 1f - chroma;
            float skin = r > g && g > b ? 0.5f : 1f;
            k *= v > 0 ? 1f + v * muted * muted * skin * 1.5f : 1f + v * muted;
        }
        float s = a.Saturation / 100f;
        if (s != 0) k *= 1f + s;
        k = MathF.Max(0f, k > 1f ? MathF.Min(k, MathF.Max(1f, limit)) : k);
        return (l + (r - l) * k, l + (g - l) * k, l + (b - l) * k);
    }
}

/// <summary>Color Balance: per-channel tone functions, then (optionally) the original luminosity restored.</summary>
internal sealed class ColorBalanceTransform : ColorTransform
{
    private readonly LutTransform _curves;
    private readonly bool _preserve;

    public ColorBalanceTransform(ColorBalanceAdjustment a)
    {
        _preserve = a.PreserveLuminosity;
        _curves = new LutTransform(
            LutTransform.TableOf(v => AdjustmentMath.ColorBalanceChannel(v, a.Shadows.CyanRed, a.Midtones.CyanRed, a.Highlights.CyanRed)),
            LutTransform.TableOf(v => AdjustmentMath.ColorBalanceChannel(v, a.Shadows.MagentaGreen, a.Midtones.MagentaGreen, a.Highlights.MagentaGreen)),
            LutTransform.TableOf(v => AdjustmentMath.ColorBalanceChannel(v, a.Shadows.YellowBlue, a.Midtones.YellowBlue, a.Highlights.YellowBlue)));
    }

    public override bool Approximate => true;

    public override void ApplyRow(Span<float> rgb)
    {
        if (!_preserve)
        {
            _curves.ApplyRow(rgb);
            return;
        }
        Span<float> px = stackalloc float[3];
        for (int i = 0; i < rgb.Length; i += 3)
        {
            float l = BlendFunctions.Lum(rgb[i], rgb[i + 1], rgb[i + 2]);
            px[0] = rgb[i]; px[1] = rgb[i + 1]; px[2] = rgb[i + 2];
            _curves.ApplyRow(px);
            (rgb[i], rgb[i + 1], rgb[i + 2]) = BlendFunctions.SetLum((px[0], px[1], px[2]), l);
        }
    }
}

/// <summary>Black &amp; White, with the tint laid over the gray as a Color blend (the gray's luminosity, the tint's hue and saturation).</summary>
internal sealed class BlackWhiteTransform(BlackWhiteAdjustment a) : PixelTransform
{
    public override bool Approximate => a.Tint;

    protected override (float, float, float) Map(float r, float g, float b)
    {
        float gray = AdjustmentMath.BlackWhiteGray(a, r, g, b);
        if (!a.Tint) return (gray, gray, gray);
        return BlendFunctions.SetLum((a.TintColor.R, a.TintColor.G, a.TintColor.B), gray);
    }
}

/// <summary>Photo Filter: the image multiplied by the filter color at the density, optionally keeping luminosity.</summary>
internal sealed class PhotoFilterTransform(PhotoFilterAdjustment a) : PixelTransform
{
    private readonly float _d = Math.Clamp(a.Density, 0, 100) / 100f;

    public override bool Approximate => true;

    protected override (float, float, float) Map(float r, float g, float b)
    {
        float fr = r + (r * a.Color.R - r) * _d, fg = g + (g * a.Color.G - g) * _d, fb = b + (b * a.Color.B - b) * _d;
        return a.PreserveLuminosity ? BlendFunctions.SetLum((fr, fg, fb), Luma(r, g, b)) : (fr, fg, fb);
    }
}

/// <summary>Channel Mixer: each output channel a weighted sum of the inputs plus a constant.</summary>
internal sealed class ChannelMixerTransform(ChannelMixerAdjustment a) : PixelTransform
{
    private static float Mix(MixerChannel c, float r, float g, float b) =>
        Math.Clamp((c.Red * r + c.Green * g + c.Blue * b + c.Constant) / 100f, 0f, 1f);

    protected override (float, float, float) Map(float r, float g, float b)
    {
        if (a.Monochrome)
        {
            float v = Mix(a.Gray, r, g, b);
            return (v, v, v);
        }
        return (Mix(a.Red, r, g, b), Mix(a.Green, r, g, b), Mix(a.Blue, r, g, b));
    }
}

/// <summary>
/// Selective Color: for each range the color belongs to, the cyan, magenta and yellow inks (the complements of red,
/// green and blue) and black change by the range's percentages, relative to the ink already there or absolutely.
/// </summary>
internal sealed class SelectiveColorTransform(SelectiveColorAdjustment a) : PixelTransform
{
    private readonly SelectiveColorValues[] _v = Enumerable.Range(0, 9).Select(i => a[(SelectiveColorRange)i]).ToArray();

    public override bool Approximate => true;

    protected override (float, float, float) Map(float r, float g, float b)
    {
        Span<float> w = stackalloc float[9];
        AdjustmentMath.SelectiveColorWeights(r, g, b, w);
        float dr = 0, dg = 0, db = 0;
        for (int i = 0; i < 9; i++)
        {
            if (w[i] <= 0) continue;
            var v = _v[i];
            if (v.IsZero) continue;
            float k = v.Black / 100f;
            dr += w[i] * Ink(r, v.Cyan / 100f, k);
            dg += w[i] * Ink(g, v.Magenta / 100f, k);
            db += w[i] * Ink(b, v.Yellow / 100f, k);
        }
        return (Math.Clamp(r - dr, 0f, 1f), Math.Clamp(g - dg, 0f, 1f), Math.Clamp(b - db, 0f, 1f));
    }

    /// <summary>How much a channel darkens when its ink changes by <paramref name="ink"/> and black by <paramref name="black"/>.</summary>
    private float Ink(float v, float ink, float black)
    {
        float scale = a.Absolute ? 1f : 1f - v; // relative changes scale with the ink already present
        float change = (ink + black * (1f + ink)) * scale;
        return Math.Clamp(change, -(1f - v), v);
    }
}

/// <summary>Gradient Map: the luminosity picks a color along the gradient; transparent parts of the gradient show the image.</summary>
internal sealed class GradientMapTransform(GradientMapAdjustment a) : PixelTransform
{
    private readonly GradientLut _lut = GradientLut.Build(a.Gradient, a.Method);

    protected override (float, float, float) Map(float r, float g, float b)
    {
        float t = Math.Clamp(Luma(r, g, b), 0f, 1f);
        if (a.Reverse) t = 1f - t;
        var (gr, gg, gb, ga) = _lut.At(t);
        return ga >= 1f ? (gr, gg, gb) : (r + (gr - r) * ga, g + (gg - g) * ga, b + (gb - b) * ga);
    }
}

/// <summary>Color Lookup through a 3D LUT with tetrahedral interpolation.</summary>
internal sealed class ColorLookupTransform(LookupTable3D table) : PixelTransform
{
    protected override (float, float, float) Map(float r, float g, float b) => table.Apply(r, g, b);
}
