using Strayta.Core.Selection;

namespace Strayta.Core.Painting;

/// <summary>
/// Draws a <see cref="GradientSpec"/> into a layer's pixels or its mask. Like <see cref="StrokeBaker"/> it returns
/// new rasters and never modifies the old ones, so undo only swaps references.
/// </summary>
/// <remarks>
/// The gradient covers the whole canvas (or the selection, whose soft edges apply partially) and is blended over
/// the existing pixels (source-over), so its transparent stops let them show through. The same code draws the
/// full-resolution result and the live preview: for the preview the caller passes the downsampled layer, a
/// <see cref="GradientSpec.Scaled"/> spec and <c>selectionFactor</c>, the preview's scale, so the selection is
/// sampled at the center of each preview pixel.
/// </remarks>
public static class GradientPainter
{
    /// <summary>
    /// The layer's pixels with the gradient drawn over them. The layer grows to cover the filled area; a layer without
    /// transparency (a Background) stays opaque as long as it does not need to grow.
    /// </summary>
    /// <param name="old">The layer's pixels (null for an empty layer) covering <paramref name="oldBounds"/>.</param>
    /// <param name="canvas">The document bounds (at the preview's scale for a preview).</param>
    public static (Raster? Pixels, PixelRect Bounds) Apply(Raster? old, PixelRect oldBounds, GradientSpec spec, SelectionMask? selection,
        PixelRect canvas, ColorMode mode, int bitDepth, int selectionFactor = 1, CancellationToken cancel = default)
    {
        if (mode is not (ColorMode.Rgb or ColorMode.Grayscale))
            throw new NotSupportedException($"Painting in {mode} documents is not supported yet.");
        if (old is null) oldBounds = PixelRect.Empty;
        var region = Region(canvas, selection, selectionFactor);
        if (region.IsEmpty || spec.IsDegenerate) return (old, oldBounds);
        if (old is not null) bitDepth = old.BitDepth;

        var bounds = Union(oldBounds, region);
        bool withAlpha = !(old is not null && old.Alpha is null && bounds == oldBounds);
        int w = bounds.Width, h = bounds.Height, colors = mode.ColorChannelCount();
        var planes = Enumerable.Range(0, colors).Select(_ => Plane.Create(w, h, bitDepth)).ToArray();
        var alpha = withAlpha ? Plane.Create(w, h, bitDepth) : null;
        float levels = bitDepth == 8 ? 255f : bitDepth == 16 ? 65535f : 0f;
        bool dither = spec.Dither && levels > 0f;
        bool gray = mode == ColorMode.Grayscale;
        var lut = spec.Lut;
        float opacity = spec.Opacity;
        var paintMode = spec.Mode;
        bool normal = paintMode.IsNormal;

        var options = new ParallelOptions { CancellationToken = cancel };
        Parallel.For(0, h, options, () => new RowBuffers(w, colors), (row, _, buf) =>
        {
            int y = bounds.Top + row;
            // Start from the old pixels (transparent outside them; opaque where the layer has no alpha).
            buf.Alpha.AsSpan().Clear();
            foreach (var c in buf.Color) c.AsSpan().Clear();
            if (old is not null && y >= oldBounds.Top && y < oldBounds.Bottom)
            {
                int src = (y - oldBounds.Top) * oldBounds.Width, at = oldBounds.Left - bounds.Left;
                for (int k = 0; k < colors; k++) ReadRow(old.ColorPlanes[k], src, buf.Color[k].AsSpan(at, oldBounds.Width));
                if (old.Alpha is { } a) ReadRow(a, src, buf.Alpha.AsSpan(at, oldBounds.Width));
                else buf.Alpha.AsSpan(at, oldBounds.Width).Fill(1f);
            }

            if (y >= region.Top && y < region.Bottom)
            {
                ReadSelection(selection, selectionFactor, y, region.Left, buf.Selection.AsSpan(0, region.Width));
                float cy = y + 0.5f;
                Span<float> pixel = stackalloc float[colors], paint = stackalloc float[colors];
                for (int x = region.Left; x < region.Right; x++)
                {
                    float s = buf.Selection[x - region.Left] * (1f / 255f);
                    if (s <= 0f) continue;
                    var (r, g, b, ga) = lut.At(spec.PositionAt(x + 0.5f, cy));
                    float cov = ga * opacity * s;
                    if (cov <= 0f) continue;
                    int i = x - bounds.Left;
                    float n = dither ? GradientSpec.DitherAt(x, y) / levels : 0f;
                    if (!normal)
                    {
                        float pa = withAlpha ? buf.Alpha[i] : 1f;
                        for (int k = 0; k < colors; k++) pixel[k] = buf.Color[k][i];
                        if (gray) paint[0] = 0.299f * r + 0.587f * g + 0.114f * b;
                        else (paint[0], paint[1], paint[2]) = (r, g, b);
                        PaintBlender.Paint(paintMode, pixel, ref pa, paint, cov, x, y);
                        for (int k = 0; k < colors; k++) buf.Color[k][i] = pixel[k] + n;
                        buf.Alpha[i] = withAlpha ? pa : 1f;
                        continue;
                    }
                    float oa = buf.Alpha[i];
                    float na = cov + oa * (1f - cov);
                    float keep = na > 0f ? oa * (1f - cov) / na : 0f, add = na > 0f ? cov / na : 1f;
                    if (gray) buf.Color[0][i] = (0.299f * r + 0.587f * g + 0.114f * b) * add + buf.Color[0][i] * keep + n;
                    else
                    {
                        buf.Color[0][i] = r * add + buf.Color[0][i] * keep + n;
                        buf.Color[1][i] = g * add + buf.Color[1][i] * keep + n;
                        buf.Color[2][i] = b * add + buf.Color[2][i] * keep + n;
                    }
                    buf.Alpha[i] = withAlpha ? na + n : 1f;
                }
            }

            int dst = row * w;
            for (int k = 0; k < colors; k++) WriteRow(planes[k], dst, buf.Color[k]);
            if (alpha is not null) WriteRow(alpha, dst, buf.Alpha);
            return buf;
        }, _ => { });

        return (new Raster(mode, planes, alpha), bounds);
    }

    /// <summary>
    /// The gradient drawn into a layer mask as gray (the colors' luminance: black hides, white reveals), each sample
    /// moving toward it by the gradient's opacity, the way a brush paints a mask. The mask grows to cover the filled
    /// area; area it did not cover before starts at its default color.
    /// </summary>
    public static LayerMask ApplyToMask(LayerMask mask, GradientSpec spec, SelectionMask? selection, PixelRect canvas, int bitDepth,
        int selectionFactor = 1, CancellationToken cancel = default)
    {
        var old = mask.Pixels;
        var oldBounds = old is null ? PixelRect.Empty : mask.Bounds;
        var region = Region(canvas, selection, selectionFactor);
        if (region.IsEmpty || spec.IsDegenerate) return mask;
        if (old is not null) bitDepth = old.BitDepth;

        var bounds = Union(oldBounds, region);
        int w = bounds.Width, h = bounds.Height;
        var plane = Plane.Create(w, h, bitDepth);
        float outside = mask.DefaultColor / 255f;
        float levels = bitDepth == 8 ? 255f : bitDepth == 16 ? 65535f : 0f;
        bool dither = spec.Dither && levels > 0f;
        var lut = spec.Lut;
        float opacity = spec.Opacity;

        var options = new ParallelOptions { CancellationToken = cancel };
        Parallel.For(0, h, options, () => new RowBuffers(w, 1), (row, _, buf) =>
        {
            int y = bounds.Top + row;
            var m = buf.Color[0];
            m.AsSpan().Fill(outside);
            if (old is not null && y >= oldBounds.Top && y < oldBounds.Bottom)
                ReadRow(old, (y - oldBounds.Top) * oldBounds.Width, m.AsSpan(oldBounds.Left - bounds.Left, oldBounds.Width));
            if (y >= region.Top && y < region.Bottom)
            {
                ReadSelection(selection, selectionFactor, y, region.Left, buf.Selection.AsSpan(0, region.Width));
                float cy = y + 0.5f;
                for (int x = region.Left; x < region.Right; x++)
                {
                    float s = buf.Selection[x - region.Left] * (1f / 255f);
                    if (s <= 0f) continue;
                    var (r, g, b, ga) = lut.At(spec.PositionAt(x + 0.5f, cy));
                    float cov = ga * opacity * s;
                    if (cov <= 0f) continue;
                    int i = x - bounds.Left;
                    float target = 0.299f * r + 0.587f * g + 0.114f * b;
                    m[i] += (target - m[i]) * cov + (dither ? GradientSpec.DitherAt(x, y) / levels : 0f);
                }
            }
            WriteRow(plane, row * w, m);
            return buf;
        }, _ => { });

        return new LayerMask
        {
            Bounds = bounds,
            Pixels = plane,
            DefaultColor = mask.DefaultColor,
            Disabled = mask.Disabled,
            PositionRelativeToLayer = mask.PositionRelativeToLayer,
        };
    }

    /// <summary>The area the gradient covers: the canvas, or the part of it the selection reaches.</summary>
    public static PixelRect Region(PixelRect canvas, SelectionMask? selection, int selectionFactor = 1)
    {
        if (selection is null) return canvas;
        var b = selection.Bounds;
        int f = selectionFactor;
        var scaled = f == 1 ? b : new PixelRect(FloorDiv(b.Left, f), FloorDiv(b.Top, f), -FloorDiv(-b.Right, f), -FloorDiv(-b.Bottom, f));
        return canvas.Intersect(scaled);
    }

    private sealed class RowBuffers(int width, int colors)
    {
        public readonly float[][] Color = Enumerable.Range(0, colors).Select(_ => new float[width]).ToArray();
        public readonly float[] Alpha = new float[width];
        public readonly byte[] Selection = new byte[width];
    }

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

    private static PixelRect Union(PixelRect a, PixelRect b) =>
        a.IsEmpty ? b : b.IsEmpty ? a
        : new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
