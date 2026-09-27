using Strayta.Core.Selection;

namespace Strayta.Core.Painting;

/// <summary>The Eyedropper's reading of an image: one pixel, or the average of a square around it.</summary>
public static class ColorSampler
{
    /// <summary>
    /// The straight (unpremultiplied) color of the <paramref name="size"/> × <paramref name="size"/> square centered
    /// on (<paramref name="x"/>, <paramref name="y"/>), clipped to the image; 1 samples just that pixel. Colors are
    /// averaged weighted by their opacity (the image is premultiplied), so transparent pixels do not pull the result
    /// toward black. Returns null when every sampled pixel is fully transparent, where Photoshop leaves the color alone.
    /// </summary>
    public static (byte R, byte G, byte B)? Average(SampleImage image, int x, int y, int size)
    {
        int half = Math.Max(0, size - 1) / 2;
        int x0 = Math.Max(0, x - half), x1 = Math.Min(image.Width, x + half + 1);
        int y0 = Math.Max(0, y - half), y1 = Math.Min(image.Height, y + half + 1);
        if (x0 >= x1 || y0 >= y1) return null;

        long r = 0, g = 0, b = 0, a = 0;
        var px = image.Rgba;
        for (int yy = y0; yy < y1; yy++)
        {
            int i = (yy * image.Width + x0) * 4;
            for (int xx = x0; xx < x1; xx++, i += 4)
            {
                r += px[i];
                g += px[i + 1];
                b += px[i + 2];
                a += px[i + 3];
            }
        }
        if (a == 0) return null;
        return (Unpremultiply(r, a), Unpremultiply(g, a), Unpremultiply(b, a));

        static byte Unpremultiply(long c, long a) => (byte)Math.Min(255, (c * 255 + a / 2) / a);
    }
}
