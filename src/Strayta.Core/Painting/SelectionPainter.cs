using Strayta.Core.Selection;

namespace Strayta.Core.Painting;

/// <summary>
/// Whole-selection pixel operations (Edit > Fill, Clear, Copy). Like <see cref="StrokeBaker"/>, they return new
/// rasters and never modify the layer's existing one. Soft selection edges apply partially.
/// </summary>
public static class SelectionPainter
{
    /// <summary>Blends <paramref name="color"/> over the selected area (source-over), growing the layer if needed.</summary>
    public static (Raster? Pixels, PixelRect Bounds) Fill(PixelLayer layer, SelectionMask selection, RgbColor color, ColorMode mode, int bitDepth)
    {
        CheckMode(mode);
        var old = layer.Pixels;
        var oldBounds = old is null ? PixelRect.Empty : layer.Bounds;
        var bounds = oldBounds.IsEmpty ? selection.Bounds : Union(oldBounds, selection.Bounds);
        // A layer without transparency (a Background) stays opaque as long as it does not need to grow.
        bool keepOpaque = old is not null && old.Alpha is null && bounds == oldBounds;
        var brush = ColorFor(color, mode);
        return (Build(old, oldBounds, bounds, selection, mode, bitDepth, !keepOpaque, (Span<float> c, ref float a, float s) =>
        {
            if (s <= 0f) return;
            float na = s + a * (1f - s);
            for (int k = 0; k < c.Length; k++) c[k] = na > 0f ? (brush[k] * s + c[k] * a * (1f - s)) / na : brush[k];
            a = na;
        }), bounds);
    }

    /// <summary>
    /// Edit > Clear (Delete key): makes the selected area transparent. Layers without transparency, such as a
    /// Background, are filled with <paramref name="background"/> instead, as in Photoshop.
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds) Clear(PixelLayer layer, SelectionMask selection, RgbColor background, ColorMode mode, int bitDepth)
    {
        CheckMode(mode);
        if (layer.Pixels is not { } old) return (null, layer.Bounds);
        var bounds = layer.Bounds;
        if (old.Alpha is null) return Fill(layer, selection, background, mode, bitDepth);
        return (Build(old, bounds, bounds, selection, mode, bitDepth, withAlpha: true, (Span<float> _, ref float a, float s) => a *= 1f - s), bounds);
    }

    /// <summary>
    /// Edit > Copy: the layer's pixels inside the selection (all of them when <paramref name="selection"/> is null),
    /// with the selection's soft edges folded into alpha. Returns null when nothing visible is selected.
    /// </summary>
    public static (Raster Pixels, PixelRect Bounds)? Extract(PixelLayer layer, SelectionMask? selection)
    {
        if (layer.Pixels is not { } old) return null;
        var bounds = selection is null ? layer.Bounds : layer.Bounds.Intersect(selection.Bounds);
        if (bounds.IsEmpty) return null;
        var clip = selection ?? SelectionMask.All(bounds);
        return (Build(old, layer.Bounds, bounds, clip, old.ColorMode, old.BitDepth, withAlpha: true, (Span<float> _, ref float a, float s) => a *= s), bounds);
    }

    private delegate void PixelOp(Span<float> color, ref float alpha, float selected);

    /// <summary>
    /// Produces a raster covering <paramref name="bounds"/>: each pixel starts as the old layer's pixel (transparent
    /// outside its bounds) and is passed to <paramref name="op"/> with its selection coverage, 0..1.
    /// </summary>
    private static Raster Build(Raster? old, PixelRect oldBounds, PixelRect bounds, SelectionMask selection, ColorMode mode,
        int bitDepth, bool withAlpha, PixelOp op)
    {
        int w = bounds.Width, h = bounds.Height, colors = mode.ColorChannelCount();
        var planes = Enumerable.Range(0, colors).Select(_ => Plane.Create(w, h, bitDepth)).ToArray();
        var alpha = withAlpha ? Plane.Create(w, h, bitDepth) : null;

        Parallel.For(0, h, () => new byte[w], (row, _, sel) =>
        {
            int y = bounds.Top + row;
            selection.CopyRow(y, bounds.Left, sel);
            Span<float> c = stackalloc float[colors];
            for (int col = 0; col < w; col++)
            {
                int x = bounds.Left + col, i = row * w + col;
                float a = 0f;
                c.Clear();
                if (old is not null && x >= oldBounds.Left && x < oldBounds.Right && y >= oldBounds.Top && y < oldBounds.Bottom)
                {
                    int s = (y - oldBounds.Top) * oldBounds.Width + (x - oldBounds.Left);
                    a = old.Alpha?.GetNormalized(s) ?? 1f;
                    for (int k = 0; k < colors; k++) c[k] = old.ColorPlanes[k].GetNormalized(s);
                }
                op(c, ref a, sel[col] * (1f / 255f));
                for (int k = 0; k < colors; k++) Set(planes[k], i, c[k]);
                if (alpha is not null) Set(alpha, i, a);
            }
            return sel;
        }, _ => { });

        return new Raster(mode, planes, alpha);
    }

    private static float[] ColorFor(RgbColor color, ColorMode mode) => mode == ColorMode.Grayscale
        ? [0.299f * color.R + 0.587f * color.G + 0.114f * color.B]
        : [color.R, color.G, color.B];

    private static void CheckMode(ColorMode mode)
    {
        if (mode is not (ColorMode.Rgb or ColorMode.Grayscale))
            throw new NotSupportedException($"Editing pixels in {mode} documents is not supported yet.");
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
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
}
