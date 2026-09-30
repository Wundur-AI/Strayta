using Strayta.Core;
using Strayta.Rendering.Transforms;

namespace Strayta.Rendering.Export;

/// <summary>
/// What to export: the whole document (<see cref="Node"/> null), an artboard (its bounds, background and layers), or
/// one layer or group on its own, trimmed to its visible pixels or at the size of the canvas.
/// </summary>
public sealed record ExportTarget(LayerNode? Node, bool TrimToContent = true)
{
    public static ExportTarget Document { get; } = new(null, false);

    public static ExportTarget For(LayerNode node, bool trimToContent = true) => new(node, trimToContent);

    public bool IsArtboard => Node is LayerGroup { Artboard: not null, Parent.Parent: null };
}

/// <summary>Renders export targets at full resolution as straight-alpha RGBA8.</summary>
public static class ExportRenderer
{
    /// <summary>The target's pixels, or null when it shows nothing (an empty or fully transparent layer).</summary>
    public static RgbaImage? TryRender(Document doc, ExportTarget target, CancellationToken cancel = default)
    {
        if (target.Node is not { } node)
        {
            var all = Compositor.Render(doc, new RenderOptions { Cancellation = cancel });
            return new RgbaImage(all.ToRgba8(cancel), doc.Width, doc.Height);
        }

        var (hidden, shown) = Isolate(node);
        var result = Compositor.Render(doc, new RenderOptions
        {
            Cancellation = cancel,
            Hidden = hidden,
            Shown = shown,
            DrawArtboards = target.IsArtboard,
        });
        var rgba = result.ToRgba8(cancel);
        var full = new RgbaImage(rgba, doc.Width, doc.Height);

        PixelRect crop;
        if (target.IsArtboard) crop = ((LayerGroup)node).Artboard!.Rect.Intersect(doc.Bounds);
        else if (target.TrimToContent) crop = CanvasOperations.TrimBounds(rgba, doc.Width, doc.Height, TrimBasis.Transparent);
        else crop = doc.Bounds;
        if (crop.IsEmpty) return null;
        return crop == doc.Bounds ? full : Crop(full, crop);
    }

    /// <summary>Like <see cref="TryRender"/>, but an empty target gives a 1×1 transparent image.</summary>
    public static RgbaImage Render(Document doc, ExportTarget target, CancellationToken cancel = default) =>
        TryRender(doc, target, cancel) ?? new RgbaImage(new byte[4], 1, 1);

    /// <summary>
    /// Layers to hide so that only <paramref name="node"/> draws: the siblings of it and of each of its groups, except
    /// the layers clipped to it (they belong to its look) and the base it is clipped to. It and its groups are shown
    /// even if hidden.
    /// </summary>
    public static (HashSet<LayerNode> Hidden, HashSet<LayerNode> Shown) Isolate(LayerNode node)
    {
        var hidden = new HashSet<LayerNode>(ReferenceEqualityComparer.Instance);
        var shown = new HashSet<LayerNode>(ReferenceEqualityComparer.Instance);
        for (LayerNode current = node; current.Parent is { } parent; current = parent)
        {
            shown.Add(current);
            var keep = new HashSet<LayerNode>(ReferenceEqualityComparer.Instance) { current };
            int index = parent.IndexOf(current);
            if (ReferenceEquals(current, node))
            {
                for (int i = index + 1; i < parent.Children.Count && parent.Children[i].Clipped; i++) keep.Add(parent.Children[i]);
                if (current.Clipped)
                    for (int i = index - 1; i >= 0; i--)
                    {
                        keep.Add(parent.Children[i]);
                        if (!parent.Children[i].Clipped) break;
                    }
            }
            foreach (var sibling in parent.Children)
                if (!keep.Contains(sibling)) hidden.Add(sibling);
        }
        return (hidden, shown);
    }

    public static RgbaImage Crop(RgbaImage image, PixelRect rect)
    {
        rect = rect.Intersect(PixelRect.FromSize(image.Width, image.Height));
        var pixels = new byte[(long)rect.Width * rect.Height * 4];
        for (int y = 0; y < rect.Height; y++)
            Array.Copy(image.Pixels, ((long)(rect.Top + y) * image.Width + rect.Left) * 4, pixels, (long)y * rect.Width * 4, rect.Width * 4);
        return new RgbaImage(pixels, rect.Width, rect.Height);
    }
}

/// <summary>Resizes RGBA8 images with Image Size's resampler (premultiplied bicubic, area-averaged when reducing).</summary>
public static class RgbaResize
{
    public static byte[] Resize(byte[] rgba, int width, int height, int newWidth, int newHeight, CancellationToken cancel = default)
    {
        var planes = new Plane[4];
        for (int c = 0; c < 4; c++)
        {
            planes[c] = Plane.Create(width, height, 8);
            var data = planes[c].Data;
            for (long i = 0, p = c; i < data.LongLength; i++, p += 4) data[i] = rgba[p];
        }
        var raster = new Raster(ColorMode.Rgb, planes[..3], planes[3]);
        var map = Affine.Scale(newWidth / (double)width, newHeight / (double)height);
        var (scaled, bounds) = SeparableScale.Raster(raster, PixelRect.FromSize(width, height), map, ResampleFilter.Bicubic,
            PixelRect.FromSize(newWidth, newHeight), cancel);
        var result = new byte[(long)newWidth * newHeight * 4];
        if (scaled is null) return result;
        var src = scaled.ColorPlanes.Append(scaled.Alpha!).ToArray();
        for (int y = 0; y < bounds.Height; y++)
            for (int x = 0; x < bounds.Width; x++)
            {
                long o = ((long)(bounds.Top + y) * newWidth + bounds.Left + x) * 4;
                long i = (long)y * bounds.Width + x;
                for (int c = 0; c < 4; c++) result[o + c] = src[c].Data[i];
            }
        return result;
    }
}
