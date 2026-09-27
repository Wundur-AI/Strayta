using Strayta.Core;

namespace Strayta.Rendering;

/// <summary>
/// Blend functions B(backdrop, source) on straight (non-premultiplied) 0..1 values,
/// following Photoshop's published definitions where they differ from the W3C ones.
/// </summary>
public static class BlendFunctions
{
    /// <summary>
    /// Photoshop's "special eight" modes apply fill opacity by pulling the source toward the mode's
    /// neutral color before blending, instead of fading the blended result.
    /// </summary>
    public static bool UsesFillScaling(BlendMode mode) => mode is
        BlendMode.ColorDodge or BlendMode.LinearDodge or BlendMode.ColorBurn or BlendMode.LinearBurn
        or BlendMode.VividLight or BlendMode.LinearLight or BlendMode.Difference;

    /// <summary>Scales a source value toward the neutral color of <paramref name="mode"/> by fill <paramref name="f"/>.</summary>
    public static float ScaleForFill(BlendMode mode, float s, float f) => mode switch
    {
        BlendMode.ColorDodge or BlendMode.LinearDodge or BlendMode.Difference => s * f,
        BlendMode.ColorBurn or BlendMode.LinearBurn => 1f - (1f - s) * f,
        BlendMode.VividLight or BlendMode.LinearLight => 0.5f + (s - 0.5f) * f,
        _ => s,
    };

    public static bool IsSeparable(BlendMode mode) => mode is not
        (BlendMode.Hue or BlendMode.Saturation or BlendMode.Color or BlendMode.Luminosity
        or BlendMode.DarkerColor or BlendMode.LighterColor);

    public static float Separable(BlendMode mode, float b, float s) => mode switch
    {
        BlendMode.Normal or BlendMode.Dissolve or BlendMode.PassThrough => s,
        BlendMode.Multiply => b * s,
        BlendMode.Screen => b + s - b * s,
        BlendMode.Overlay => HardLight(s, b),
        BlendMode.Darken => MathF.Min(b, s),
        BlendMode.Lighten => MathF.Max(b, s),
        BlendMode.ColorDodge => ColorDodge(b, s),
        BlendMode.ColorBurn => ColorBurn(b, s),
        BlendMode.LinearBurn => MathF.Max(0f, b + s - 1f),
        BlendMode.LinearDodge => MathF.Min(1f, b + s),
        BlendMode.HardLight => HardLight(b, s),
        BlendMode.SoftLight => s <= 0.5f
            ? 2f * b * s + b * b * (1f - 2f * s)
            : 2f * b * (1f - s) + MathF.Sqrt(b) * (2f * s - 1f),
        BlendMode.VividLight => s <= 0.5f ? ColorBurn(b, 2f * s) : ColorDodge(b, 2f * (s - 0.5f)),
        BlendMode.LinearLight => Math.Clamp(b + 2f * s - 1f, 0f, 1f),
        BlendMode.PinLight => s <= 0.5f ? MathF.Min(b, 2f * s) : MathF.Max(b, 2f * s - 1f),
        BlendMode.HardMix => b + s >= 1f ? 1f : 0f,
        BlendMode.Difference => MathF.Abs(b - s),
        BlendMode.Exclusion => b + s - 2f * b * s,
        BlendMode.Subtract => MathF.Max(0f, b - s),
        BlendMode.Divide => s <= 0f ? (b <= 0f ? 0f : 1f) : MathF.Min(1f, b / s),
        _ => s,
    };

    /// <summary>Non-separable modes operate on the whole color at once.</summary>
    public static (float R, float G, float B) NonSeparable(BlendMode mode, float br, float bg, float bb, float sr, float sg, float sb) => mode switch
    {
        BlendMode.Hue => SetLum(SetSat(sr, sg, sb, Sat(br, bg, bb)), Lum(br, bg, bb)),
        BlendMode.Saturation => SetLum(SetSat(br, bg, bb, Sat(sr, sg, sb)), Lum(br, bg, bb)),
        BlendMode.Color => SetLum((sr, sg, sb), Lum(br, bg, bb)),
        BlendMode.Luminosity => SetLum((br, bg, bb), Lum(sr, sg, sb)),
        BlendMode.DarkerColor => sr + sg + sb < br + bg + bb ? (sr, sg, sb) : (br, bg, bb),
        BlendMode.LighterColor => sr + sg + sb > br + bg + bb ? (sr, sg, sb) : (br, bg, bb),
        _ => (sr, sg, sb),
    };

    private static float HardLight(float b, float s) =>
        s <= 0.5f ? 2f * b * s : 1f - 2f * (1f - b) * (1f - s);

    private static float ColorDodge(float b, float s) =>
        b <= 0f ? 0f : s >= 1f ? 1f : MathF.Min(1f, b / (1f - s));

    private static float ColorBurn(float b, float s) =>
        b >= 1f ? 1f : s <= 0f ? 0f : 1f - MathF.Min(1f, (1f - b) / s);

    private static float Lum(float r, float g, float b) => 0.3f * r + 0.59f * g + 0.11f * b;

    private static float Sat(float r, float g, float b) => MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));

    private static (float, float, float) SetLum((float R, float G, float B) c, float l)
    {
        float d = l - Lum(c.R, c.G, c.B);
        return ClipColor(c.R + d, c.G + d, c.B + d);
    }

    private static (float, float, float) ClipColor(float r, float g, float b)
    {
        float l = Lum(r, g, b);
        float n = MathF.Min(r, MathF.Min(g, b));
        float x = MathF.Max(r, MathF.Max(g, b));
        if (n < 0f)
        {
            float k = l - n;
            if (k > 0f) (r, g, b) = (l + (r - l) * l / k, l + (g - l) * l / k, l + (b - l) * l / k);
        }
        if (x > 1f)
        {
            float k = x - l;
            if (k > 0f) (r, g, b) = (l + (r - l) * (1f - l) / k, l + (g - l) * (1f - l) / k, l + (b - l) * (1f - l) / k);
        }
        return (r, g, b);
    }

    private static (float, float, float) SetSat(float r, float g, float b, float s)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        if (max <= min) return (0f, 0f, 0f);
        float Scale(float v) => (v - min) * s / (max - min);
        return (Scale(r), Scale(g), Scale(b));
    }
}
