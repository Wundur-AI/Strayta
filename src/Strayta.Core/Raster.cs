namespace Strayta.Core;

/// <summary>A block of pixels: color planes in the document's color mode plus optional transparency.</summary>
public sealed class Raster
{
    public Raster(ColorMode colorMode, IReadOnlyList<Plane> colorPlanes, Plane? alpha)
    {
        if (colorPlanes.Count == 0 && alpha is null)
            throw new ArgumentException("A raster needs at least one plane.", nameof(colorPlanes));

        var first = colorPlanes.Count > 0 ? colorPlanes[0] : alpha!;
        foreach (var p in colorPlanes.Append(alpha).OfType<Plane>())
        {
            if (p.Width != first.Width || p.Height != first.Height || p.BitDepth != first.BitDepth)
                throw new ArgumentException("All planes in a raster must share size and bit depth.", nameof(colorPlanes));
        }

        ColorMode = colorMode;
        ColorPlanes = colorPlanes;
        Alpha = alpha;
        Width = first.Width;
        Height = first.Height;
        BitDepth = first.BitDepth;
    }

    public ColorMode ColorMode { get; }
    public IReadOnlyList<Plane> ColorPlanes { get; }
    public Plane? Alpha { get; }
    public int Width { get; }
    public int Height { get; }
    public int BitDepth { get; }
}
