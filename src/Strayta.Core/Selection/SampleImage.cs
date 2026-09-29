namespace Strayta.Core.Selection;

/// <summary>
/// The pixels the color-based selection tools (magic wand, quick selection) look at: premultiplied 8-bit RGBA over
/// the whole canvas, in any color mode's display colors. Premultiplying makes every fully transparent pixel
/// identical, so the empty parts of a layer form one region whatever color data they happen to hold.
/// </summary>
public sealed class SampleImage
{
    public SampleImage(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (rgba.LongLength != (long)width * height * 4) throw new ArgumentException("Expected 4 bytes per pixel.", nameof(rgba));
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Premultiplied RGBA, row-major, 4 bytes per pixel.</summary>
    public byte[] Rgba { get; }

    public PixelRect Bounds => PixelRect.FromSize(Width, Height);

    /// <summary>
    /// One layer's own pixels placed on a canvas of <paramref name="width"/> × <paramref name="height"/>; everything
    /// outside the layer is transparent (Photoshop's wand without Sample All Layers). Masks are ignored, as there.
    /// </summary>
    public static SampleImage FromLayer(PixelLayer layer, int width, int height, byte[]? palette = null) =>
        FromPixels(layer.Pixels, layer.Bounds, width, height, palette);

    /// <summary>Like <see cref="FromLayer"/> for pixels read beforehand, so a background thread never touches the layer.</summary>
    /// <param name="mask">
    /// The layer's mask, when the tool honors it (the Magic Wand in current-layer mode): pixels the mask hides count as
    /// transparent, in proportion to how much it hides them. A disabled mask is ignored.
    /// </param>
    public static SampleImage FromPixels(Raster? pixels, PixelRect bounds, int width, int height, byte[]? palette = null, LayerMask? mask = null)
    {
        var rgba = new byte[(long)width * height * 4];
        if (pixels is null) return new SampleImage(width, height, rgba);

        var src = RgbaConverter.ToRgba8(pixels, palette);
        var b = bounds;
        var visible = b.Intersect(PixelRect.FromSize(width, height));
        if (!visible.IsEmpty)
        {
            Parallel.For(visible.Top, visible.Bottom, y =>
            {
                int s = ((y - b.Top) * b.Width + (visible.Left - b.Left)) * 4;
                int d = (y * width + visible.Left) * 4;
                var row = rgba.AsSpan(d, visible.Width * 4);
                if (mask is { Disabled: false } m)
                {
                    var masked = src.AsSpan(s, visible.Width * 4).ToArray();
                    for (int x = 0; x < visible.Width; x++)
                        masked[x * 4 + 3] = (byte)(masked[x * 4 + 3] * Painting.MaskBaker.Sample(m, visible.Left + x, y) + 0.5f);
                    Premultiply(masked, row);
                }
                else Premultiply(src.AsSpan(s, visible.Width * 4), row);
            });
        }
        return new SampleImage(width, height, rgba);
    }

    /// <summary>A flattened image (straight-alpha RGBA, e.g. the document render) for Sample All Layers.</summary>
    public static SampleImage FromStraightRgba(byte[] straight, int width, int height)
    {
        if (straight.LongLength != (long)width * height * 4) throw new ArgumentException("Expected 4 bytes per pixel.", nameof(straight));
        var rgba = new byte[straight.LongLength];
        Parallel.For(0, height, y =>
        {
            int o = y * width * 4;
            Premultiply(straight.AsSpan(o, width * 4), rgba.AsSpan(o, width * 4));
        });
        return new SampleImage(width, height, rgba);
    }

    private static void Premultiply(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        for (int i = 0; i < src.Length; i += 4)
        {
            int a = src[i + 3];
            if (a == 255)
            {
                src.Slice(i, 4).CopyTo(dst.Slice(i, 4));
                continue;
            }
            dst[i] = (byte)((src[i] * a + 127) / 255);
            dst[i + 1] = (byte)((src[i + 1] * a + 127) / 255);
            dst[i + 2] = (byte)((src[i + 2] * a + 127) / 255);
            dst[i + 3] = (byte)a;
        }
    }
}
