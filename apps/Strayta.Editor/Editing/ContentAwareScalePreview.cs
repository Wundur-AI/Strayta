using Strayta.Core;
using Strayta.Rendering;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>
/// Shows Content-Aware Scale on the canvas: the layer's box is seam-carved to the box's new size at the preview's
/// resolution and placed where the box is. The width's seam order is worked out once per preview scale (any narrower or
/// wider width then follows at once, <see cref="SeamCarver"/>); the height is carved from that result when it changes.
/// </summary>
internal sealed class ContentAwareScalePreview(PixelLayer layer, PixelRect box, float[]? protect)
{
    private sealed record Cut(Raster Input, int Factor, float[] Rgba, float[]? Protect, int Width, int Height);

    private readonly Dictionary<int, (Cut Cut, int[] Order, int Seams)> _widthOrders = [];
    private readonly Dictionary<(int Factor, int Width, int Height, bool Full), (Raster? Pixels, PixelRect Bounds)> _results = [];

    /// <summary>The box's size and place now (document pixels). Set on the UI thread; each <see cref="Prepare"/> captures it.</summary>
    public (int Width, int Height, int Left, int Top) Target { get; set; } = (box.Width, box.Height, box.Left, box.Top);

    public PixelLayer Layer => layer;
    public PixelRect Box => box;

    public Action<CancellationToken>? Prepare(PreviewDocument preview, bool full)
    {
        if (preview.ProxyOf(layer) is not PixelLayer proxy) return null;
        int f = preview.Factor;
        var target = Target;
        var pixels = proxy.Pixels;
        var bounds = proxy.Bounds;
        return cancel =>
        {
            if (pixels is null) return;
            (proxy.Pixels, proxy.Bounds) = Carve(pixels, bounds, f, target, cancel);
        };
    }

    private (Raster?, PixelRect) Carve(Raster pixels, PixelRect bounds, int f, (int Width, int Height, int Left, int Top) target, CancellationToken cancel)
    {
        int newW = Math.Max(1, (int)Math.Round(target.Width / (double)f)), newH = Math.Max(1, (int)Math.Round(target.Height / (double)f));
        var place = new PixelRect((int)Math.Round(target.Left / (double)f), (int)Math.Round(target.Top / (double)f), 0, 0);
        (Raster? Pixels, PixelRect Bounds) result;
        lock (_results)
            if (_results.TryGetValue((f, newW, newH, false), out var hit))
                return hit.Pixels is null ? (null, PixelRect.Empty) : (hit.Pixels, Offset(hit.Bounds, place.Left, place.Top));
        var cut = CutFor(pixels, bounds, f);
        // Width from the seam order (computed once for as many seams as it needs), height from that result.
        float[] image = cut.Rgba;
        float[]? guard = cut.Protect;
        int w = cut.Width, h = cut.Height;
        if (newW != w)
        {
            int need = Math.Abs(newW - w);
            if (need < w && newW <= w + w / 2)
            {
                var (_, order, _) = WidthOrder(cut, need, cancel);
                image = SeamCarver.ApplyWidth(image, w, h, order, newW);
                if (guard is not null) guard = SeamCarver.ApplyWidthPlane(guard, w, h, order, newW);
            }
            else image = SeamCarver.Retarget(image, w, h, newW, h, guard, cancel);
            w = newW;
        }
        if (newH != h) image = SeamCarver.Retarget(image, w, h, w, newH, guard, cancel);
        var raster = SeamCarver.FromRgba(image, newW, newH, pixels.ColorMode, pixels.BitDepth);
        result = (raster, new PixelRect(0, 0, newW, newH));
        lock (_results)
        {
            if (_results.Count > 64) _results.Clear();
            _results[(f, newW, newH, false)] = result;
        }
        return (raster, Offset(result.Bounds, place.Left, place.Top));
    }

    private static PixelRect Offset(PixelRect r, int dx, int dy) => new(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);

    /// <summary>The box's part of the (proxy) pixels as premultiplied RGBA, with the protection at the same scale.</summary>
    private Cut CutFor(Raster pixels, PixelRect bounds, int f)
    {
        lock (_widthOrders)
            if (_widthOrders.TryGetValue(f, out var known) && ReferenceEquals(known.Cut.Input, pixels)) return known.Cut;
        var area = new PixelRect(box.Left / f, box.Top / f, (box.Right + f - 1) / f, (box.Bottom + f - 1) / f);
        var (part, partBounds) = Resampler.Crop(pixels, bounds, area);
        int w = Math.Max(1, area.Width), h = Math.Max(1, area.Height);
        var rgba = new float[w * h * 4];
        if (part is not null)
        {
            var src = SeamCarver.ToRgba(part);
            for (int y = 0; y < partBounds.Height; y++)
                Array.Copy(src, y * partBounds.Width * 4, rgba, ((partBounds.Top - area.Top + y) * w + partBounds.Left - area.Left) * 4, partBounds.Width * 4);
        }
        float[]? guard = null;
        if (protect is not null)
        {
            guard = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    // The box's protection at full resolution, sampled at this pixel's center.
                    int px = Math.Min(box.Width - 1, x * f + f / 2), py = Math.Min(box.Height - 1, y * f + f / 2);
                    guard[y * w + x] = protect[py * box.Width + px];
                }
        }
        return new Cut(pixels, f, rgba, guard, w, h);
    }

    private (Cut Cut, int[] Order, int Seams) WidthOrder(Cut cut, int need, CancellationToken cancel)
    {
        lock (_widthOrders)
            if (_widthOrders.TryGetValue(cut.Factor, out var known) && ReferenceEquals(known.Cut, cut) && known.Seams >= need) return known;
        // Half the width at once, so a drag does not work it out again and again.
        int seams = Math.Min(cut.Width - 1, Math.Max(need, cut.Width / 2));
        var order = SeamCarver.RemovalOrder(cut.Rgba, cut.Width, cut.Height, seams, cut.Protect, cancel);
        var entry = (cut, order, seams);
        lock (_widthOrders) _widthOrders[cut.Factor] = entry;
        return entry;
    }
}
