namespace Strayta.Core.Painting;

/// <summary>
/// Mask edits that produce new planes (the old mask is left untouched, like <see cref="StrokeBaker"/>), so
/// render caches and thumbnails keyed by plane identity see the change.
/// </summary>
public static class MaskBaker
{
    /// <summary>
    /// Paints a mask stroke: each sample moves toward the stroke's gray by coverage × opacity, the same
    /// source-over a brush does on an opaque grayscale channel. The mask grows to cover the stroke; area it
    /// did not cover before starts at its default color, so the result looks the same outside the stroke.
    /// A cloning stroke (<see cref="PaintStroke.Source"/>, a mask source) moves each sample toward the source's gray there.
    /// </summary>
    public static LayerMask Bake(LayerMask mask, PaintStroke stroke, int bitDepth)
    {
        if (!stroke.TargetsMask) throw new ArgumentException("Not a mask stroke.", nameof(stroke));
        var old = mask.Pixels;
        var oldBounds = old is null ? PixelRect.Empty : mask.Bounds;
        var bounds = Union(oldBounds, stroke.Bounds);
        if (stroke.Bounds.IsEmpty || bounds.IsEmpty) return mask;
        if (old is not null) bitDepth = old.BitDepth;

        int w = bounds.Width, h = bounds.Height;
        var plane = Plane.Create(w, h, bitDepth);
        float outside = mask.DefaultColor / 255f;
        float gray = stroke.Color.R, opacity = stroke.Brush.Opacity;
        var sb = stroke.Bounds;

        Parallel.For(0, h, row =>
        {
            int y = bounds.Top + row;
            int dst = row * w;
            // Start from the old mask: its samples where it had them, its default color elsewhere.
            FillRow(plane, dst, w, outside);
            if (old is not null && y >= oldBounds.Top && y < oldBounds.Bottom)
            {
                int bpp = bitDepth / 8;
                int src = (y - oldBounds.Top) * oldBounds.Width;
                Buffer.BlockCopy(old.Data, src * bpp, plane.Data, (dst + oldBounds.Left - bounds.Left) * bpp, oldBounds.Width * bpp);
            }
            if (y < sb.Top || y >= sb.Bottom) return;

            Span<float> sample = stackalloc float[1];
            for (int x = sb.Left; x < sb.Right; x++)
            {
                float cov = stroke.CoverageAt(x, y) * opacity;
                if (cov <= 0f) continue;
                float target = gray;
                if (stroke.Source is { } source)
                {
                    cov *= source.Read(x, y, sample, 1);
                    target = sample[0];
                }
                int i = dst + x - bounds.Left;
                float m = plane.GetNormalized(i);
                Set(plane, i, m + (target - m) * cov);
            }
        });

        return new LayerMask
        {
            Bounds = bounds,
            Pixels = plane,
            DefaultColor = mask.DefaultColor,
            Disabled = mask.Disabled,
            PositionRelativeToLayer = mask.PositionRelativeToLayer,
        };
    }

    /// <summary>
    /// Layer › Layer Mask › Apply: multiplies the mask into the layer's transparency. Color planes are shared
    /// with the old raster; a layer without transparency gains an alpha channel. Returns null for empty layers.
    /// </summary>
    public static Raster? ApplyToPixels(PixelLayer layer, LayerMask mask)
    {
        if (layer.Pixels is not { } px) return null;
        var b = layer.Bounds;
        int w = b.Width, h = b.Height;
        var alpha = Plane.Create(w, h, px.BitDepth);
        Parallel.For(0, h, row =>
        {
            int y = b.Top + row;
            for (int col = 0; col < w; col++)
            {
                int i = row * w + col;
                float a = px.Alpha?.GetNormalized(i) ?? 1f;
                Set(alpha, i, a * Sample(mask, b.Left + col, y));
            }
        });
        return new Raster(px.ColorMode, px.ColorPlanes, alpha);
    }

    /// <summary>The mask's value at a document pixel, 0..1.</summary>
    public static float Sample(LayerMask mask, int x, int y)
    {
        var b = mask.Bounds;
        if (mask.Pixels is { } p && x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom)
            return p.GetNormalized((y - b.Top) * b.Width + (x - b.Left));
        return mask.DefaultColor / 255f;
    }

    private static void FillRow(Plane p, int start, int n, float v)
    {
        switch (p.BitDepth)
        {
            case 8: p.Data.AsSpan(start, n).Fill((byte)MathF.Round(v * 255f)); break;
            case 16: p.AsUInt16().Slice(start, n).Fill((ushort)MathF.Round(v * 65535f)); break;
            default: p.AsSingle().Slice(start, n).Fill(v); break;
        }
    }

    private static void Set(Plane p, int i, float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        switch (p.BitDepth)
        {
            case 8: p.Data[i] = (byte)MathF.Round(v * 255f); break;
            case 16: p.AsUInt16()[i] = (ushort)MathF.Round(v * 65535f); break;
            default: p.AsSingle()[i] = v; break;
        }
    }

    private static PixelRect Union(PixelRect a, PixelRect b) =>
        a.IsEmpty ? b : b.IsEmpty ? a
        : new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
}
