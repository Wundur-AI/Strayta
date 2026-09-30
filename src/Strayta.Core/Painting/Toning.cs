namespace Strayta.Core.Painting;

/// <summary>The toning tools (Photoshop's O slot).</summary>
public enum ToneTool
{
    Dodge,
    Burn,
    Sponge,
}

/// <summary>Which tones the Dodge and Burn tools change most (their Range menu).</summary>
public enum ToneRange
{
    Shadows,
    Midtones,
    Highlights,
}

/// <summary>
/// Settings of a Dodge, Burn or Sponge stroke. Exposure (Dodge, Burn) and Flow (Sponge) are the brush's
/// <see cref="BrushSettings.Opacity"/> and <see cref="BrushSettings.Flow"/>; see <see cref="Toning"/>.
/// </summary>
public sealed record ToneSettings(ToneTool Tool)
{
    public ToneRange Range { get; init; } = ToneRange.Midtones;

    /// <summary>Dodge / Burn: change brightness through luminance, keeping hue and saturation and avoiding clipping.</summary>
    public bool ProtectTones { get; init; } = true;

    /// <summary>Sponge: true saturates, false desaturates (the Mode menu).</summary>
    public bool Saturate { get; init; }

    /// <summary>Sponge: change dull colors most and never clip (Vibrance).</summary>
    public bool Vibrance { get; init; } = true;
}

/// <summary>
/// The tone curves of the Dodge, Burn and Sponge tools: pure functions from a pixel's color and a strength
/// <c>t</c> (0..1: stroke coverage × exposure) to its new color. Shared by the stroke baker and the live overlay, so
/// what you see while painting is exactly what is committed.
/// </summary>
/// <remarks>
/// <para>These are Strayta's own models, built to behave as the tools are described, not copies of Photoshop's
/// curves. Values are 0..1; every curve fixes t = 0 and is monotonic in the value.</para>
/// <para><b>Dodge</b> (lighten), per value v:
/// Shadows <c>v + a·t·(1 − v)²</c> (lifts the darks most, black included);
/// Midtones <c>v^(1 / (1 + g·t))</c> (a gamma that leaves black and white alone);
/// Highlights <c>v + a·t·v²</c> (brightens the lights most, clipping to white as they pass 1).
/// <b>Burn</b> mirrors them through <c>v ↦ 1 − v</c>: Shadows <c>v − a·t·(1 − v)²</c> (crushes the darks), Midtones
/// <c>v^(1 + g·t)</c>, Highlights <c>v − a·t·v²</c> (greys the whites). With a = 0.5 and g = 0.8 a full-strength
/// midtone dodge takes 50% gray to about 68%, and every curve stays monotonic.</para>
/// <para><b>Protect Tones</b> off applies the curve to each channel on its own, which shifts hue and saturation (the
/// classic behaviour). On, the curve is applied to the luma Y (Rec. 601 weights) and the color is scaled by Y′/Y, so
/// hue and saturation stay; where that would push a channel past 1 the color is pulled toward its new gray Y′ just
/// enough to fit, so nothing clips.</para>
/// <para><b>Sponge</b> moves the color along its line through its gray Y: desaturate <c>Y + (C − Y)·(1 − t)</c>,
/// saturate <c>Y + (C − Y)·(1 + t)</c> (channels clip). Vibrance weights the change by the color's own saturation s
/// (max − min channel): saturating by (1 − s)², so dull colors gain most and saturated ones barely move, and
/// limited so no channel clips; desaturating by (0.35 + 0.65·s), so strong colors lose saturation faster than subtle
/// ones and skin tones keep some color. Gray pixels (and grayscale documents and masks) are left alone.</para>
/// </remarks>
public static class Toning
{
    private const float RangeGain = 0.5f;
    private const float GammaGain = 0.8f;

    /// <summary>Applies the tool to one pixel in place: <paramref name="colors"/> channels (1 gray, 3 RGB) at strength <paramref name="t"/>.</summary>
    public static void Apply(ToneSettings tone, Span<float> color, int colors, float t)
    {
        if (t <= 0f) return;
        t = MathF.Min(t, 1f);
        if (tone.Tool == ToneTool.Sponge)
        {
            if (colors >= 3) Sponge(tone, color, t);
            return;
        }
        bool dodge = tone.Tool == ToneTool.Dodge;
        if (colors < 3 || !tone.ProtectTones)
        {
            for (int k = 0; k < colors; k++) color[k] = Math.Clamp(Curve(dodge, tone.Range, color[k], t), 0f, 1f);
            return;
        }

        float r = color[0], g = color[1], b = color[2];
        float y = Luma(r, g, b);
        float y2 = Math.Clamp(Curve(dodge, tone.Range, y, t), 0f, 1f);
        if (y <= 1e-5f)
        {
            // Black has no hue to keep: it lifts to gray.
            color[0] = color[1] = color[2] = y2;
            return;
        }
        float s = y2 / y;
        (r, g, b) = (r * s, g * s, b * s);
        float max = MathF.Max(r, MathF.Max(g, b));
        if (max > 1f)
        {
            // Pull toward the new gray just enough that the brightest channel lands on 1 (keeps hue, gives up saturation).
            float k = (1f - y2) / MathF.Max(max - y2, 1e-6f);
            (r, g, b) = (y2 + (r - y2) * k, y2 + (g - y2) * k, y2 + (b - y2) * k);
        }
        color[0] = Math.Clamp(r, 0f, 1f);
        color[1] = Math.Clamp(g, 0f, 1f);
        color[2] = Math.Clamp(b, 0f, 1f);
    }

    /// <summary>The Dodge (<paramref name="dodge"/>) or Burn curve for <paramref name="range"/> at strength <paramref name="t"/>, unclamped.</summary>
    public static float Curve(bool dodge, ToneRange range, float v, float t)
    {
        v = Math.Clamp(v, 0f, 1f);
        return (dodge, range) switch
        {
            (true, ToneRange.Shadows) => v + RangeGain * t * (1f - v) * (1f - v),
            (true, ToneRange.Midtones) => MathF.Pow(v, 1f / (1f + GammaGain * t)),
            (true, _) => v + RangeGain * t * v * v,
            (false, ToneRange.Shadows) => v - RangeGain * t * (1f - v) * (1f - v),
            (false, ToneRange.Midtones) => MathF.Pow(v, 1f + GammaGain * t),
            (false, _) => v - RangeGain * t * v * v,
        };
    }

    private static void Sponge(ToneSettings tone, Span<float> c, float t)
    {
        float r = c[0], g = c[1], b = c[2];
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float sat = max - min;
        if (sat <= 1e-6f) return;
        float y = Luma(r, g, b);
        float gain;
        if (!tone.Saturate)
        {
            float amount = tone.Vibrance ? t * (0.35f + 0.65f * sat) : t;
            gain = 1f - MathF.Min(amount, 1f);
        }
        else
        {
            float amount = tone.Vibrance ? t * (1f - sat) * (1f - sat) : t;
            gain = 1f + amount;
            if (tone.Vibrance)
            {
                // The largest gain that keeps every channel inside 0..1.
                float limit = float.MaxValue;
                foreach (float v in (ReadOnlySpan<float>)[r, g, b])
                {
                    float d = v - y;
                    if (d > 1e-6f) limit = MathF.Min(limit, (1f - y) / d);
                    else if (d < -1e-6f) limit = MathF.Min(limit, -y / d);
                }
                gain = MathF.Min(gain, MathF.Max(1f, limit));
            }
        }
        c[0] = Math.Clamp(y + (r - y) * gain, 0f, 1f);
        c[1] = Math.Clamp(y + (g - y) * gain, 0f, 1f);
        c[2] = Math.Clamp(y + (b - y) * gain, 0f, 1f);
    }

    /// <summary>Rec. 601 luma, the weights the rest of the painting code uses for gray.</summary>
    public static float Luma(float r, float g, float b) => 0.299f * r + 0.587f * g + 0.114f * b;
}
