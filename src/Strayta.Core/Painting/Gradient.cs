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
/// A gradient as the Gradient tool draws it: a shape, the drag from <see cref="Start"/> to <see cref="End"/> (document
/// pixels), and the colors: a multi-stop <see cref="Stops"/> gradient, or when that is null the two colors at either
/// end, each with its own opacity (so "Foreground to Transparent" is the foreground color at both ends, fading from 1
/// to 0).
/// </summary>
/// <remarks>
/// Colors are blended in the space <see cref="Method"/> names (Classic, the document's own encoding, by default) and
/// looked up in a <see cref="GradientLut"/> built once per spec. <see cref="Dither"/> adds a fixed, position-dependent
/// offset of up to half a level before rounding, which breaks the visible bands of a long, low-contrast gradient into
/// noise; being a pure function of the pixel's position, the same gradient always produces the same pixels.
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

    /// <summary>A multi-stop (or noise) gradient with its foreground/background stops resolved; null for the two end colors.</summary>
    public Gradient? Stops { get; init; }

    /// <summary>The color space stops blend in (Photoshop's Method option).</summary>
    public GradientMethod Method { get; init; } = GradientMethod.Classic;

    /// <summary>Photoshop's Transparency option: off ignores the opacity stops (everything opaque).</summary>
    public bool Transparency { get; init; } = true;

    /// <summary>How the gradient combines with the pixels it is drawn over (Photoshop's Mode option).</summary>
    public PaintMode Mode { get; init; } = PaintMode.Normal;

    // Built on first use and shared by copies made with `with`; it remembers what it was built from, so a copy with
    // other colors rebuilds it. It never takes part in equality.
    private readonly LutCache _cache = new();

    /// <summary>The gradient's colors as used for drawing: <see cref="Stops"/>, or the two end colors as stops.</summary>
    public Gradient EffectiveGradient => Stops ?? new Gradient(
        [new GradientColorStop(0f, 0.5f, StartColor), new GradientColorStop(1f, 0.5f, EndColor)],
        [new GradientOpacityStop(0f, 0.5f, StartAlpha), new GradientOpacityStop(1f, 0.5f, EndAlpha)]);

    /// <summary>The evaluated gradient (built once; read it once per drawing rather than per pixel).</summary>
    public GradientLut Lut
    {
        get
        {
            var key = new LutKey(Stops, StartColor, StartAlpha, EndColor, EndAlpha, Method, Transparency);
            if (_cache.Entry is { } entry && entry.Key == key) return entry.Lut;
            var lut = GradientLut.Build(EffectiveGradient, Method, Transparency);
            _cache.Entry = (key, lut);
            return lut;
        }
    }

    private readonly record struct LutKey(Gradient? Stops, RgbColor StartColor, float StartAlpha, RgbColor EndColor, float EndAlpha,
        GradientMethod Method, bool Transparency)
    {
        // The stops compare by reference: equal gradients built separately just build the table again.
        public bool Equals(LutKey o) => ReferenceEquals(Stops, o.Stops) && StartColor == o.StartColor && StartAlpha == o.StartAlpha
            && EndColor == o.EndColor && EndAlpha == o.EndAlpha && Method == o.Method && Transparency == o.Transparency;

        public override int GetHashCode() => HashCode.Combine(StartColor, StartAlpha, EndColor, EndAlpha, Method, Transparency);
    }

    private sealed class LutCache
    {
        public volatile Tuple<LutKey, GradientLut>? Box;

        public (LutKey Key, GradientLut Lut)? Entry
        {
            get => Box is { } b ? (b.Item1, b.Item2) : null;
            set => Box = value is { } v ? Tuple.Create(v.Key, v.Lut) : null;
        }

        public override bool Equals(object? obj) => obj is LutCache;
        public override int GetHashCode() => 0;
    }

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
    public (float R, float G, float B, float A) ColorAt(float t)
    {
        var (r, g, b, a) = Lut.At(t);
        return (r, g, b, a * Opacity);
    }

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
