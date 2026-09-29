using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Editor.ViewModels;

// The Crop tool's Content-Aware option: fills the area a rotated or enlarged crop adds beyond the image with the same
// synthesis as Edit › Content-Aware Fill (PatchSynthesis, then a Poisson heal for color adaptation).
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// <see cref="ContentAwareCropFill"/>'s implementation: <paramref name="pixels"/> covers the new canvas from (0, 0);
    /// <paramref name="added"/> marks the area beyond the old image. Returns null (keep the background color) when
    /// there is nothing to sample or the crop is cancelled.
    /// </summary>
    private static Raster? FillCropContentAware(Raster pixels, SelectionMask added, CancellationToken cancel)
    {
        var canvas = new PixelRect(0, 0, pixels.Width, pixels.Height);
        var area = added.Bounds.Intersect(canvas);
        if (area.IsEmpty) return null;
        var coverage = new float[area.Width * area.Height];
        Parallel.For(area.Top, area.Bottom, y =>
        {
            for (int x = area.Left; x < area.Right; x++)
                coverage[(y - area.Top) * area.Width + (x - area.Left)] = added.CoverageAt(x, y) / 255f;
        });
        cancel.ThrowIfCancellationRequested();
        var image = PixelSource.FromRaster(pixels, canvas);
        if (PatchSynthesis.Synthesize(image, coverage, area, canvas, new SynthesisOptions()) is not { } texture) return null;
        cancel.ThrowIfCancellationRequested();
        var fill = Healing.Heal(image, texture, 0, 0, coverage, area, canvas);
        var target = new PixelLayer { Bounds = canvas, Pixels = pixels };
        var stroke = PaintStroke.FromCoverage(target, false, coverage, area, new CloneSource(0, 0, fill), canvas);
        return StrokeBaker.Bake(target, stroke, pixels.ColorMode, pixels.BitDepth).Pixels;
    }
}
