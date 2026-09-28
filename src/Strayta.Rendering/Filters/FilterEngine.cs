using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Rendering.Filters;

/// <summary>Where a filter applies in a document: the canvas, optionally limited by a selection.</summary>
/// <param name="Canvas">The document bounds (at the preview's scale for a preview).</param>
public sealed record FilterScope(PixelRect Canvas)
{
    /// <summary>Limits the filter; soft edges blend the filtered and original pixels. Always full resolution.</summary>
    public SelectionMask? Selection { get; init; }

    /// <summary>
    /// For a reduced preview: how many full-resolution pixels one working pixel stands for. The selection is sampled at
    /// the center of each preview pixel, and position-dependent filters (noise) use full-resolution coordinates. Pass
    /// the filter <see cref="ImageFilter.Scaled"/> by 1/Factor too.
    /// </summary>
    public int Factor { get; init; } = 1;

    /// <summary>The layer's transparency is locked: its alpha is kept, only color changes.</summary>
    public bool PreserveTransparency { get; init; }
}

/// <summary>
/// Applies an <see cref="ImageFilter"/> to a layer's pixels or a layer mask, as Photoshop's Filter menu does. Like the
/// painters it returns new rasters and never modifies its inputs, so undo only swaps references.
/// </summary>
/// <remarks>
/// <para>Filters see the layer as it sits on the canvas: pixels outside the layer are transparent, and beyond the
/// canvas edge the edge pixels repeat, so blurring a Background (or any layer that covers the canvas) never pulls
/// transparency or darkness in from the edges. Color is premultiplied by alpha while filtering, so a transparent
/// layer's edges blur into transparency without dark or colored fringes.</para>
/// <para>The engine filters only the part of the canvas that can change (the layer grown by the filter's
/// <see cref="ImageFilter.Reach"/>, cut to the selection) and reads that much further around it. A selection's soft edge
/// mixes the filtered and original pixels by its coverage (in premultiplied color, like painting through it).</para>
/// </remarks>
public static class FilterEngine
{
    /// <summary>
    /// The layer's pixels filtered. Blurs can grow the layer (up to the canvas); a layer without transparency (a
    /// Background) stays opaque as long as it covers everything the filter reads.
    /// </summary>
    /// <param name="pixels">The layer's pixels (null for an empty layer, which stays empty) covering <paramref name="bounds"/>.</param>
    public static (Raster? Pixels, PixelRect Bounds) ApplyToLayer(Raster? pixels, PixelRect bounds, ImageFilter filter, FilterScope scope,
        CancellationToken cancel = default)
    {
        if (pixels is null) return (null, bounds);
        CheckMode(pixels.ColorMode);
        var region = LayerRegion(bounds, filter, scope);
        if (region.IsEmpty) return (pixels, bounds);
        var grid = Grid(region, filter, scope);
        var image = ReadLayer(pixels, bounds, grid, scope.Factor);
        filter.Apply(image, cancel);
        var outBounds = Union(bounds, region);
        bool withAlpha = !(pixels.Alpha is null && outBounds == bounds && image.Alpha is null);
        return (ComposeLayer(pixels, bounds, image, grid, region, outBounds, withAlpha, scope, cancel), outBounds);
    }

    /// <summary>
    /// The layer's pixels over exactly <paramref name="area"/> (with alpha), filtered where the filter applies: what a
    /// filter dialog's small preview shows. A null <paramref name="filter"/> gives the unfiltered pixels.
    /// </summary>
    public static Raster PreviewLayer(Raster? pixels, PixelRect bounds, ImageFilter? filter, FilterScope scope, PixelRect area,
        ColorMode mode, int bitDepth, CancellationToken cancel = default)
    {
        if (area.IsEmpty) throw new ArgumentException("The preview area is empty.", nameof(area));
        if (pixels is null)
        {
            var empty = Enumerable.Range(0, mode.ColorChannelCount()).Select(_ => Plane.Create(area.Width, area.Height, bitDepth)).ToArray();
            return new Raster(mode, empty, Plane.Create(area.Width, area.Height, bitDepth));
        }
        CheckMode(pixels.ColorMode);
        var region = filter is null ? PixelRect.Empty : LayerRegion(bounds, filter, scope).Intersect(area);
        FilterImage? image = null;
        var grid = PixelRect.Empty;
        if (!region.IsEmpty)
        {
            grid = Grid(region, filter!, scope);
            image = ReadLayer(pixels, bounds, grid, scope.Factor);
            filter!.Apply(image, cancel);
        }
        return ComposeLayer(pixels, bounds, image, grid, region, area, withAlpha: true, scope, cancel);
    }

    /// <summary>
    /// The mask filtered, as gray values (black hides). The mask's area beyond its pixels counts as its default color;
    /// the result grows to cover what changed.
    /// </summary>
    public static LayerMask ApplyToMask(LayerMask mask, ImageFilter filter, FilterScope scope, int bitDepth, CancellationToken cancel = default)
    {
        var region = MaskRegion(mask, filter, scope);
        if (region.IsEmpty) return mask;
        var grid = Grid(region, filter, scope);
        var image = ReadMask(mask, grid, scope.Factor);
        filter.Apply(image, cancel);
        var oldBounds = mask.Pixels is null ? PixelRect.Empty : mask.Bounds;
        var outBounds = Union(oldBounds, region);
        var plane = ComposeMask(mask, image, grid, region, outBounds, mask.Pixels?.BitDepth ?? bitDepth, scope, cancel);
        return new LayerMask
        {
            Bounds = outBounds,
            Pixels = plane,
            DefaultColor = mask.DefaultColor,
            Disabled = mask.Disabled,
            PositionRelativeToLayer = mask.PositionRelativeToLayer,
        };
    }

    /// <summary>The mask over exactly <paramref name="area"/>, filtered where the filter applies (for a dialog preview).</summary>
    public static Plane PreviewMask(LayerMask mask, ImageFilter? filter, FilterScope scope, PixelRect area, int bitDepth, CancellationToken cancel = default)
    {
        if (area.IsEmpty) throw new ArgumentException("The preview area is empty.", nameof(area));
        var region = filter is null ? PixelRect.Empty : MaskRegion(mask, filter, scope).Intersect(area);
        FilterImage? image = null;
        var grid = PixelRect.Empty;
        if (!region.IsEmpty)
        {
            grid = Grid(region, filter!, scope);
            image = ReadMask(mask, grid, scope.Factor);
            filter!.Apply(image, cancel);
        }
        return ComposeMask(mask, image, grid, region, area, bitDepth, scope, cancel);
    }

    /// <summary>A whole raster filtered on its own (its bounds are the canvas): the simplest use outside a document.</summary>
    public static Raster Apply(Raster raster, ImageFilter filter, CancellationToken cancel = default)
    {
        var canvas = PixelRect.FromSize(raster.Width, raster.Height);
        return ApplyToLayer(raster, canvas, filter, new FilterScope(canvas), cancel).Pixels!;
    }

    // ---- Regions ------------------------------------------------------------------------------------

    /// <summary>The part of the canvas a filter can change on a layer covering <paramref name="bounds"/>.</summary>
    public static PixelRect LayerRegion(PixelRect bounds, ImageFilter filter, FilterScope scope)
    {
        var canvas = scope.Canvas;
        var onCanvas = bounds.Intersect(canvas);
        if (onCanvas.IsEmpty) return PixelRect.Empty;
        // Blurs spread into the transparent area around the layer; filters that keep transparency cannot.
        var reach = filter.KeepsTransparency || scope.PreserveTransparency ? onCanvas : Inflate(onCanvas, filter.Reach).Intersect(canvas);
        return reach.Intersect(GradientPainter.Region(canvas, scope.Selection, scope.Factor));
    }

    private static PixelRect MaskRegion(LayerMask mask, ImageFilter filter, FilterScope scope)
    {
        var canvas = scope.Canvas;
        PixelRect region;
        if (filter.ChangesFlatAreas) region = canvas;
        else if (mask.Pixels is null) return PixelRect.Empty; // a solid mask stays solid under a blur
        else region = Inflate(mask.Bounds.Intersect(canvas), filter.Reach).Intersect(canvas);
        return region.IsEmpty ? region : region.Intersect(GradientPainter.Region(canvas, scope.Selection, scope.Factor));
    }

    /// <summary>What the filter reads to compute <paramref name="region"/>.</summary>
    private static PixelRect Grid(PixelRect region, ImageFilter filter, FilterScope scope) =>
        Inflate(region, filter.Reach).Intersect(scope.Canvas);

    private static PixelRect Inflate(PixelRect r, int by) =>
        r.IsEmpty ? r : new PixelRect(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);

    private static PixelRect Union(PixelRect a, PixelRect b) =>
        a.IsEmpty ? b : b.IsEmpty ? a
        : new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    private static void CheckMode(ColorMode mode)
    {
        if (mode is ColorMode.Indexed or ColorMode.Bitmap or ColorMode.Multichannel)
            throw new NotSupportedException($"Filters do not work on {mode} images; convert to RGB or Grayscale first.");
    }

    // ---- Reading ------------------------------------------------------------------------------------

    /// <summary>The layer over <paramref name="grid"/>, premultiplied; transparent where the layer has no pixels.</summary>
    private static FilterImage ReadLayer(Raster pixels, PixelRect bounds, PixelRect grid, int factor)
    {
        int colors = pixels.ColorPlanes.Count, w = grid.Width;
        bool covered = bounds.Intersect(grid) == grid;
        var image = new FilterImage(w, grid.Height, colors, hasAlpha: pixels.Alpha is not null || !covered)
        {
            Left = grid.Left, Top = grid.Top, Scale = factor,
        };
        var inside = bounds.Intersect(grid);
        if (inside.IsEmpty) return image;
        Parallel.For(inside.Top, inside.Bottom, () => new float[inside.Width], (y, _, alphaRow) =>
        {
            int src = (y - bounds.Top) * bounds.Width + (inside.Left - bounds.Left);
            int dst = (y - grid.Top) * w + (inside.Left - grid.Left);
            if (pixels.Alpha is { } a) ReadRow(a, src, alphaRow);
            else alphaRow.AsSpan().Fill(1f);
            if (image.Alpha is { } alpha) alphaRow.CopyTo(alpha, dst);
            for (int c = 0; c < colors; c++)
            {
                var row = image.Color[c].AsSpan(dst, inside.Width);
                ReadRow(pixels.ColorPlanes[c], src, row);
                if (pixels.Alpha is not null)
                    for (int i = 0; i < row.Length; i++) row[i] *= alphaRow[i];
            }
            return alphaRow;
        }, _ => { });
        return image;
    }

    /// <summary>The mask over <paramref name="grid"/> as one plane (its default color beyond its pixels).</summary>
    private static FilterImage ReadMask(LayerMask mask, PixelRect grid, int factor)
    {
        var image = new FilterImage(grid.Width, grid.Height, 1, hasAlpha: false) { Left = grid.Left, Top = grid.Top, Scale = factor };
        FillMaskRows(mask, grid, image.Color[0], grid.Width);
        return image;
    }

    /// <summary>Writes the mask's values over <paramref name="area"/> into <paramref name="dst"/> (row stride <paramref name="stride"/>).</summary>
    private static void FillMaskRows(LayerMask mask, PixelRect area, float[] dst, int stride)
    {
        float outside = mask.DefaultColor / 255f;
        var pixels = mask.Pixels;
        var inside = pixels is null ? PixelRect.Empty : mask.Bounds.Intersect(area);
        Parallel.For(0, area.Height, row =>
        {
            int y = area.Top + row;
            var span = dst.AsSpan(row * stride, area.Width);
            span.Fill(outside);
            if (pixels is null || y < inside.Top || y >= inside.Bottom || inside.IsEmpty) return;
            ReadRow(pixels, (y - mask.Bounds.Top) * mask.Bounds.Width + (inside.Left - mask.Bounds.Left),
                span.Slice(inside.Left - area.Left, inside.Width));
        });
    }

    // ---- Composing ----------------------------------------------------------------------------------

    private sealed class LayerRows(int width, int colors)
    {
        public readonly float[][] Color = Enumerable.Range(0, colors).Select(_ => new float[width]).ToArray();
        public readonly float[] Alpha = new float[width];
        public readonly byte[] Selection = new byte[width];
    }

    /// <summary>
    /// New planes over <paramref name="outBounds"/>: the old pixels, with the filtered ones (from <paramref name="image"/>,
    /// which covers <paramref name="grid"/>) inside <paramref name="region"/>, mixed by the selection.
    /// </summary>
    private static Raster ComposeLayer(Raster old, PixelRect oldBounds, FilterImage? image, PixelRect grid, PixelRect region,
        PixelRect outBounds, bool withAlpha, FilterScope scope, CancellationToken cancel)
    {
        int w = outBounds.Width, h = outBounds.Height, colors = old.ColorPlanes.Count, depth = old.BitDepth;
        var planes = Enumerable.Range(0, colors).Select(_ => Plane.Create(w, h, depth)).ToArray();
        var alphaPlane = withAlpha ? Plane.Create(w, h, depth) : null;
        var valid = oldBounds.Intersect(outBounds);
        bool lockAlpha = scope.PreserveTransparency;

        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, () => new LayerRows(w, colors), (row, _, buf) =>
        {
            int y = outBounds.Top + row;
            buf.Alpha.AsSpan().Clear();
            foreach (var c in buf.Color) c.AsSpan().Clear();
            if (!valid.IsEmpty && y >= valid.Top && y < valid.Bottom)
            {
                int src = (y - oldBounds.Top) * oldBounds.Width + (valid.Left - oldBounds.Left), at = valid.Left - outBounds.Left;
                for (int k = 0; k < colors; k++) ReadRow(old.ColorPlanes[k], src, buf.Color[k].AsSpan(at, valid.Width));
                if (old.Alpha is { } a) ReadRow(a, src, buf.Alpha.AsSpan(at, valid.Width));
                else buf.Alpha.AsSpan(at, valid.Width).Fill(1f);
            }

            if (image is not null && y >= region.Top && y < region.Bottom)
            {
                ReadSelection(scope.Selection, scope.Factor, y, region.Left, buf.Selection.AsSpan(0, region.Width));
                int g0 = (y - grid.Top) * grid.Width - grid.Left;
                for (int x = region.Left; x < region.Right; x++)
                {
                    float s = buf.Selection[x - region.Left] * (1f / 255f);
                    if (s <= 0f) continue;
                    int i = x - outBounds.Left, g = g0 + x;
                    float oa = buf.Alpha[i], fa = image.Alpha?[g] ?? 1f;
                    // Mix premultiplied, like painting the filtered pixels through the selection.
                    float na = oa + (fa - oa) * s;
                    for (int k = 0; k < colors; k++)
                    {
                        float oc = buf.Color[k][i] * oa, fc = image.Color[k][g];
                        float nc = oc + (fc - oc) * s;
                        buf.Color[k][i] = na > 1e-6f ? nc / na : fa > 1e-6f ? fc / fa : buf.Color[k][i];
                    }
                    buf.Alpha[i] = lockAlpha ? oa : na;
                }
            }

            int dst = row * w;
            for (int k = 0; k < colors; k++) WriteRow(planes[k], dst, buf.Color[k]);
            if (alphaPlane is not null) WriteRow(alphaPlane, dst, buf.Alpha);
            return buf;
        }, _ => { });

        return new Raster(old.ColorMode, planes, alphaPlane);
    }

    private static Plane ComposeMask(LayerMask mask, FilterImage? image, PixelRect grid, PixelRect region, PixelRect outBounds, int depth,
        FilterScope scope, CancellationToken cancel)
    {
        int w = outBounds.Width, h = outBounds.Height;
        var values = new float[w * h];
        FillMaskRows(mask, outBounds, values, w);
        var plane = Plane.Create(w, h, depth);
        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, () => new byte[Math.Max(1, region.Width)], (row, _, sel) =>
        {
            int y = outBounds.Top + row;
            var m = values.AsSpan(row * w, w);
            if (image is not null && y >= region.Top && y < region.Bottom)
            {
                ReadSelection(scope.Selection, scope.Factor, y, region.Left, sel.AsSpan(0, region.Width));
                int g0 = (y - grid.Top) * grid.Width - grid.Left;
                for (int x = region.Left; x < region.Right; x++)
                {
                    float s = sel[x - region.Left] * (1f / 255f);
                    int i = x - outBounds.Left;
                    m[i] += (image.Color[0][g0 + x] - m[i]) * s;
                }
            }
            WriteRow(plane, row * w, m);
            return sel;
        }, _ => { });
        return plane;
    }

    // ---- Samples ------------------------------------------------------------------------------------

    /// <summary>Selection coverage along a row; a preview samples the full-resolution selection at each pixel's center.</summary>
    private static void ReadSelection(SelectionMask? selection, int factor, int y, int x0, Span<byte> dst)
    {
        if (selection is null)
        {
            dst.Fill(255);
            return;
        }
        if (factor == 1)
        {
            selection.CopyRow(y, x0, dst);
            return;
        }
        int sy = y * factor + factor / 2;
        for (int i = 0; i < dst.Length; i++) dst[i] = selection.CoverageAt((x0 + i) * factor + factor / 2, sy);
    }

    private static void ReadRow(Plane p, int start, Span<float> dst)
    {
        switch (p.BitDepth)
        {
            case 8:
                var bytes = p.Data.AsSpan(start, dst.Length);
                for (int i = 0; i < dst.Length; i++) dst[i] = bytes[i] * (1f / 255f);
                break;
            case 16:
                var words = p.AsUInt16().Slice(start, dst.Length);
                for (int i = 0; i < dst.Length; i++) dst[i] = words[i] * (1f / 65535f);
                break;
            default:
                p.AsSingle().Slice(start, dst.Length).CopyTo(dst);
                break;
        }
    }

    private static void WriteRow(Plane p, int start, ReadOnlySpan<float> src)
    {
        switch (p.BitDepth)
        {
            case 8:
                var bytes = p.Data.AsSpan(start, src.Length);
                for (int i = 0; i < src.Length; i++) bytes[i] = (byte)MathF.Round(Math.Clamp(src[i], 0f, 1f) * 255f);
                break;
            case 16:
                var words = p.AsUInt16().Slice(start, src.Length);
                for (int i = 0; i < src.Length; i++) words[i] = (ushort)MathF.Round(Math.Clamp(src[i], 0f, 1f) * 65535f);
                break;
            default:
                var floats = p.AsSingle().Slice(start, src.Length);
                for (int i = 0; i < src.Length; i++) floats[i] = Math.Clamp(src[i], 0f, 1f);
                break;
        }
    }
}
