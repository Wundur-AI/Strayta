namespace Strayta.Core.Painting;

/// <summary>
/// Read-only pixels placed in document space, for the retouching tools (Clone Stamp, Healing Brush, Spot Healing):
/// a layer's raster as it was when a stroke started, a flattened copy of the document, a layer mask, or a healed patch.
/// Rasters are never modified in place, so holding one is a free snapshot of the "state at stroke start".
/// </summary>
public abstract class PixelSource
{
    /// <summary>Color channels per pixel: 3 for RGB, 1 for grayscale images and masks.</summary>
    public abstract int ColorChannels { get; }

    /// <summary>Where the source has pixels of its own; see <see cref="Read"/> for what lies outside.</summary>
    public abstract PixelRect Bounds { get; }

    /// <summary>
    /// The straight (unpremultiplied) color at document pixel (<paramref name="x"/>, <paramref name="y"/>) into the first
    /// <see cref="ColorChannels"/> entries of <paramref name="color"/>; returns its alpha, 0..1. Layer pixels outside the
    /// layer are transparent; a mask reads its default color there.
    /// </summary>
    public abstract float Read(int x, int y, Span<float> color);

    /// <summary>A layer's pixels (as they are now; rasters are immutable) over <paramref name="bounds"/>. A null raster is empty.</summary>
    public static PixelSource FromRaster(Raster? raster, PixelRect bounds) => new RasterPixels(raster, bounds);

    /// <summary>A layer mask's gray values (one channel, always opaque).</summary>
    public static PixelSource FromMask(LayerMask mask) => new MaskPixels(mask);

    /// <summary>
    /// Float pixels over <paramref name="bounds"/>, interleaved as <paramref name="colorChannels"/> colors then alpha;
    /// transparent outside.
    /// </summary>
    public static PixelSource FromFloats(float[] pixels, int colorChannels, PixelRect bounds) => new FloatPixels(pixels, colorChannels, bounds);

    private sealed class RasterPixels(Raster? raster, PixelRect bounds) : PixelSource
    {
        private readonly int _colors = raster is null ? 1 : Math.Min(3, raster.ColorPlanes.Count);

        public override int ColorChannels => _colors;
        public override PixelRect Bounds { get; } = raster is null ? PixelRect.Empty : bounds;

        public override float Read(int x, int y, Span<float> color)
        {
            var b = Bounds;
            if (raster is null || x < b.Left || x >= b.Right || y < b.Top || y >= b.Bottom)
            {
                color[.._colors].Clear();
                return 0f;
            }
            int i = (y - b.Top) * b.Width + (x - b.Left);
            var planes = raster.ColorPlanes;
            for (int k = 0; k < _colors; k++) color[k] = planes[k].GetNormalized(i);
            return raster.Alpha?.GetNormalized(i) ?? 1f;
        }
    }

    private sealed class MaskPixels(LayerMask mask) : PixelSource
    {
        public override int ColorChannels => 1;
        public override PixelRect Bounds => mask.Bounds;

        public override float Read(int x, int y, Span<float> color)
        {
            color[0] = MaskBaker.Sample(mask, x, y);
            return 1f;
        }
    }

    private sealed class FloatPixels(float[] pixels, int colors, PixelRect bounds) : PixelSource
    {
        public override int ColorChannels => colors;
        public override PixelRect Bounds => bounds;

        public override float Read(int x, int y, Span<float> color)
        {
            if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom)
            {
                color[..colors].Clear();
                return 0f;
            }
            int stride = colors + 1, i = ((y - bounds.Top) * bounds.Width + (x - bounds.Left)) * stride;
            for (int k = 0; k < colors; k++) color[k] = pixels[i + k];
            return pixels[i + colors];
        }
    }
}

