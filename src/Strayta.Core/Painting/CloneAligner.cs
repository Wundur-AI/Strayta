namespace Strayta.Core.Painting;

/// <summary>
/// The Clone Stamp's and Healing Brush's source point and Photoshop's Aligned option. Option-click sets the source
/// point; the first stroke after it fixes the offset from the source to where the stroke starts. With Aligned on,
/// later strokes keep that offset, so the source moves with the pointer as if one long stroke had been painted; with
/// it off, every stroke starts sampling at the source point again.
/// </summary>
public sealed class CloneAligner
{
    private (int Dx, int Dy)? _offset;

    /// <summary>The Option-clicked point in document pixels, or null before one is set.</summary>
    public (int X, int Y)? SourcePoint { get; private set; }

    /// <summary>The offset the last stroke used (destination − source), or null before the first stroke from this source.</summary>
    public (int Dx, int Dy)? Offset => _offset;

    /// <summary>Option-click: a new source point; the next stroke measures a new offset from it.</summary>
    public void SetSource(int x, int y)
    {
        SourcePoint = (x, y);
        _offset = null;
    }

    /// <summary>
    /// Sets the offset as if a stroke had measured it (the Clone Source panel's Offset X/Y): aligned strokes use it
    /// from now on. Ignored before a source point is set.
    /// </summary>
    public void SetOffset(int dx, int dy)
    {
        if (SourcePoint is not null) _offset = (dx, dy);
    }

    /// <summary>
    /// The offset for a stroke starting at (<paramref name="x"/>, <paramref name="y"/>): the aligned offset when there is
    /// one, otherwise the distance from the source point to the start (which becomes the aligned offset). Null when no
    /// source point has been set.
    /// </summary>
    public (int Dx, int Dy)? BeginStroke(float x, float y, bool aligned)
    {
        if (SourcePoint is not { } s) return null;
        if (aligned && _offset is { } kept) return kept;
        var offset = ((int)MathF.Floor(x) - s.X, (int)MathF.Floor(y) - s.Y);
        _offset = offset;
        return offset;
    }

    /// <summary>
    /// Where the source is for a brush at (<paramref name="x"/>, <paramref name="y"/>) that has not started painting,
    /// for the overlay and crosshair: with an aligned offset it follows the pointer, otherwise it is the source point.
    /// </summary>
    public (float X, float Y)? SourceFor(float x, float y, bool aligned)
    {
        if (SourcePoint is not { } s) return null;
        if (aligned && _offset is { } o) return (x - o.Dx, y - o.Dy);
        return (s.X + 0.5f, s.Y + 0.5f);
    }
}
