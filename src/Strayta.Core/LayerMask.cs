namespace Strayta.Core;

/// <summary>A grayscale mask limiting where a layer is visible. White reveals, black hides.</summary>
public sealed class LayerMask
{
    public required PixelRect Bounds { get; init; }

    /// <summary>Mask samples covering <see cref="Bounds"/>; null when the mask is a solid <see cref="DefaultColor"/>.</summary>
    public Plane? Pixels { get; init; }

    /// <summary>Value (0 or 255) of the mask outside <see cref="Bounds"/>.</summary>
    public byte DefaultColor { get; init; }

    public bool Disabled { get; init; }

    /// <summary>When true the mask stays put when the layer moves (it is "unlinked").</summary>
    public bool PositionRelativeToLayer { get; init; }
}
