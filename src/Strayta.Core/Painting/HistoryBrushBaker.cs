namespace Strayta.Core.Painting;

/// <summary>
/// Photoshop's History Brush: paints a layer back toward how it was in an earlier history state or snapshot. Unlike a
/// cloning stroke, which blends its source over the layer, the brush replaces: where it paints at full strength the
/// layer takes the earlier pixel exactly, transparency included (paint over something added since, and it disappears).
/// Partial coverage blends the two in premultiplied space, so edges fade without dark fringes.
/// </summary>
public static class HistoryBrushBaker
{
    /// <summary>
    /// The layer's new pixels after <paramref name="stroke"/> painted it from <paramref name="source"/> (the same
    /// layer's raster in the earlier state, placed at <paramref name="sourceBounds"/>). Layers without transparency (a
    /// Background) stay opaque. The old raster is not modified.
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds) Bake(PixelLayer layer, PaintStroke stroke, Raster? source, PixelRect sourceBounds, ColorMode mode, int bitDepth)
    {
        if (mode is not (ColorMode.Rgb or ColorMode.Grayscale))
            throw new NotSupportedException($"Painting in {mode} documents is not supported yet.");

        var old = layer.Pixels;
        var oldBounds = old is null ? PixelRect.Empty : layer.Bounds;
        var sourceArea = source is null ? PixelRect.Empty : sourceBounds;
        // Only where the earlier state had pixels can the layer grow; elsewhere the brush can only take pixels away.
        var bounds = Union(oldBounds, stroke.Bounds.Intersect(sourceArea));
        if (bounds.IsEmpty) return (old, oldBounds);

        int w = bounds.Width, h = bounds.Height;
        int colors = mode.ColorChannelCount();
        var planes = Enumerable.Range(0, colors).Select(_ => Plane.Create(w, h, bitDepth)).ToArray();
        bool opaque = old is not null && old.Alpha is null && bounds == oldBounds;
        var alpha = opaque ? null : Plane.Create(w, h, bitDepth);
        float opacity = stroke.Brush.Opacity;
        int sourceColors = source is null ? 0 : Math.Min(source.ColorPlanes.Count, 3);

        Parallel.For(0, h, row =>
        {
            int y = bounds.Top + row;
            Span<float> c = stackalloc float[3];
            Span<float> s = stackalloc float[3];
            for (int col = 0; col < w; col++)
            {
                int x = bounds.Left + col, i = row * w + col;
                float a = Read(old, oldBounds, x, y, c, colors);
                float cov = stroke.CoverageAt(x, y) * opacity;
                if (cov > 0f)
                {
                    float sa = ReadSource(source, sourceArea, x, y, s, sourceColors, colors);
                    if (opaque)
                    {
                        // A Background stays opaque: transparent source pixels leave it as it is.
                        float k = cov * sa;
                        for (int ch = 0; ch < colors; ch++) c[ch] += (s[ch] - c[ch]) * k;
                    }
                    else
                    {
                        float na = a + (sa - a) * cov;
                        for (int ch = 0; ch < colors; ch++)
                        {
                            float premultiplied = c[ch] * a + (s[ch] * sa - c[ch] * a) * cov;
                            c[ch] = na > 1e-6f ? premultiplied / na : 0f;
                        }
                        a = na;
                    }
                }
                for (int ch = 0; ch < colors; ch++) Set(planes[ch], i, c[ch]);
                if (alpha is not null) Set(alpha, i, a);
            }
        });
        return (new Raster(mode, planes, alpha), bounds);
    }

    /// <summary>The pixel's straight color and alpha (transparent outside the raster; opaque without an alpha plane).</summary>
    private static float Read(Raster? raster, PixelRect bounds, int x, int y, Span<float> color, int colors)
    {
        color.Clear();
        if (raster is null || x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom) return 0f;
        int i = (y - bounds.Top) * bounds.Width + (x - bounds.Left);
        for (int k = 0; k < colors; k++) color[k] = raster.ColorPlanes[Math.Min(k, raster.ColorPlanes.Count - 1)].GetNormalized(i);
        return raster.Alpha?.GetNormalized(i) ?? 1f;
    }

    private static float ReadSource(Raster? source, PixelRect bounds, int x, int y, Span<float> color, int sourceColors, int colors)
    {
        float a = Read(source, bounds, x, y, color, Math.Max(1, sourceColors));
        if (sourceColors == 3 && colors == 1) color[0] = 0.299f * color[0] + 0.587f * color[1] + 0.114f * color[2];
        else if (sourceColors == 1 && colors == 3) color[1] = color[2] = color[0];
        return a;
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
