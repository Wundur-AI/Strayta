namespace Strayta.Core;

/// <summary>What a gradient color stop shows: its own color, or whatever the foreground or background color is.</summary>
public enum GradientStopKind
{
    User,
    Foreground,
    Background,
}

/// <summary>The color model a noise gradient picks its random colors in.</summary>
public enum NoiseColorModel
{
    Rgb,
    Hsb,
}

/// <summary>A 0..1 range a noise gradient's channel stays inside.</summary>
public readonly record struct ChannelRange(float Min, float Max)
{
    public static ChannelRange Full => new(0f, 1f);
}

/// <summary>
/// Photoshop's Noise gradient: random colors along the gradient, with <see cref="Roughness"/> setting how often and
/// how sharply they change, each channel limited to its range. The same <see cref="Seed"/> always gives the same
/// gradient (Photoshop's "Randomize" picks a new seed).
/// </summary>
public sealed record GradientNoise
{
    /// <summary>0..1 (Photoshop's 0–100%).</summary>
    public float Roughness { get; init; } = 0.5f;
    public int Seed { get; init; }
    public NoiseColorModel Model { get; init; } = NoiseColorModel.Rgb;
    /// <summary>Ranges of the model's three channels (R, G, B or H, S, B).</summary>
    public ChannelRange C1 { get; init; } = ChannelRange.Full;
    public ChannelRange C2 { get; init; } = ChannelRange.Full;
    public ChannelRange C3 { get; init; } = ChannelRange.Full;
    /// <summary>Keeps colors away from full saturation (Photoshop's "Restrict Colors").</summary>
    public bool RestrictColors { get; init; }
    /// <summary>Random opacity too (Photoshop's "Add Transparency").</summary>
    public bool AddTransparency { get; init; }
}

/// <summary>Helpers for building and resolving <see cref="Gradient"/>s.</summary>
public static class GradientModel
{
    /// <summary>A two-color gradient, both stops opaque, midpoints at 50%.</summary>
    public static Gradient TwoColor(string name, RgbColor from, RgbColor to) => new(
        [new GradientColorStop(0f, 0.5f, from), new GradientColorStop(1f, 0.5f, to)],
        [new GradientOpacityStop(0f, 0.5f, 1f), new GradientOpacityStop(1f, 0.5f, 1f)]) { Name = name };

    /// <summary>
    /// The gradient with its foreground and background stops set to today's colors (the stops keep their kind, so
    /// the result still follows the colors the next time it is resolved).
    /// </summary>
    public static Gradient Resolve(this Gradient g, RgbColor foreground, RgbColor background)
    {
        if (g.Colors.All(c => c.Kind == GradientStopKind.User)) return g;
        return g with
        {
            Colors = g.Colors.Select(c => c.Kind switch
            {
                GradientStopKind.Foreground => c with { Color = foreground },
                GradientStopKind.Background => c with { Color = background },
                _ => c,
            }).ToList(),
        };
    }

    /// <summary>Stops sorted by location, as the evaluator and the file format expect.</summary>
    public static Gradient Sorted(this Gradient g) => g with
    {
        Colors = g.Colors.OrderBy(c => c.Location).ToList(),
        Opacities = g.Opacities.OrderBy(o => o.Location).ToList(),
    };
}
