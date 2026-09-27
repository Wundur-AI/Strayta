namespace Strayta.Core;

/// <summary>A color with 0..1 components.</summary>
public readonly record struct RgbColor(float R, float G, float B)
{
    public static RgbColor Black => new(0, 0, 0);
}

public readonly record struct GradientColorStop(float Location, float Midpoint, RgbColor Color);
public readonly record struct GradientOpacityStop(float Location, float Midpoint, float Opacity);

/// <summary>A multi-stop gradient. Locations and midpoints are 0..1; a midpoint is where the blend reaches 50%.</summary>
public sealed record Gradient(IReadOnlyList<GradientColorStop> Colors, IReadOnlyList<GradientOpacityStop> Opacities)
{
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

public enum GradientStyle { Linear, Radial, Angle, Reflected, Diamond }

/// <summary>Layer styles attached to a layer. Sizes and distances are in document pixels, already scaled.</summary>
public sealed record LayerEffects(IReadOnlyList<LayerEffect> Items);

public abstract record LayerEffect
{
    public bool Enabled { get; init; } = true;
    public BlendMode BlendMode { get; init; } = BlendMode.Normal;
    public float Opacity { get; init; } = 1f;
}

public sealed record DropShadowEffect : LayerEffect
{
    public RgbColor Color { get; init; }
    /// <summary>Light angle in degrees, counterclockwise from the right; the shadow falls opposite.</summary>
    public float Angle { get; init; } = 120f;
    public float Distance { get; init; }
    /// <summary>0..1 fraction of <see cref="Size"/> that is solid before the blur starts.</summary>
    public float Spread { get; init; }
    public float Size { get; init; }
    /// <summary>The layer's own shape hides the shadow beneath it when fill opacity is reduced.</summary>
    public bool Knockout { get; init; } = true;
}

public sealed record OuterGlowEffect : LayerEffect
{
    public RgbColor Color { get; init; }
    public float Spread { get; init; }
    public float Size { get; init; }
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

public sealed record StrokeEffect : LayerEffect
{
    public RgbColor Color { get; init; }
    public float Size { get; init; }
    public StrokePosition Position { get; init; }
}

/// <summary>An effect that was read but cannot be rendered yet (bevel, satin, inner glow, ...).</summary>
public sealed record UnsupportedEffect(string Name) : LayerEffect;
