namespace Strayta.Core.Painting;

/// <summary>What a painting mode does beyond blending: Photoshop's Behind and Clear exist only for painting.</summary>
public enum PaintModeKind
{
    /// <summary>Blend with <see cref="PaintMode.Blend"/> and composite over the pixels.</summary>
    Blend,
    /// <summary>Paint only where the layer is transparent, as if under it.</summary>
    Behind,
    /// <summary>Erase: remove opacity where painted.</summary>
    Clear,
}

/// <summary>
/// A painting mode as the Gradient, Paint Bucket, Edit › Fill and Edit › Stroke apply it: one of the layer blend modes,
/// or Behind or Clear.
/// </summary>
public readonly record struct PaintMode(BlendMode Blend, PaintModeKind Kind = PaintModeKind.Blend)
{
    public static PaintMode Normal => new(BlendMode.Normal);
    public static PaintMode Behind => new(BlendMode.Normal, PaintModeKind.Behind);
    public static PaintMode Clear => new(BlendMode.Normal, PaintModeKind.Clear);

    public bool IsNormal => Kind == PaintModeKind.Blend && Blend == BlendMode.Normal;

    /// <summary>Photoshop's painting-mode menu, in its order (null marks a separator).</summary>
    public static IReadOnlyList<PaintMode?> Menu { get; } =
    [
        Normal, new(BlendMode.Dissolve), Behind, Clear, null,
        new(BlendMode.Darken), new(BlendMode.Multiply), new(BlendMode.ColorBurn), new(BlendMode.LinearBurn), new(BlendMode.DarkerColor), null,
        new(BlendMode.Lighten), new(BlendMode.Screen), new(BlendMode.ColorDodge), new(BlendMode.LinearDodge), new(BlendMode.LighterColor), null,
        new(BlendMode.Overlay), new(BlendMode.SoftLight), new(BlendMode.HardLight), new(BlendMode.VividLight), new(BlendMode.LinearLight),
        new(BlendMode.PinLight), new(BlendMode.HardMix), null,
        new(BlendMode.Difference), new(BlendMode.Exclusion), new(BlendMode.Subtract), new(BlendMode.Divide), null,
        new(BlendMode.Hue), new(BlendMode.Saturation), new(BlendMode.Color), new(BlendMode.Luminosity),
    ];
}

/// <summary>
/// Paints a color onto one straight-alpha pixel with a <see cref="PaintMode"/>, the way Photoshop's painting tools do.
/// </summary>
/// <remarks>
/// For the blend modes this is the W3C compositing model Photoshop's layer blending also follows: where the pixel is
/// transparent the paint shows as is, where it is opaque the blend B(backdrop, paint) shows, weighted by the
/// backdrop's alpha, and the result is composited source-over with the paint's coverage. Behind composites the
/// pixel over the paint instead; Clear multiplies alpha by (1 − coverage). Dissolve paints a pixel fully or not at
/// all, with a probability equal to the coverage, from a fixed hash of the position (so a redraw gives the same dots).
/// The blend functions follow Photoshop's published definitions (the same ones Strayta.Rendering composites with).
/// </remarks>
public static class PaintBlender
{
    /// <summary>
    /// Paints <paramref name="paint"/> (1 or 3 components, matching <paramref name="color"/>) with coverage
    /// <paramref name="cover"/> (0..1) over the pixel (<paramref name="color"/>, <paramref name="alpha"/>).
    /// </summary>
    public static void Paint(PaintMode mode, Span<float> color, ref float alpha, ReadOnlySpan<float> paint, float cover, int x, int y)
    {
        if (cover <= 0f) return;
        switch (mode.Kind)
        {
            case PaintModeKind.Clear:
                alpha *= 1f - cover;
                return;
            case PaintModeKind.Behind:
            {
                float na = alpha + cover * (1f - alpha);
                if (na <= 0f) return;
                for (int k = 0; k < color.Length; k++) color[k] = (color[k] * alpha + paint[k] * cover * (1f - alpha)) / na;
                alpha = na;
                return;
            }
        }

        if (mode.Blend == BlendMode.Dissolve)
        {
            if (cover < 1f && GradientSpec.DitherAt(x * 7 + 3, y * 13 + 5) + 0.5f >= cover) return;
            cover = 1f;
        }

        float ab = alpha;
        float outA = cover + ab * (1f - cover);
        if (outA <= 0f) return;
        Span<float> blended = stackalloc float[color.Length];
        if (ab <= 0f || mode.Blend is BlendMode.Normal or BlendMode.Dissolve) paint.CopyTo(blended);
        else Blend(mode.Blend, color, paint, blended);
        for (int k = 0; k < color.Length; k++)
        {
            float cs = (1f - ab) * paint[k] + ab * blended[k];
            color[k] = (cover * cs + ab * (1f - cover) * color[k]) / outA;
        }
        alpha = outA;
    }

    /// <summary>B(backdrop, source) for each component; gray documents use the gray value as all three for the non-separable modes.</summary>
    public static void Blend(BlendMode mode, ReadOnlySpan<float> b, ReadOnlySpan<float> s, Span<float> result)
    {
        if (IsSeparable(mode))
        {
            for (int k = 0; k < b.Length; k++) result[k] = Separable(mode, b[k], s[k]);
            return;
        }
        if (b.Length == 1)
        {
            var (g, _, _) = NonSeparable(mode, b[0], b[0], b[0], s[0], s[0], s[0]);
            result[0] = g;
            return;
        }
        var (r, gg, bb) = NonSeparable(mode, b[0], b[1], b[2], s[0], s[1], s[2]);
        (result[0], result[1], result[2]) = (r, gg, bb);
    }

    private static bool IsSeparable(BlendMode mode) => mode is not
        (BlendMode.Hue or BlendMode.Saturation or BlendMode.Color or BlendMode.Luminosity or BlendMode.DarkerColor or BlendMode.LighterColor);

    private static float Separable(BlendMode mode, float b, float s) => mode switch
    {
        BlendMode.Multiply => b * s,
        BlendMode.Screen => b + s - b * s,
        BlendMode.Overlay => HardLight(s, b),
        BlendMode.Darken => MathF.Min(b, s),
        BlendMode.Lighten => MathF.Max(b, s),
        BlendMode.ColorDodge => b <= 0f ? 0f : s >= 1f ? 1f : MathF.Min(1f, b / (1f - s)),
        BlendMode.ColorBurn => ColorBurn(b, s),
        BlendMode.LinearBurn => MathF.Max(0f, b + s - 1f),
        BlendMode.LinearDodge => MathF.Min(1f, b + s),
        BlendMode.HardLight => HardLight(b, s),
        BlendMode.SoftLight => s <= 0.5f ? 2f * b * s + b * b * (1f - 2f * s) : 2f * b * (1f - s) + MathF.Sqrt(b) * (2f * s - 1f),
        BlendMode.VividLight => s <= 0.5f ? ColorBurn(b, 2f * s) : (b <= 0f ? 0f : 2f * (s - 0.5f) >= 1f ? 1f : MathF.Min(1f, b / (1f - 2f * (s - 0.5f)))),
        BlendMode.LinearLight => Math.Clamp(b + 2f * s - 1f, 0f, 1f),
        BlendMode.PinLight => s <= 0.5f ? MathF.Min(b, 2f * s) : MathF.Max(b, 2f * s - 1f),
        BlendMode.HardMix => b + s >= 1f ? 1f : 0f,
        BlendMode.Difference => MathF.Abs(b - s),
        BlendMode.Exclusion => b + s - 2f * b * s,
        BlendMode.Subtract => MathF.Max(0f, b - s),
        BlendMode.Divide => s <= 0f ? (b <= 0f ? 0f : 1f) : MathF.Min(1f, b / s),
        _ => s,
    };

    private static float HardLight(float b, float s) => s <= 0.5f ? 2f * b * s : 1f - 2f * (1f - b) * (1f - s);

    private static float ColorBurn(float b, float s) => b >= 1f ? 1f : s <= 0f ? 0f : 1f - MathF.Min(1f, (1f - b) / s);

    private static (float R, float G, float B) NonSeparable(BlendMode mode, float br, float bg, float bb, float sr, float sg, float sb) => mode switch
    {
        BlendMode.Hue => SetLum(SetSat(sr, sg, sb, Sat(br, bg, bb)), Lum(br, bg, bb)),
        BlendMode.Saturation => SetLum(SetSat(br, bg, bb, Sat(sr, sg, sb)), Lum(br, bg, bb)),
        BlendMode.Color => SetLum((sr, sg, sb), Lum(br, bg, bb)),
        BlendMode.Luminosity => SetLum((br, bg, bb), Lum(sr, sg, sb)),
        BlendMode.DarkerColor => sr + sg + sb < br + bg + bb ? (sr, sg, sb) : (br, bg, bb),
        BlendMode.LighterColor => sr + sg + sb > br + bg + bb ? (sr, sg, sb) : (br, bg, bb),
        _ => (sr, sg, sb),
    };

    private static float Lum(float r, float g, float b) => 0.3f * r + 0.59f * g + 0.11f * b;

    private static float Sat(float r, float g, float b) => MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));

    private static (float, float, float) SetLum((float R, float G, float B) c, float l)
    {
        float d = l - Lum(c.R, c.G, c.B);
        float r = c.R + d, g = c.G + d, b = c.B + d;
        float lum = Lum(r, g, b), n = MathF.Min(r, MathF.Min(g, b)), x = MathF.Max(r, MathF.Max(g, b));
        if (n < 0f && lum - n > 0f) (r, g, b) = (lum + (r - lum) * lum / (lum - n), lum + (g - lum) * lum / (lum - n), lum + (b - lum) * lum / (lum - n));
        if (x > 1f && x - lum > 0f) (r, g, b) = (lum + (r - lum) * (1f - lum) / (x - lum), lum + (g - lum) * (1f - lum) / (x - lum), lum + (b - lum) * (1f - lum) / (x - lum));
        return (r, g, b);
    }

    private static (float, float, float) SetSat(float r, float g, float b, float s)
    {
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        if (max <= min) return (0f, 0f, 0f);
        return ((r - min) * s / (max - min), (g - min) * s / (max - min), (b - min) * s / (max - min));
    }
}
