using Strayta.Core;

namespace Strayta.Rendering.Transforms;

// The Perspective Crop tool's result: a quadrilateral of the image becomes an upright canvas.
public static partial class CanvasOperations
{
    /// <summary>
    /// The upright size a quadrilateral (top-left, top-right, bottom-right, bottom-left, in document pixels)
    /// straightens to: the longer of each pair of opposite sides, so no detail is lost along either axis.
    /// </summary>
    public static (int Width, int Height) PerspectiveSize(IReadOnlyList<(double X, double Y)> quad)
    {
        double Len(int a, int b) => Math.Sqrt(Math.Pow(quad[a].X - quad[b].X, 2) + Math.Pow(quad[a].Y - quad[b].Y, 2));
        return (Math.Max(1, (int)Math.Round(Math.Max(Len(0, 1), Len(3, 2)))), Math.Max(1, (int)Math.Round(Math.Max(Len(0, 3), Len(1, 2)))));
    }

    /// <summary>
    /// Perspective Crop: <paramref name="quad"/> (corners clockwise from the one that becomes top-left) is mapped onto
    /// a <paramref name="width"/>×<paramref name="height"/> canvas with a projective map, every layer, mask and the
    /// stored composite resampled bicubically (area-filtered where the perspective shrinks) and cut to the new
    /// canvas, as Photoshop does. The Background stays a Background, filled with <paramref name="fill"/> where the
    /// quadrilateral reaches past the image. Live layers are listed in <see cref="CanvasChange.ResampledLiveLayers"/>
    /// to be rasterized.
    /// </summary>
    public static CanvasChange PerspectiveCrop(Document doc, IReadOnlyList<(double X, double Y)> quad, int width, int height,
        IReadOnlyList<float>? fill = null, CancellationToken cancel = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        // Output rectangle to quad, inverted: old document coordinates to the new canvas.
        var map = Projective.RectToQuad(width, height, quad).Invert();
        var canvas = PixelRect.FromSize(width, height);
        var background = FindBackground(doc);
        int colors = doc.ColorMode == ColorMode.Multichannel ? 0 : doc.ColorMode.ColorChannelCount();
        var fillColor = fill is not null && fill.Count == colors ? fill : Enumerable.Repeat(1f, Math.Max(colors, 1)).ToArray();

        var nodes = doc.Root.Descendants().ToList();
        var results = new LayerGeometry[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var node = nodes[i];
            var mask = ProjectiveResampler.TransformMask(node.GetMask(), map, ResampleFilter.Bicubic, canvas, cancel);
            if (node is not PixelLayer { Pixels: { } raster } layer || layer.Bounds.IsEmpty)
            {
                results[i] = new LayerGeometry(null, PixelRect.Empty, mask, node.Name);
                continue;
            }
            var (pixels, bounds) = ProjectiveResampler.TransformRaster(raster, layer.Bounds, map, ResampleFilter.Bicubic, canvas, cancel);
            if (ReferenceEquals(layer, background))
                results[i] = new LayerGeometry(ToCanvas(pixels, bounds, width, height, fillColor, raster.ColorMode, raster.BitDepth), canvas, mask, node.Name);
            else
            {
                if (pixels is not null && raster.Alpha is null && pixels.Alpha is { } a && IsOpaque(a))
                    pixels = new Raster(pixels.ColorMode, pixels.ColorPlanes, null);
                results[i] = new LayerGeometry(pixels, pixels is null ? PixelRect.Empty : bounds, mask, node.Name);
            }
        }

        Raster? composite = null;
        if (doc.Composite is { } c && c.Width == doc.Width && c.Height == doc.Height)
        {
            var (mapped, bounds) = ProjectiveResampler.TransformRaster(c, doc.Bounds, map, ResampleFilter.Bicubic, canvas, cancel);
            composite = ToCanvas(mapped, bounds, width, height, c.Alpha is null ? CompositeFill(c, fillColor) : null, c.ColorMode, c.BitDepth);
        }

        var (cx, cy) = map.Invert().Apply(width / 2.0, height / 2.0); // the old point that lands mid-canvas
        return new CanvasChange
        {
            Width = width,
            Height = height,
            Map = map.Linearize(cx, cy),
            Perspective = map,
            Method = ResampleMethod.Bicubic,
            Layers = nodes.Select((n, i) => (n, results[i])).ToList(),
            Composite = composite,
            ResampledLiveLayers = nodes.Where(n => n is PixelLayer { Pixels: not null } && IsLive(n)).ToList(),
        };
    }
}
