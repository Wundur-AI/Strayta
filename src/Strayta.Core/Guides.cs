namespace Strayta.Core;

/// <summary>Whether a guide runs across the image (a horizontal line at a y) or down it (a vertical line at an x).</summary>
public enum GuideOrientation
{
    Vertical,
    Horizontal,
}

/// <summary>
/// A document guide: a line at <paramref name="Position"/> document pixels from the left (vertical) or top (horizontal)
/// edge. Guides belong to the document (PSD image resource 1032 stores them to 1/32 pixel), so they are saved with it and
/// move with crops and canvas changes.
/// </summary>
public readonly record struct Guide(GuideOrientation Orientation, double Position)
{
    public bool IsHorizontal => Orientation == GuideOrientation.Horizontal;

    /// <summary>The same guide at another position.</summary>
    public Guide At(double position) => this with { Position = position };
}
