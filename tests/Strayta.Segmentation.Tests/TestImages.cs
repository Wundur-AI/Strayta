using Strayta.Core;
using Strayta.Core.Selection;

namespace Strayta.Segmentation.Tests;

internal static class TestImages
{
    /// <summary>A filled disc (anti-aliasing ignored) on a plain background.</summary>
    public static RgbaImage Disc(int w, int h, float cx, float cy, float r, (byte R, byte G, byte B) disc, (byte R, byte G, byte B) background,
        int left = 0, int top = 0)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool inside = InDisc(x, y, cx, cy, r);
                var c = inside ? disc : background;
                int i = (y * w + x) * 4;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = (c.R, c.G, c.B, 255);
            }
        return new RgbaImage(px, w, h, new PixelRect(left, top, left + w, top + h));
    }

    public static bool InDisc(int x, int y, float cx, float cy, float r)
    {
        float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
        return dx * dx + dy * dy <= r * r;
    }

    /// <summary>Intersection over union of a selection (coverage ≥ 50%) and a reference shape, over <paramref name="area"/>.</summary>
    public static double Iou(SelectionMask? mask, PixelRect area, Func<int, int, bool> reference)
    {
        long both = 0, either = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                bool a = mask is not null && mask.CoverageAt(x, y) >= 128, b = reference(x, y);
                if (a && b) both++;
                if (a || b) either++;
            }
        return either == 0 ? 1 : (double)both / either;
    }
}
