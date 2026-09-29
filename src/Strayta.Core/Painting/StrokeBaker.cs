namespace Strayta.Core.Painting;

/// <summary>Applies a finished stroke to a layer, producing new pixels (the old raster is left untouched).</summary>
public static class StrokeBaker
{
    /// <summary>
    /// Brush strokes blend the color over the layer in the stroke's <see cref="PaintMode"/> (see <see cref="PaintBlender"/>;
    /// Normal is source-over, Clear erases); the eraser reduces alpha. Brush strokes may
    /// grow the layer. Layers without transparency gain an alpha channel when erased or grown.
    /// Cloning strokes (<see cref="PaintStroke.Source"/>) blend each pixel's source color, weighted by the source's
    /// alpha; they keep a layer without transparency (a Background) opaque when they stay inside it, as in Photoshop.
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds) Bake(PixelLayer layer, PaintStroke stroke, ColorMode mode, int bitDepth)
    {
        if (mode is not (ColorMode.Rgb or ColorMode.Grayscale))
            throw new NotSupportedException($"Painting in {mode} documents is not supported yet.");

        var old = layer.Pixels;
        var oldBounds = old is null ? PixelRect.Empty : layer.Bounds;
        var bounds = stroke.Erase || stroke.Mode == PaintMode.Clear ? oldBounds : Union(oldBounds, stroke.Bounds);
        if (bounds.IsEmpty) return (old, oldBounds);

        int w = bounds.Width, h = bounds.Height;
        int colors = mode.ColorChannelCount();
        var planes = Enumerable.Range(0, colors).Select(_ => Plane.Create(w, h, bitDepth)).ToArray();
        var source = stroke.Source;
        var paintMode = stroke.Mode;
        bool keepOpaque = source is not null && old is not null && old.Alpha is null && bounds == oldBounds && paintMode != PaintMode.Clear;
        var alpha = keepOpaque ? null : Plane.Create(w, h, bitDepth);
        float opacity = stroke.Brush.Opacity;
        float[] brush = mode == ColorMode.Grayscale
            ? [0.299f * stroke.Color.R + 0.587f * stroke.Color.G + 0.114f * stroke.Color.B]
            : [stroke.Color.R, stroke.Color.G, stroke.Color.B];

        // When the layer keeps its size, rows the stroke does not reach (and the rest of the rows it does) are copied
        // as they are, so committing a small stroke on a large layer costs little more than the stroke itself.
        bool copyRows = old is not null && bounds == oldBounds && old.BitDepth == bitDepth && old.ColorPlanes.Count == colors;
        var sb = stroke.Bounds.Intersect(bounds);

        Parallel.For(0, h, row =>
        {
            int y = bounds.Top + row;
            int from = 0, to = w;
            if (copyRows)
            {
                CopyRow(old!, alpha, planes, row, w, bitDepth);
                (from, to) = y < sb.Top || y >= sb.Bottom ? (0, 0) : (sb.Left - bounds.Left, sb.Right - bounds.Left);
            }
            Span<float> c = stackalloc float[3];
            Span<float> src = stackalloc float[3];
            for (int col = from; col < to; col++)
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
                    ReadOnlySpan<float> paint = src;
                    if (source is not null) cov *= source.Read(x, y, src, colors);
                    else paint = brush;
                    if (cov <= 0f) { }
                    else if (stroke.Erase) a *= 1f - cov;
                    else a = PaintBlender.Paint(paintMode, c, a, paint, cov, colors, x, y);
                }

                for (int k = 0; k < colors; k++) Set(planes[k], i, c[k]);
                if (alpha is not null) Set(alpha, i, a);
            }
        });

        return (new Raster(mode, planes, alpha), bounds);
    }

    /// <summary>Copies one row of an unchanged layer; a layer gaining an alpha channel gets it fully opaque.</summary>
    private static void CopyRow(Raster old, Plane? alpha, Plane[] planes, int row, int w, int bitDepth)
    {
        int bpp = bitDepth / 8, start = row * w * bpp, count = w * bpp;
        for (int k = 0; k < planes.Length; k++) Buffer.BlockCopy(old.ColorPlanes[k].Data, start, planes[k].Data, start, count);
        if (alpha is null) return;
        if (old.Alpha is { } a) Buffer.BlockCopy(a.Data, start, alpha.Data, start, count);
        else if (bitDepth == 8) alpha.Data.AsSpan(start, count).Fill(255);
        else if (bitDepth == 16) alpha.AsUInt16().Slice(row * w, w).Fill(ushort.MaxValue);
        else alpha.AsSingle().Slice(row * w, w).Fill(1f);
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
