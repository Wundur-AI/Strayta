namespace Strayta.Core;

/// <summary>A color with 0..1 components.</summary>
public readonly record struct RgbColor(float R, float G, float B)
{
    public static RgbColor Black => new(0, 0, 0);
}

public readonly record struct GradientColorStop(float Location, float Midpoint, RgbColor Color)
{
    /// <summary>
    /// A stop that follows the foreground or background color (Photoshop's stop "Type"); <see cref="Color"/> then
    /// holds the color it had when last resolved. See <see cref="GradientModel.Resolve"/>.
    /// </summary>
    public GradientStopKind Kind { get; init; }
}
public readonly record struct GradientOpacityStop(float Location, float Midpoint, float Opacity);

/// <summary>A multi-stop gradient. Locations and midpoints are 0..1; a midpoint is where the blend reaches 50%.</summary>
public sealed record Gradient(IReadOnlyList<GradientColorStop> Colors, IReadOnlyList<GradientOpacityStop> Opacities)
{
    /// <summary>Photoshop's name for the gradient (e.g. "Black, White"), kept so a saved style shows it again.</summary>
    public string Name { get; init; } = "Custom";

    /// <summary>
    /// Photoshop's Smoothness (the descriptor's "Intr", 0..4096), 0..1: 0 blends linearly between stops, 1 rounds the
    /// corners at interior stops (see Painting.GradientLut). <see cref="Sample"/> ignores it.
    /// </summary>
    public float Smoothness { get; init; } = 1f;

    /// <summary>Set for Photoshop's Noise gradient type; the stops are then unused.</summary>
    public GradientNoise? Noise { get; init; }

    /// <summary>Stops compare by value, so an unchanged gradient is recognised after a round trip.</summary>
    public bool Equals(Gradient? other) =>
        other is not null && Name == other.Name && Colors.SequenceEqual(other.Colors) && Opacities.SequenceEqual(other.Opacities)
        && Smoothness == other.Smoothness && Equals(Noise, other.Noise);

    public override int GetHashCode() => HashCode.Combine(Name, Colors.Count, Opacities.Count);

    /// <summary>Samples the gradient at <paramref name="t"/> (0..1).</summary>
    public (RgbColor Color, float Opacity) Sample(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var (i, u) = Locate(Colors.Select(c => (c.Location, c.Midpoint)).ToList(), t);
        RgbColor color;
        if (Colors.Count == 0) color = RgbColor.Black;
        else if (i < 0) color = Colors[0].Color;
        else if (i >= Colors.Count - 1) color = Colors[^1].Color;
        else
        {
            RgbColor a = Colors[i].Color, b = Colors[i + 1].Color;
            color = new RgbColor(a.R + (b.R - a.R) * u, a.G + (b.G - a.G) * u, a.B + (b.B - a.B) * u);
        }

        var (j, v) = Locate(Opacities.Select(o => (o.Location, o.Midpoint)).ToList(), t);
        float opacity = Opacities.Count == 0 ? 1f
            : j < 0 ? Opacities[0].Opacity
            : j >= Opacities.Count - 1 ? Opacities[^1].Opacity
            : Opacities[j].Opacity + (Opacities[j + 1].Opacity - Opacities[j].Opacity) * v;
        return (color, opacity);
    }

    /// <summary>Finds the stop segment containing t and the blend factor within it, honoring the midpoint.</summary>
    private static (int Index, float Blend) Locate(List<(float Location, float Midpoint)> stops, float t)
    {
        if (stops.Count == 0 || t <= stops[0].Location) return (-1, 0f);
        for (int i = 0; i < stops.Count - 1; i++)
        {
            var (a, mid) = stops[i];
            float b = stops[i + 1].Location;
            if (t > b) continue;
            float u = b > a ? (t - a) / (b - a) : 1f;
            float m = Math.Clamp(mid, 0.001f, 0.999f);
            // Piecewise-linear remap so that u = midpoint gives 0.5.
            return (i, u < m ? 0.5f * u / m : 0.5f + 0.5f * (u - m) / (1f - m));
        }
        return (stops.Count - 1, 0f);
    }
}

/// <summary>
/// How a gradient is laid out. <see cref="ShapeBurst"/> follows the shape's outline (from its edge inwards) and is
/// offered only for strokes, as in Photoshop.
/// </summary>
public enum GradientStyle { Linear, Radial, Angle, Reflected, Diamond, ShapeBurst }

/// <summary>A glow's Technique: Softer blurs the shape; Precise measures the exact distance to its edge.</summary>
public enum GlowTechnique { Softer, Precise }

/// <summary>
/// Layer styles attached to a layer. Sizes and distances are in document pixels, already scaled.
/// Equality is by value: the master switch and the items, where only the order among effects of the same kind
/// matters (each kind has its fixed place in the stack, so files list the kinds in their own order).
/// <see cref="SourceData"/> is not compared.
/// </summary>
public sealed record LayerEffects(IReadOnlyList<LayerEffect> Items)
{
    /// <summary>Photoshop's master switch (the "Effects" eye): off hides every effect but keeps them on the layer.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Opaque data a format reader attaches so its writer can keep settings this model does not represent.</summary>
    public object? SourceData { get; init; }

    public bool Equals(LayerEffects? other) =>
        other is not null && Enabled == other.Enabled && Items.Count == other.Items.Count
        && ByKind(Items).SequenceEqual(ByKind(other.Items));

    /// <summary>A stable sort by kind: keeps the order within a kind.</summary>
    private static IEnumerable<LayerEffect> ByKind(IEnumerable<LayerEffect> items) =>
        items.OrderBy(e => e.GetType().Name, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Enabled, Items.Count);

    /// <summary>The effects that draw something: the master switch is on and the effect's own eye is open.</summary>
    public IEnumerable<LayerEffect> Visible => Enabled ? Items.Where(e => e.Enabled) : [];
}

/// <summary>
/// One layer effect. Records compare by value, except <see cref="SourceData"/>: two effects with the same settings
/// are equal whether they came from a file or from the editor.
/// </summary>
public abstract record LayerEffect
{
    /// <summary>The effect's eye in the Layers panel (Photoshop's "enab"); a hidden effect stays on the layer.</summary>
    public bool Enabled { get; init; } = true;
    public BlendMode BlendMode { get; init; } = BlendMode.Normal;
    public float Opacity { get; init; } = 1f;

    /// <summary>
    /// Opaque data a format reader attaches (for PSD, the effect's own descriptor), so settings this model does not
    /// represent (contour, noise, anti-aliasing, ...) survive editing the ones it does. <c>with</c> copies it along.
    /// </summary>
    public object? SourceData { get; init; }

    public virtual bool Equals(LayerEffect? other) =>
        other is not null && EqualityContract == other.EqualityContract
        && Enabled == other.Enabled && BlendMode == other.BlendMode && Opacity == other.Opacity;

    public override int GetHashCode() => HashCode.Combine(EqualityContract, Enabled, BlendMode, Opacity);
}

public sealed record DropShadowEffect : LayerEffect
{
    public RgbColor Color { get; init; }
    /// <summary>Light angle in degrees, counterclockwise from the right; the shadow falls opposite.</summary>
    public float Angle { get; init; } = 120f;
    /// <summary>
    /// The angle follows the document's global light (<see cref="Document.GlobalLightAngle"/>). <see cref="Angle"/>
    /// then holds that angle, so renderers need not look it up.
    /// </summary>
    public bool UseGlobalLight { get; init; } = true;
    public float Distance { get; init; }
    /// <summary>0..1 fraction of <see cref="Size"/> that is solid before the blur starts.</summary>
    public float Spread { get; init; }
    public float Size { get; init; }
    /// <summary>The layer's own shape hides the shadow beneath it when fill opacity is reduced.</summary>
    public bool Knockout { get; init; } = true;

    /// <summary>Reshapes the shadow's falloff (Photoshop's Quality › Contour).</summary>
    public Contour Contour { get; init; } = Contour.Linear;
    public bool AntiAliased { get; init; }
    /// <summary>0..1: grain mixed into the shadow's opacity.</summary>
    public float Noise { get; init; }
}

/// <summary>A shadow cast inside the layer's edges, as if the layer were a hole: the outside, offset and blurred.</summary>
public sealed record InnerShadowEffect : LayerEffect
{
    public RgbColor Color { get; init; }
    /// <inheritdoc cref="DropShadowEffect.Angle"/>
    public float Angle { get; init; } = 120f;
    /// <inheritdoc cref="DropShadowEffect.UseGlobalLight"/>
    public bool UseGlobalLight { get; init; } = true;
    public float Distance { get; init; }
    /// <summary>0..1 fraction of <see cref="Size"/> that is solid before the blur starts.</summary>
    public float Choke { get; init; }
    public float Size { get; init; }

    /// <summary>Reshapes the shadow's falloff (Photoshop's Quality › Contour).</summary>
    public Contour Contour { get; init; } = Contour.Linear;
    public bool AntiAliased { get; init; }
    /// <summary>0..1: grain mixed into the shadow's opacity.</summary>
    public float Noise { get; init; }
}

public sealed record OuterGlowEffect : LayerEffect
{
    public RgbColor Color { get; init; }
    public float Spread { get; init; }
    public float Size { get; init; }

    /// <summary>A gradient glow (Photoshop's gradient swatch): colors run from the edge outwards. Null for a solid <c>Color</c>.</summary>
    public Gradient? Gradient { get; init; }
    public GlowTechnique Technique { get; init; }
    /// <summary>Reshapes the falloff (Quality › Contour).</summary>
    public Contour Contour { get; init; } = Contour.Linear;
    public bool AntiAliased { get; init; }
    /// <summary>0..1: grain mixed into the glow's opacity.</summary>
    public float Noise { get; init; }
    /// <summary>0.01..1: the part of the glow the contour applies to (Photoshop's default 50%).</summary>
    public float Range { get; init; } = 0.5f;
    /// <summary>0..1: randomizes where a gradient glow samples its colors.</summary>
    public float Jitter { get; init; }
}

/// <summary>A glow inside the layer, from its edges inwards or from its center outwards.</summary>
public sealed record InnerGlowEffect : LayerEffect
{
    public RgbColor Color { get; init; }
    /// <summary>0..1 fraction of <see cref="Size"/> that is solid before the blur starts.</summary>
    public float Choke { get; init; }
    public float Size { get; init; }
    /// <summary>Photoshop's "Center" source: the glow fills the middle and fades towards the edges.</summary>
    public bool FromCenter { get; init; }

    /// <summary>A gradient glow (Photoshop's gradient swatch): colors run from the edge outwards. Null for a solid <c>Color</c>.</summary>
    public Gradient? Gradient { get; init; }
    public GlowTechnique Technique { get; init; }
    /// <summary>Reshapes the falloff (Quality › Contour).</summary>
    public Contour Contour { get; init; } = Contour.Linear;
    public bool AntiAliased { get; init; }
    /// <summary>0..1: grain mixed into the glow's opacity.</summary>
    public float Noise { get; init; }
    /// <summary>0.01..1: the part of the glow the contour applies to (Photoshop's default 50%).</summary>
    public float Range { get; init; } = 0.5f;
    /// <summary>0..1: randomizes where a gradient glow samples its colors.</summary>
    public float Jitter { get; init; }
}

public sealed record ColorOverlayEffect : LayerEffect
{
    public RgbColor Color { get; init; }
}

public sealed record GradientOverlayEffect : LayerEffect
{
    public required Gradient Gradient { get; init; }
    public GradientStyle Style { get; init; }
    public float Angle { get; init; } = 90f;
    /// <summary>Gradient length relative to the layer, 1 = 100%.</summary>
    public float Scale { get; init; } = 1f;
    public bool Reverse { get; init; }
    public bool AlignWithLayer { get; init; } = true;
    /// <summary>Offset of the gradient center as a fraction of the layer width and height.</summary>
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
}

public enum StrokePosition { Outside, Inside, Center }

/// <summary>What a stroke is filled with (Photoshop's Fill Type).</summary>
public enum StrokeFillType { Color, Gradient, Pattern }

public sealed record StrokeEffect : LayerEffect
{
    public RgbColor Color { get; init; }
    public float Size { get; init; }
    public StrokePosition Position { get; init; }
    public StrokeFillType FillType { get; init; }
    /// <summary>The gradient of a gradient stroke (kept when the fill type changes, as Photoshop's dialog does).</summary>
    public GradientFill? GradientFill { get; init; }
    /// <summary>The pattern of a pattern stroke.</summary>
    public PatternFill? PatternFill { get; init; }
}

/// <summary>
/// An effect that was read but cannot be rendered or edited (an unreadable or unknown effect). It stays on the layer,
/// and in the saved file, as it was; only its visibility can change.
/// </summary>
public sealed record UnsupportedEffect(string Name) : LayerEffect;

