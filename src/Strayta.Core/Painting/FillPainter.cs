using Strayta.Core.Selection;

namespace Strayta.Core.Painting;

/// <summary>What a fill paints: a flat color, or a pattern tiled from the document's origin.</summary>
public abstract record FillSource
{
    public sealed record Color(RgbColor Value) : FillSource;
    public sealed record Tiled(Pattern Pattern) : FillSource;
}

/// <summary>
/// Photoshop's fill options as Edit › Fill, Edit › Stroke and the Paint Bucket share them: what to paint, the painting
/// mode, the opacity (0..1) and Preserve Transparency.
/// </summary>
public sealed record FillOptions(FillSource Source)
{
    public PaintMode Mode { get; init; } = PaintMode.Normal;
    public float Opacity { get; init; } = 1f;

    /// <summary>Paints only where the layer already has pixels and keeps its transparency exactly (color changes only).</summary>
    public bool PreserveTransparency { get; init; }
}

/// <summary>
/// Paints a <see cref="FillOptions"/> into a layer through a coverage mask (a selection, a Paint Bucket region or a
/// stroke band). Like <see cref="SelectionPainter"/> it returns new rasters and never modifies the layer's own.
/// </summary>
public static class FillPainter
{
    /// <summary>
    /// The layer's pixels with the fill painted through <paramref name="coverage"/>. The layer grows to cover the area
    /// unless transparency is preserved; a layer without transparency (a Background) stays opaque while it need not grow.
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds) Fill(PixelLayer layer, SelectionMask coverage, FillOptions options, ColorMode mode, int bitDepth,
        CancellationToken cancel = default)
    {
        if (mode is not (ColorMode.Rgb or ColorMode.Grayscale))
            throw new NotSupportedException($"Editing pixels in {mode} documents is not supported yet.");
        var old = layer.Pixels;
        var oldBounds = old is null ? PixelRect.Empty : layer.Bounds;
        if (old is not null) bitDepth = old.BitDepth;
        bool preserve = options.PreserveTransparency;
        if (preserve && old is null) return (old, layer.Bounds);
        // Behind and Clear only change transparent or existing pixels; with transparency preserved Clear does nothing.
        var bounds = preserve || options.Mode == PaintMode.Clear ? oldBounds
            : oldBounds.IsEmpty ? coverage.Bounds : Union(oldBounds, coverage.Bounds);
        if (bounds.IsEmpty) return (old, layer.Bounds);
        bool withAlpha = !(old is not null && old.Alpha is null && bounds == oldBounds);
        int w = bounds.Width, h = bounds.Height, colors = mode.ColorChannelCount();
        var planes = Enumerable.Range(0, colors).Select(_ => Plane.Create(w, h, bitDepth)).ToArray();
        var alpha = withAlpha ? Plane.Create(w, h, bitDepth) : null;
        bool gray = mode == ColorMode.Grayscale;
        float opacity = Math.Clamp(options.Opacity, 0f, 1f);
        var source = options.Source;
        var paintMode = options.Mode;

        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, () => new byte[w], (row, _, sel) =>
        {
            int y = bounds.Top + row;
            coverage.CopyRow(y, bounds.Left, sel);
            Span<float> c = stackalloc float[colors], paint = stackalloc float[colors];
            if (source is FillSource.Color flat) SetPaint(paint, flat.Value.R, flat.Value.G, flat.Value.B, gray);
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
                float cover = sel[col] * (1f / 255f) * opacity;
                if (cover > 0f)
                {
                    if (source is FillSource.Tiled tiled)
                    {
                        var (r, g, b, pa) = tiled.Pattern.At(x, y);
                        SetPaint(paint, r, g, b, gray);
                        cover *= pa;
                    }
                    if (preserve)
                    {
                        float keep = a;
                        a = 1f;
                        a = PaintBlender.Paint(paintMode, c, a, paint, cover, c.Length, x, y);
                        a = keep;
                    }
                    else a = PaintBlender.Paint(paintMode, c, a, paint, cover, c.Length, x, y);
                }
                for (int k = 0; k < colors; k++) Set(planes[k], i, c[k]);
                if (alpha is not null) Set(alpha, i, a);
            }
            return sel;
        }, _ => { });

        return (new Raster(mode, planes, alpha), bounds);
    }

    private static void SetPaint(Span<float> paint, float r, float g, float b, bool gray)
    {
        if (gray) paint[0] = 0.299f * r + 0.587f * g + 0.114f * b;
        else (paint[0], paint[1], paint[2]) = (r, g, b);
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
