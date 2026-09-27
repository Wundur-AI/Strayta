using Strayta.Core.Selection;

namespace Strayta.Core.Painting;

/// <summary>
/// Photoshop's Paint Bucket: fills the pixels similar in color to the clicked one. The area is exactly what the Magic
/// Wand would select with the same options (tolerance, anti-alias, contiguous, sampled layers), limited to the
/// selection, and it is filled the way Edit › Fill fills a selection.
/// </summary>
public static class PaintBucket
{
    /// <summary>
    /// The area a click at (<paramref name="x"/>, <paramref name="y"/>) fills, as coverage; null when nothing would be
    /// filled, including a click outside the selection (Photoshop does nothing there).
    /// </summary>
    public static SelectionMask? Region(SampleImage image, int x, int y, MagicWandOptions options, SelectionMask? selection)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return null;
        if (selection is not null && selection.CoverageAt(x, y) == 0) return null;
        var region = MagicWand.Select(image, x, y, options);
        return selection is null ? region : SelectionMask.Combine(selection, region, SelectionMode.Intersect);
    }

    /// <summary>The layer's new pixels after a fill with <paramref name="color"/>, or null when nothing is filled.</summary>
    public static (Raster? Pixels, PixelRect Bounds)? Fill(PixelLayer layer, SampleImage image, int x, int y, MagicWandOptions options,
        SelectionMask? selection, RgbColor color, ColorMode mode, int bitDepth) =>
        Region(image, x, y, options, selection) is { } region ? SelectionPainter.Fill(layer, region, color, mode, bitDepth) : null;
}
