using Strayta.Core;

namespace Strayta.Segmentation;

/// <summary>
/// An 8-bit straight-alpha RGBA image and where it sits in the document: the whole canvas for "Sample All
/// Layers", or a layer's bounds. Models see only this image; prompts and masks are mapped through
/// <see cref="Placement"/> so callers work in document coordinates throughout.
/// </summary>
public sealed class RgbaImage
{
    public RgbaImage(byte[] pixels, int width, int height, PixelRect placement)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.LongLength != (long)width * height * 4)
            throw new ArgumentException($"Expected {(long)width * height * 4} bytes for a {width}x{height} RGBA image.", nameof(pixels));
        if (placement.Width != width || placement.Height != height)
            throw new ArgumentException("The placement must have the image's size.", nameof(placement));
        Pixels = pixels;
        Width = width;
        Height = height;
        Placement = placement;
    }

    /// <summary>RGBA, row-major, straight (not premultiplied) alpha.</summary>
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public PixelRect Placement { get; }
}
