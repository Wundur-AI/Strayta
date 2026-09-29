using Strayta.Core.Painting;

namespace Strayta.Core;

/// <summary>
/// A layer style's reference to a pattern: Photoshop's pattern identifier and name, and the pattern itself when it is
/// known (the document's, or one from <see cref="PatternLibrary"/>). A file can name a pattern it does not contain (a
/// preset on the machine it was made on): the reference is kept, but nothing can be drawn. References compare by id
/// and name alone, so an effect read back from a file equals the one written although the tile is stored elsewhere.
/// </summary>
public sealed record PatternReference(string Id, string Name)
{
    /// <summary>The pattern's tile, or null when the file did not contain it.</summary>
    public Pattern? Resolved { get; init; }

    public static PatternReference To(Pattern pattern) => new(pattern.Id, pattern.Name) { Resolved = pattern };

    public bool Equals(PatternReference? other) => other is not null && Id == other.Id && Name == other.Name;

    public override int GetHashCode() => HashCode.Combine(Id, Name);

    public override string ToString() => Name;
}

/// <summary>
/// How an effect lays a pattern out: the tile's scale (1 = 100%), whether it moves with the layer ("Link with
/// Layer" / "Align") and the phase (Photoshop's "Snap to Origin" offset), in document pixels.
/// </summary>
public sealed record PatternFill(PatternReference Pattern)
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
