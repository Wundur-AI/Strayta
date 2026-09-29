namespace Strayta.Core;

/// <summary>Where a bevel sits relative to the layer's edge (Photoshop's Bevel &amp; Emboss Style).</summary>
public enum BevelStyle
{
    /// <summary>Inside the edge: the layer looks raised.</summary>
    InnerBevel,
    /// <summary>Outside the edge, on what lies beneath.</summary>
    OuterBevel,
    /// <summary>Across the edge: raised against the layers beneath.</summary>
    Emboss,
    /// <summary>Across the edge, pressed in: the edge is stamped into the layers beneath.</summary>
    PillowEmboss,
    /// <summary>On the layer's Stroke effect only.</summary>
    StrokeEmboss,
}

/// <summary>How the bevel's slope is shaped: rounded (blurred), a hard exact ramp, or a softened ramp.</summary>
public enum BevelTechnique { Smooth, ChiselHard, ChiselSoft }

/// <summary>
/// Bevel &amp; Emboss: the layer's edge lit as a raised (or sunken) surface, drawn as a highlight and a shadow with
/// their own blend modes, colors and opacities. The effect's own <see cref="LayerEffect.BlendMode"/> and
/// <see cref="LayerEffect.Opacity"/> are not used; see <see cref="HighlightMode"/> and <see cref="ShadowMode"/>.
/// </summary>
public sealed record BevelEffect : LayerEffect
{
    public BevelStyle Style { get; init; }
    public BevelTechnique Technique { get; init; }
    /// <summary>0.01..10 (1 = Photoshop's 100%): how steep the slope is.</summary>
    public float Depth { get; init; } = 1f;
    /// <summary>Direction Up (raised); false is Down (sunken).</summary>
    public bool Up { get; init; } = true;
    /// <summary>The bevel's width in pixels.</summary>
    public float Size { get; init; } = 5f;
    /// <summary>Blurs the shading, in pixels.</summary>
    public float Soften { get; init; }
    /// <inheritdoc cref="DropShadowEffect.Angle"/>
    public float Angle { get; init; } = 120f;
    /// <summary>The light's height above the layer in degrees (0 grazing .. 90 overhead).</summary>
    public float Altitude { get; init; } = 30f;
    /// <summary>Angle and altitude follow the document's global light; both then hold its values.</summary>
    public bool UseGlobalLight { get; init; } = true;
    /// <summary>Maps the lighting to brightness: metallic and glossy looks.</summary>
    public Contour GlossContour { get; init; } = Contour.Linear;
    public bool GlossAntiAliased { get; init; }

    public BlendMode HighlightMode { get; init; } = BlendMode.Screen;
    public RgbColor HighlightColor { get; init; } = new(1, 1, 1);
    public float HighlightOpacity { get; init; } = 0.75f;
    public BlendMode ShadowMode { get; init; } = BlendMode.Multiply;
    public RgbColor ShadowColor { get; init; } = RgbColor.Black;
    public float ShadowOpacity { get; init; } = 0.75f;

    /// <summary>The Contour sub-page: reshapes the bevel's profile (ridges, rings, rounded edges).</summary>
    public bool UseContour { get; init; }
    public Contour Contour { get; init; } = Contour.Linear;
    public bool ContourAntiAliased { get; init; }
    /// <summary>0.01..1: how much of the bevel the contour spans (Photoshop's default 50%).</summary>
    public float ContourRange { get; init; } = 0.5f;

    /// <summary>The Texture sub-page: a pattern pressed into the surface.</summary>
    public bool UseTexture { get; init; }
    public PatternFill? Texture { get; init; }
    /// <summary>-10..10 (1 = 100%): how deep the texture is; negative presses it in.</summary>
    public float TextureDepth { get; init; } = 1f;
    public bool TextureInvert { get; init; }
}

/// <summary>
/// Satin: interior shading from two copies of the layer's shape, blurred and offset in opposite directions along
/// <see cref="Angle"/>; where they differ the satin shows. <see cref="Invert"/> (on by default) swaps light and dark.
/// </summary>
public sealed record SatinEffect : LayerEffect
{
    public RgbColor Color { get; init; }
    public float Angle { get; init; } = 19f;
    public float Distance { get; init; } = 11f;
    public float Size { get; init; } = 14f;
    public Contour Contour { get; init; } = Contour.Linear;
    public bool AntiAliased { get; init; } = true;
    public bool Invert { get; init; } = true;
}

/// <summary>Pattern Overlay: the layer's shape filled with a tiled pattern.</summary>
public sealed record PatternOverlayEffect : LayerEffect
{
    /// <summary>The pattern and how it is laid out; null when the file named no pattern (nothing is drawn).</summary>
    public PatternFill? Fill { get; init; }
}
