namespace Strayta.Core.Painting;

/// <summary>
/// How painted color combines with a layer's pixels (the Mode menu of Photoshop's painting tools): the layer blend
/// modes, plus Behind (paint only where the layer is transparent) and Clear (paint transparency).
/// </summary>
public enum PaintMode
{
    Normal,
    Dissolve,
    Behind,
    Clear,

    Darken,
    Multiply,
    ColorBurn,
    LinearBurn,
    DarkerColor,

    Lighten,
    Screen,
    ColorDodge,
    LinearDodge,
    LighterColor,

    Overlay,
    SoftLight,
    HardLight,
    VividLight,
    LinearLight,
    PinLight,
    HardMix,

    Difference,
    Exclusion,
    Subtract,
    Divide,

    Hue,
    Saturation,
    Color,
    Luminosity,
}

/// <summary>
/// Composites paint onto one layer pixel in a <see cref="PaintMode"/>. Shared by <see cref="StrokeBaker"/> and the
/// renderer's live stroke overlay, so what you see while painting is exactly what is committed.
/// </summary>
/// <remarks>
/// Colors are straight (not premultiplied), 0..1. For the blend modes the paint is first mixed with the blend result
/// by the layer's own alpha (the W3C compositing model: over a transparent pixel the paint shows as it is, over an
/// opaque one as B(layer, paint)), then laid over the pixel with the paint's coverage. The blend functions follow
/// the published Photoshop definitions (the same ones the renderer uses for layer blending).
/// </remarks>
public static class PaintBlender
{
    /// <summary>
    /// Paints <paramref name="paint"/> with coverage <paramref name="k"/> (brush coverage × opacity × source alpha) over
    /// the pixel (<paramref name="color"/>, <paramref name="alpha"/>), in place; returns the new alpha. Only the first
    /// <paramref name="colors"/> entries are used (1 for grayscale, 3 for RGB). (<paramref name="x"/>,
    /// <paramref name="y"/>) seed Dissolve's per-pixel noise.
    /// </summary>
    public static float Paint(PaintMode mode, Span<float> color, float alpha, ReadOnlySpan<float> paint, float k, int colors, int x, int y)
    {
        if (k <= 0f) return alpha;
        switch (mode)
        {
            case PaintMode.Clear:
                return alpha * (1f - k);
            case PaintMode.Behind:
            {
                float na = alpha + k * (1f - alpha);
                if (na <= 0f) return 0f;
                for (int c = 0; c < colors; c++) color[c] = (color[c] * alpha + paint[c] * k * (1f - alpha)) / na;
                return na;
            }
            case PaintMode.Dissolve:
                // Each pixel is either fully painted or untouched, with probability k (fixed per pixel, so the live
                // preview and the committed stroke agree).
                return Over(color, alpha, paint, Noise(x, y) < k ? 1f : 0f, colors);
            case PaintMode.Normal:
                return Over(color, alpha, paint, k, colors);
        }

        // Blend modes: B(layer, paint), faded toward the paint where the layer is transparent.
        Span<float> blended = stackalloc float[3];
        if (colors == 3 && !IsSeparable(mode))
        {
            var (r, g, b) = NonSeparable(mode, color[0], color[1], color[2], paint[0], paint[1], paint[2]);
            (blended[0], blended[1], blended[2]) = (r, g, b);
        }
        else if (colors == 1 && !IsSeparable(mode))
        {
            // Gray has no hue or saturation: Color and Hue keep the layer's gray, Luminosity takes the paint's.
            blended[0] = mode switch
            {
                PaintMode.Luminosity => paint[0],
                PaintMode.DarkerColor => MathF.Min(color[0], paint[0]),
                PaintMode.LighterColor => MathF.Max(color[0], paint[0]),
                _ => color[0],
            };
        }
        else
        {
            for (int c = 0; c < colors; c++) blended[c] = Separable(mode, color[c], paint[c]);
        }
        for (int c = 0; c < colors; c++) blended[c] = paint[c] + (blended[c] - paint[c]) * alpha;
        return Over(color, alpha, blended, k, colors);
    }

    /// <summary>Source-over with coverage <paramref name="k"/>.</summary>
    private static float Over(Span<float> color, float alpha, ReadOnlySpan<float> paint, float k, int colors)
    {
        if (k <= 0f) return alpha;
        float na = k + alpha * (1f - k);
        for (int c = 0; c < colors; c++) color[c] = na > 0f ? (paint[c] * k + color[c] * alpha * (1f - k)) / na : paint[c];
        return na;
    }

    /// <summary>A fixed pseudo-random value in [0, 1) for a pixel.</summary>
    private static float Noise(int x, int y)
    {
        uint h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return (h >> 8) * (1f / (1 << 24));
    }

    public static bool IsSeparable(PaintMode mode) => mode is not
        (PaintMode.Hue or PaintMode.Saturation or PaintMode.Color or PaintMode.Luminosity or PaintMode.DarkerColor or PaintMode.LighterColor);

    /// <summary>A separable blend B(backdrop <paramref name="b"/>, source <paramref name="s"/>).</summary>
    public static float Separable(PaintMode mode, float b, float s) => mode switch
    {
        PaintMode.Multiply => b * s,
        PaintMode.Screen => b + s - b * s,
        PaintMode.Overlay => HardLight(s, b),
        PaintMode.Darken => MathF.Min(b, s),
        PaintMode.Lighten => MathF.Max(b, s),
        PaintMode.ColorDodge => ColorDodge(b, s),
        PaintMode.ColorBurn => ColorBurn(b, s),
        PaintMode.LinearBurn => MathF.Max(0f, b + s - 1f),
        PaintMode.LinearDodge => MathF.Min(1f, b + s),
        PaintMode.HardLight => HardLight(b, s),
        PaintMode.SoftLight => s <= 0.5f ? 2f * b * s + b * b * (1f - 2f * s) : 2f * b * (1f - s) + MathF.Sqrt(b) * (2f * s - 1f),
        PaintMode.VividLight => s <= 0.5f ? ColorBurn(b, 2f * s) : ColorDodge(b, 2f * (s - 0.5f)),
        PaintMode.LinearLight => Math.Clamp(b + 2f * s - 1f, 0f, 1f),
        PaintMode.PinLight => s <= 0.5f ? MathF.Min(b, 2f * s) : MathF.Max(b, 2f * s - 1f),
        PaintMode.HardMix => b + s >= 1f ? 1f : 0f,
        PaintMode.Difference => MathF.Abs(b - s),
        PaintMode.Exclusion => b + s - 2f * b * s,
        PaintMode.Subtract => MathF.Max(0f, b - s),
        PaintMode.Divide => s <= 0f ? (b <= 0f ? 0f : 1f) : MathF.Min(1f, b / s),
        _ => s,
    };

    /// <summary>The whole-color blends (backdrop b, source s).</summary>
    public static (float R, float G, float B) NonSeparable(PaintMode mode, float br, float bg, float bb, float sr, float sg, float sb) => mode switch
    {
        PaintMode.Hue => SetLum(SetSat(sr, sg, sb, Sat(br, bg, bb)), Lum(br, bg, bb)),
        PaintMode.Saturation => SetLum(SetSat(br, bg, bb, Sat(sr, sg, sb)), Lum(br, bg, bb)),
        PaintMode.Color => SetLum((sr, sg, sb), Lum(br, bg, bb)),
        PaintMode.Luminosity => SetLum((br, bg, bb), Lum(sr, sg, sb)),
        PaintMode.DarkerColor => sr + sg + sb < br + bg + bb ? (sr, sg, sb) : (br, bg, bb),
        PaintMode.LighterColor => sr + sg + sb > br + bg + bb ? (sr, sg, sb) : (br, bg, bb),
        _ => (sr, sg, sb),
    };

    private static float HardLight(float b, float s) => s <= 0.5f ? 2f * b * s : 1f - 2f * (1f - b) * (1f - s);

    private static float ColorDodge(float b, float s) => b <= 0f ? 0f : s >= 1f ? 1f : MathF.Min(1f, b / (1f - s));

    private static float ColorBurn(float b, float s) => b >= 1f ? 1f : s <= 0f ? 0f : 1f - MathF.Min(1f, (1f - b) / s);

    internal static float Lum(float r, float g, float b) => 0.3f * r + 0.59f * g + 0.11f * b;

    private static float Sat(float r, float g, float b) => MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));

    internal static (float, float, float) SetLum((float R, float G, float B) c, float l)
    {
        float d = l - Lum(c.R, c.G, c.B);
        float r = c.R + d, g = c.G + d, b = c.B + d;
        float lum = Lum(r, g, b), n = MathF.Min(r, MathF.Min(g, b)), x = MathF.Max(r, MathF.Max(g, b));
        if (n < 0f && lum - n > 0f)
        {
            float k = lum / (lum - n);
            (r, g, b) = (lum + (r - lum) * k, lum + (g - lum) * k, lum + (b - lum) * k);
        }
        if (x > 1f && x - lum > 0f)
        {
            float k = (1f - lum) / (x - lum);
            (r, g, b) = (lum + (r - lum) * k, lum + (g - lum) * k, lum + (b - lum) * k);
        }
        return (r, g, b);
    }

    private static (float, float, float) SetSat(float r, float g, float b, float s)
    {
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        if (max <= min) return (0f, 0f, 0f);
        float k = s / (max - min);
        return ((r - min) * k, (g - min) * k, (b - min) * k);
    }
}
