namespace Strayta.Core;

/// <summary>
/// A pattern tile, as used by Pattern Overlay, pattern strokes and bevel textures. Effects refer to a pattern by its
/// <see cref="Id"/> (Photoshop's pattern identifier), so two patterns compare equal by id and name alone: an effect
/// read back from a file equals the one written even though the pixels were stored elsewhere in the file.
/// </summary>
public sealed record Pattern(string Id, string Name)
{
    /// <summary>
    /// The tile (RGB or grayscale, 8-bit, with optional transparency), or null when the file referred to a pattern it
    /// does not contain (a preset of the machine it was made on): such effects are kept but cannot be drawn.
    /// </summary>
    public Raster? Pixels { get; init; }

    public bool Equals(Pattern? other) => other is not null && Id == other.Id && Name == other.Name;

    public override int GetHashCode() => HashCode.Combine(Id, Name);

    public override string ToString() => Name;
}

/// <summary>
/// How an effect lays a pattern out: the tile's scale (1 = 100%), whether it moves with the layer ("Link with
/// Layer" / "Align") and the phase (Photoshop's "Snap to Origin" offset), in document pixels.
/// </summary>
public sealed record PatternFill(Pattern Pattern)
{
    public float Scale { get; init; } = 1f;
    public bool LinkWithLayer { get; init; } = true;
    public float PhaseX { get; init; }
    public float PhaseY { get; init; }
}

/// <summary>
/// A gradient laid over an area, as Gradient Overlay, gradient strokes and gradient fills do: the gradient, its
/// style, angle, scale (1 = 100%), whether it spans the layer ("Align with Layer") and its offset as a fraction of
/// the layer box.
/// </summary>
public sealed record GradientFill(Gradient Gradient)
{
    public GradientStyle Style { get; init; }
    public float Angle { get; init; } = 90f;
    public float Scale { get; init; } = 1f;
    public bool Reverse { get; init; }
    public bool AlignWithLayer { get; init; } = true;
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
}
