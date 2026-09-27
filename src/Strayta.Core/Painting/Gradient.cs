using System.Numerics;

namespace Strayta.Core.Painting;

/// <summary>Photoshop's five gradient shapes.</summary>
public enum GradientType
{
    /// <summary>Changes along the drag line; beyond either end the end colors continue.</summary>
    Linear,
    /// <summary>Circles around the start point; the drag length is the radius.</summary>
    Radial,
    /// <summary>Sweeps once around the start point, starting (and ending) on the drag line.</summary>
    Angle,
    /// <summary>A linear gradient mirrored at the start point.</summary>
    Reflected,
    /// <summary>Diamonds around the start point, with a corner at the end point.</summary>
    Diamond,
}

/// <summary>
/// A two-stop gradient as the Gradient tool draws it: a shape, the drag from <see cref="Start"/> to
/// <see cref="End"/> (document pixels), and the colors at either end, each with its own opacity (so
/// "Foreground to Transparent" is the foreground color at both ends, fading from 1 to 0).
/// </summary>
/// <remarks>
/// Colors are interpolated in the document's own encoding, which is what Photoshop calls the Classic method.
/// <see cref="Dither"/> adds a fixed, position-dependent offset of up to half a level before rounding, which
/// breaks the visible bands of a long, low-contrast gradient into noise; being a pure function of the pixel's
/// position, the same gradient always produces the same pixels.
/// </remarks>
public sealed record GradientSpec(
    GradientType Type,
    Vector2 Start,
    Vector2 End,
    RgbColor StartColor,
    float StartAlpha,
    RgbColor EndColor,
    float EndAlpha)
{
    /// <summary>Swaps the ends: the end color starts at <see cref="Start"/>.</summary>
    public bool Reverse { get; init; }

    public bool Dither { get; init; }

    /// <summary>Overall opacity, 0..1, applied on top of the stops' own.</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>True for a click without a drag, which Photoshop ignores.</summary>
    public bool IsDegenerate => Vector2.DistanceSquared(Start, End) < 1e-6f;

    /// <summary>The same gradient with its geometry scaled (e.g. by 1/factor for a downsampled preview).</summary>
    public GradientSpec Scaled(float scale) => this with { Start = Start * scale, End = End * scale };

    /// <summary>
    /// Where the pixel center (<paramref name="x"/>, <paramref name="y"/>) falls in the gradient, 0 (start color)
    /// to 1 (end color), after <see cref="Reverse"/>.
    /// </summary>
    public float PositionAt(float x, float y)
    {
        var d = End - Start;
        float len2 = d.LengthSquared();
        if (len2 <= 0f) return Reverse ? 1f : 0f;
        float px = x - Start.X, py = y - Start.Y;
        float t = Type switch
        {
            GradientType.Linear => (px * d.X + py * d.Y) / len2,
            GradientType.Radial => MathF.Sqrt((px * px + py * py) / len2),
            GradientType.Angle => AngleFraction(px, py, d),
            GradientType.Reflected => MathF.Abs(px * d.X + py * d.Y) / len2,
            GradientType.Diamond => (MathF.Abs(px * d.X + py * d.Y) + MathF.Abs(px * d.Y - py * d.X)) / len2,
            _ => 0f,
        };
        t = Math.Clamp(t, 0f, 1f);
        return Reverse ? 1f - t : t;
    }

    /// <summary>
    /// The angle from the drag line to the pixel as a fraction of a full turn, counterclockwise on screen (y points
    /// down), in [0, 1): the start color lies along the drag line and the sweep ends just before it.
    /// </summary>
    private static float AngleFraction(float px, float py, Vector2 d)
    {
        if (px == 0f && py == 0f) return 0f;
        // Screen counterclockwise is mathematically clockwise with y down, hence the negated cross product.
        float cross = d.X * py - d.Y * px, dot = d.X * px + d.Y * py;
        float a = MathF.Atan2(-cross, dot); // -π..π, 0 on the drag line
        if (a < 0f) a += 2f * MathF.PI;
        float f = a / (2f * MathF.PI);
        return f >= 1f ? 0f : f;
    }

    /// <summary>Color and opacity (0..1, <see cref="Opacity"/> included) at position <paramref name="t"/>.</summary>
    public (float R, float G, float B, float A) ColorAt(float t) => (
        StartColor.R + (EndColor.R - StartColor.R) * t,
        StartColor.G + (EndColor.G - StartColor.G) * t,
        StartColor.B + (EndColor.B - StartColor.B) * t,
        (StartAlpha + (EndAlpha - StartAlpha) * t) * Opacity);

    /// <summary>
    /// The dither offset at a pixel, in output levels, within ±0.49: an integer hash of the position, so it is the
    /// same for every render of the same pixel and uncorrelated between neighbors. Staying short of half a level
    /// means a value that is already exactly on a level (a flat color, full opacity) always rounds back to it.
    /// </summary>
    public static float DitherAt(int x, int y)
    {
        uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return ((h >> 8) * (1f / (1 << 24)) - 0.5f) * 0.98f;
    }
}
