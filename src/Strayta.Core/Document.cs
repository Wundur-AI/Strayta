namespace Strayta.Core;

/// <summary>A layered image document, independent of any file format.</summary>
public sealed class Document
{
    public Document(int width, int height, ColorMode colorMode, int bitDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        ColorMode = colorMode;
        BitDepth = bitDepth;
        Root = new LayerGroup { Name = "<root>", BlendMode = BlendMode.Normal };
    }

    public int Width { get; }
    public int Height { get; }
    public ColorMode ColorMode { get; }

    /// <summary>Bit depth of the source document (1, 8, 16 or 32). Pixel planes are never 1-bit; see <see cref="Plane"/>.</summary>
    public int BitDepth { get; }

    /// <summary>Top of the layer tree. Its children are the document's top-level layers, bottom first.</summary>
    public LayerGroup Root { get; }

    /// <summary>The flattened image as saved by the authoring application, if the file contained one.</summary>
    public Raster? Composite { get; set; }

    /// <summary>For <see cref="ColorMode.Indexed"/> documents: 256 RGB triples.</summary>
    public byte[]? Palette { get; set; }

    /// <summary>Opaque data a format reader attaches for round-tripping; see <see cref="LayerNode.SourceData"/>.</summary>
    public object? SourceData { get; set; }

    /// <summary>Embedded ICC color profile, if any.</summary>
    public byte[]? IccProfile { get; set; }

    public PixelRect Bounds => PixelRect.FromSize(Width, Height);
}
