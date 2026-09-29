namespace Strayta.Core;

/// <summary>Whole-style operations shared by the renderer's previews and the editor: scaling and the global light.</summary>
public static class EffectOperations
{
    /// <summary>
    /// Every size, distance and pattern scale multiplied by <paramref name="factor"/>, as Photoshop's Layer › Layer
    /// Style › Scale Effects does (percentages, angles and colors stay). Used for Scale Effects and for rendering
    /// reduced-resolution previews.
    /// </summary>
    public static LayerEffects Scaled(this LayerEffects effects, float factor) =>
        factor == 1f ? effects : effects with { Items = effects.Items.Select(e => e.Scaled(factor)).ToList() };

    /// <inheritdoc cref="Scaled(LayerEffects, float)"/>
    public static LayerEffect Scaled(this LayerEffect effect, float s) => effect switch
    {
        DropShadowEffect d => d with { Distance = d.Distance * s, Size = d.Size * s },
        InnerShadowEffect d => d with { Distance = d.Distance * s, Size = d.Size * s },
        OuterGlowEffect g => g with { Size = g.Size * s },
        InnerGlowEffect g => g with { Size = g.Size * s },
        StrokeEffect k => k with { Size = k.Size * s, PatternFill = Scaled(k.PatternFill, s) },
        BevelEffect b => b with { Size = b.Size * s, Soften = b.Soften * s, Texture = Scaled(b.Texture, s) },
        SatinEffect t => t with { Distance = t.Distance * s, Size = t.Size * s },
        PatternOverlayEffect p => p with { Fill = Scaled(p.Fill, s) },
        _ => effect,
    };

    private static PatternFill? Scaled(PatternFill? fill, float s) =>
        fill is null ? null : fill with { Scale = fill.Scale * s, PhaseX = fill.PhaseX * s, PhaseY = fill.PhaseY * s };

    /// <summary>Uses the document's global light: shadows and bevels (and only those) can follow it.</summary>
    public static bool UsesGlobalLight(this LayerEffect e) =>
        e is DropShadowEffect { UseGlobalLight: true } or InnerShadowEffect { UseGlobalLight: true } or BevelEffect { UseGlobalLight: true };

    /// <summary>
    /// <paramref name="effects"/> with every effect that uses the global light turned to <paramref name="angle"/> (and
    /// bevels raised to <paramref name="altitude"/>), or the same instance when none changes, so unchanged layers keep
    /// their identity (and their saved bytes).
    /// </summary>
    public static LayerEffects? WithGlobalLight(this LayerEffects? effects, float angle, float altitude)
    {
        if (effects is null) return null;
        bool changed = false;
        var items = effects.Items.Select(e =>
        {
            LayerEffect turned = e switch
            {
                DropShadowEffect { UseGlobalLight: true } d when d.Angle != angle => d with { Angle = angle },
                InnerShadowEffect { UseGlobalLight: true } s when s.Angle != angle => s with { Angle = angle },
                BevelEffect { UseGlobalLight: true } b when b.Angle != angle || b.Altitude != altitude => b with { Angle = angle, Altitude = altitude },
                _ => e,
            };
            changed |= !ReferenceEquals(turned, e);
            return turned;
        }).ToList();
        return changed ? effects with { Items = items } : effects;
    }
}
