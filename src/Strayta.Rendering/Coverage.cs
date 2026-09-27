using System.Runtime.CompilerServices;
using Strayta.Core;

namespace Strayta.Rendering;

/// <summary>
/// Works out where layers can actually be visible, so compositing can skip transparent or masked-out areas.
/// Scans are cached per <see cref="Plane"/> (pixel data is treated as immutable once rendered; editors that
/// paint into a plane must replace it, which the render cache also relies on).
/// </summary>
internal static class Coverage
{
    private sealed class Box(PixelRect rect) { public PixelRect Rect { get; } = rect; }
    private static readonly ConditionalWeakTable<Plane, Box> NonZeroCache = new();

    /// <summary>Bounding box (in plane coordinates) of samples that are not zero; empty if all are zero.</summary>
    public static PixelRect NonZeroBounds(Plane p) => NonZeroCache.GetValue(p, static p => new Box(Scan(p))).Rect;

    private static PixelRect Scan(Plane p)
    {
        int w = p.Width, h = p.Height;
        int top = -1, bottom = -1;
        var rowMin = new int[h];
        var rowMax = new int[h];
        Parallel.For(0, h, y =>
        {
            int lo = -1, hi = -1;
            for (int x = 0; x < w; x++)
            {
                if (IsZero(p, y * w + x)) continue;
                if (lo < 0) lo = x;
                hi = x;
            }
            rowMin[y] = lo;
            rowMax[y] = hi;
        });

        int left = int.MaxValue, right = -1;
        for (int y = 0; y < h; y++)
        {
            if (rowMin[y] < 0) continue;
            if (top < 0) top = y;
            bottom = y;
            left = Math.Min(left, rowMin[y]);
            right = Math.Max(right, rowMax[y]);
        }
        return top < 0 ? PixelRect.Empty : new PixelRect(left, top, right + 1, bottom + 1);
    }

    private static bool IsZero(Plane p, int i) => p.BitDepth switch
    {
        8 => p.Data[i] == 0,
        16 => p.AsUInt16()[i] == 0,
        _ => p.AsSingle()[i] <= 0f,
    };

    /// <summary>
    /// Where a mask can be non-zero within <paramref name="area"/> (document coordinates), or null when it may
    /// reveal anything there. The default color only matters outside the mask's bounds, so a mask whose
    /// bounds cover the whole area is limited to its non-zero pixels even if its default is white.
    /// </summary>
    public static PixelRect? MaskReach(LayerMask? mask, PixelRect area)
    {
        if (mask is null || mask.Disabled) return null;
        var outside = mask.DefaultColor == 0 ? PixelRect.Empty : OutsideBounds(mask.Bounds.Intersect(area), area);
        if (outside is null) return null;

        var inside = PixelRect.Empty;
        if (mask.Pixels is { } px && NonZeroBounds(px) is { IsEmpty: false } nz)
            inside = new PixelRect(mask.Bounds.Left + nz.Left, mask.Bounds.Top + nz.Top, mask.Bounds.Left + nz.Right, mask.Bounds.Top + nz.Bottom).Intersect(area);
        return Union(inside, outside.Value);
    }

    /// <summary>
    /// <see cref="MaskReach(LayerMask?, PixelRect)"/> while a mask stroke paints the mask: the stroke can reveal
    /// anything it covers.
    /// </summary>
    public static PixelRect? MaskReach(LayerMask? mask, PixelRect area, StrokeOverlay? maskStroke)
    {
        var reach = MaskReach(mask, area);
        if (reach is not { } r || maskStroke is null) return reach;
        return Union(r, maskStroke.Bounds.Intersect(area));
    }

    /// <summary>
    /// Bounding box of <paramref name="area"/> minus <paramref name="inner"/>, when that can be tighter than
    /// the whole area: the inner rectangle must span the full width or height, leaving one band.
    /// Returns null when the complement's bounding box is the whole area anyway.
    /// </summary>
    private static PixelRect? OutsideBounds(PixelRect inner, PixelRect area)
    {
        if (inner == area) return PixelRect.Empty;
        if (inner.IsEmpty) return null;
        if (inner.Left == area.Left && inner.Right == area.Right)
        {
            if (inner.Top == area.Top) return area with { Top = inner.Bottom };
            if (inner.Bottom == area.Bottom) return area with { Bottom = inner.Top };
        }
        if (inner.Top == area.Top && inner.Bottom == area.Bottom)
        {
            if (inner.Left == area.Left) return area with { Left = inner.Right };
            if (inner.Right == area.Right) return area with { Right = inner.Left };
        }
        return null;
    }

    private static PixelRect Union(PixelRect a, PixelRect b) =>
        a.IsEmpty ? b : b.IsEmpty ? a
        : new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    /// <summary>Where within <paramref name="area"/> a pixel layer's own pixels can show (transparency and mask considered).</summary>
    public static PixelRect Visible(PixelLayer layer, PixelRect area, StrokeOverlay? maskStroke = null)
    {
        if (layer.Pixels is not { } raster) return PixelRect.Empty;
        var bounds = layer.Bounds;
        if (raster.Alpha is { } alpha)
        {
            var nz = NonZeroBounds(alpha);
            bounds = nz.IsEmpty ? PixelRect.Empty
                : new PixelRect(bounds.Left + nz.Left, bounds.Top + nz.Top, bounds.Left + nz.Right, bounds.Top + nz.Bottom);
        }
        bounds = bounds.Intersect(area);
        if (MaskReach(layer.Mask, area, maskStroke) is { } reach) bounds = bounds.Intersect(reach);
        return bounds;
    }
}
