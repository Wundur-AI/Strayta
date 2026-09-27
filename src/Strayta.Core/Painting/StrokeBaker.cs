namespace Strayta.Core.Painting;

/// <summary>Applies a finished stroke to a layer, producing new pixels (the old raster is left untouched).</summary>
public static class StrokeBaker
{
    /// <summary>
    /// Brush strokes blend the color over the layer (source-over); the eraser reduces alpha. Brush strokes may
    /// grow the layer. Layers without transparency gain an alpha channel when erased or grown.
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds) Bake(PixelLayer layer, PaintStroke stroke, ColorMode mode, int bitDepth)
    {
        if (mode is not (ColorMode.Rgb or ColorMode.Grayscale))
            throw new NotSupportedException($"Painting in {mode} documents is not supported yet.");

        var old = layer.Pixels;
        var oldBounds = old is null ? PixelRect.Empty : layer.Bounds;
        var bounds = stroke.Erase ? oldBounds : Union(oldBounds, stroke.Bounds);
        if (bounds.IsEmpty) return (old, oldBounds);

        int w = bounds.Width, h = bounds.Height;
        int colors = mode.ColorChannelCount();
        var planes = Enumerable.Range(0, colors).Select(_ => Plane.Create(w, h, bitDepth)).ToArray();
        var alpha = Plane.Create(w, h, bitDepth);
        float opacity = stroke.Brush.Opacity;
        float[] brush = mode == ColorMode.Grayscale
            ? [0.299f * stroke.Color.R + 0.587f * stroke.Color.G + 0.114f * stroke.Color.B]
            : [stroke.Color.R, stroke.Color.G, stroke.Color.B];

        Parallel.For(0, h, row =>
        {
            int y = bounds.Top + row;
            Span<float> c = stackalloc float[3];
            for (int col = 0; col < w; col++)
            {
                int x = bounds.Left + col, i = row * w + col;

                // Existing pixel (transparent outside the old bounds; opaque if the layer has no alpha).
                float a = 0f;
                c.Clear();
                if (old is not null && x >= oldBounds.Left && x < oldBounds.Right && y >= oldBounds.Top && y < oldBounds.Bottom)
                {
                    int s = (y - oldBounds.Top) * oldBounds.Width + (x - oldBounds.Left);
                    a = old.Alpha?.GetNormalized(s) ?? 1f;
                    for (int k = 0; k < colors; k++) c[k] = old.ColorPlanes[k].GetNormalized(s);
                }

                float cov = stroke.CoverageAt(x, y) * opacity;
                if (cov > 0f)
                {
                    if (stroke.Erase) a *= 1f - cov;
                    else
                    {
                        float na = cov + a * (1f - cov);
                        for (int k = 0; k < colors; k++)
                            c[k] = na > 0f ? (brush[k] * cov + c[k] * a * (1f - cov)) / na : brush[k];
                        a = na;
                    }
                }

                for (int k = 0; k < colors; k++) Set(planes[k], i, c[k]);
                Set(alpha, i, a);
            }
        });

        return (new Raster(mode, planes, alpha), bounds);
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
