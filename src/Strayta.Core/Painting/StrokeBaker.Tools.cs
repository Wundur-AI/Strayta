namespace Strayta.Core.Painting;

// Committing the toning (Dodge, Burn, Sponge) and focus (Blur, Sharpen, Smudge) strokes. Toning changes each pixel by
// its coverage (Toning); the focus tools' working image (LocalStroke) already holds the result, so it is copied in where
// the stroke has been. The renderer's live overlay does the same per pixel, so the preview and the commit agree.
public static partial class StrokeBaker
{
    private static (Raster? Pixels, PixelRect Bounds) BakeTool(PixelLayer layer, PaintStroke stroke, ColorMode mode, int bitDepth)
    {
        var old = layer.Pixels;
        var oldBounds = old is null ? PixelRect.Empty : layer.Bounds;
        // Toning never adds pixels; blurring and smudging can spread them past the layer's edge (or fill an empty layer
        // when sampling all layers).
        var bounds = stroke.Local is null ? oldBounds : Union(oldBounds, stroke.Bounds);
        if (bounds.IsEmpty) return (old, oldBounds);

        int w = bounds.Width, h = bounds.Height;
        int colors = mode.ColorChannelCount();
        var planes = Enumerable.Range(0, colors).Select(_ => Plane.Create(w, h, bitDepth)).ToArray();
        bool keepOpaque = old is not null && old.Alpha is null && bounds == oldBounds;
        var alpha = keepOpaque ? null : Plane.Create(w, h, bitDepth);
        bool copyRows = old is not null && bounds == oldBounds && old.BitDepth == bitDepth && old.ColorPlanes.Count == colors;
        var sb = stroke.Bounds.Intersect(bounds);
        float opacity = stroke.Brush.Opacity;
        var tone = stroke.Tone;
        var local = stroke.Local;

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
            for (int col = from; col < to; col++)
            {
                int x = bounds.Left + col, i = row * w + col;
                float a = 0f;
                c.Clear();
                if (old is not null && x >= oldBounds.Left && x < oldBounds.Right && y >= oldBounds.Top && y < oldBounds.Bottom)
                {
                    int s = (y - oldBounds.Top) * oldBounds.Width + (x - oldBounds.Left);
                    a = old.Alpha?.GetNormalized(s) ?? 1f;
                    for (int k = 0; k < colors; k++) c[k] = old.ColorPlanes[Math.Min(k, old.ColorPlanes.Count - 1)].GetNormalized(s);
                }
                float cov = stroke.CoverageAt(x, y);
                if (cov > 0f)
                {
                    if (tone is not null) Toning.Apply(tone, c, colors, cov * opacity);
                    else
                    {
                        float na = local!.Read(x, y, c);
                        if (!keepOpaque) a = na;
                    }
                }
                for (int k = 0; k < colors; k++) Set(planes[k], i, c[k]);
                if (alpha is not null) Set(alpha, i, a);
            }
        });
        return (new Raster(mode, planes, alpha), bounds);
    }

    /// <summary>
    /// A toning or focus stroke's new value for a gray sample (a layer mask) at (<paramref name="x"/>, <paramref name="y"/>),
    /// where the stroke's coverage × opacity is <paramref name="cov"/> (&gt; 0).
    /// </summary>
    public static float ToolGray(PaintStroke stroke, int x, int y, float value, float cov)
    {
        Span<float> g = stackalloc float[3];
        if (stroke.Local is { } local)
        {
            local.Read(x, y, g);
            return g[0];
        }
        g[0] = value;
        if (stroke.Tone is { } tone) Toning.Apply(tone, g, 1, cov);
        return g[0];
    }
}
