using Strayta.Core;
using Strayta.Core.Selection;

namespace Strayta.Rendering.Transforms;

/// <summary>
/// Moves, scales or turns a selection with the canvas (Crop, Canvas Size, Image Size), so the marching ants stay on
/// the same part of the image instead of being dropped.
/// </summary>
public static class SelectionTransform
{
    /// <summary>
    /// The selection after <paramref name="map"/>, clipped to <paramref name="canvas"/>; null when nothing selected
    /// remains. Whole-pixel moves copy the coverage; a hard-edged rectangle under a move or scale stays a hard-edged
    /// rectangle (its edges rounded to whole pixels); anything else is resampled like a mask, soft edges included.
    /// </summary>
    public static SelectionMask? Apply(SelectionMask? selection, Affine map, PixelRect canvas, ResampleMethod method = ResampleMethod.Bicubic,
        CancellationToken cancel = default)
    {
        if (selection is null) return null;
        var b = selection.Bounds;
        bool axisAligned = Math.Abs(map.M12) < 1e-12 && Math.Abs(map.M21) < 1e-12;
        if (selection.IsRectangular && axisAligned)
        {
            var (x0, y0) = map.Apply(b.Left, b.Top);
            var (x1, y1) = map.Apply(b.Right, b.Bottom);
            var rect = new PixelRect((int)Math.Round(Math.Min(x0, x1)), (int)Math.Round(Math.Min(y0, y1)),
                (int)Math.Round(Math.Max(x0, x1)), (int)Math.Round(Math.Max(y0, y1)));
            return rect.IsEmpty ? null : SelectionMask.Rectangle(rect, canvas);
        }

        return FromResampled(CanvasOperations.Transform(AsRaster(selection), b, map, method, canvas, cancel), canvas);
    }

    /// <summary>The selection through a perspective map (Perspective Crop), clipped to <paramref name="canvas"/>.</summary>
    public static SelectionMask? Apply(SelectionMask? selection, Projective map, PixelRect canvas, CancellationToken cancel = default) =>
        selection is null ? null
            : FromResampled(ProjectiveResampler.TransformRaster(AsRaster(selection), selection.Bounds, map, ResampleFilter.Bicubic, canvas, cancel), canvas);

    private static Raster AsRaster(SelectionMask selection)
    {
        var b = selection.Bounds;
        var coverage = new byte[b.Width * b.Height];
        for (int y = 0; y < b.Height; y++) selection.CopyRow(b.Top + y, b.Left, coverage.AsSpan(y * b.Width, b.Width));
        return new Raster(ColorMode.Grayscale, [new Plane(b.Width, b.Height, 8, coverage)], null);
    }

    private static SelectionMask? FromResampled((Raster? Pixels, PixelRect Bounds) resampled, PixelRect canvas)
    {
        var (moved, bounds) = resampled;
        if (moved is null) return null;
        // Resampling puts the edge coverage in alpha: selected = gray value × coverage of the turned area.
        var gray = moved.ColorPlanes[0].Data;
        var result = new byte[gray.Length];
        if (moved.Alpha is { } alpha)
            for (int i = 0; i < result.Length; i++) result[i] = (byte)((gray[i] * alpha.Data[i] + 127) / 255);
        else
            gray.CopyTo(result, 0);
        return SelectionMask.FromCoverage(bounds, result, canvas);
    }
}
