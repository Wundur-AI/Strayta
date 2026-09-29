using Avalonia.Media.Imaging;
using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Editor.Controls;

/// <summary>
/// Pictures of gradients and patterns for swatches, preset grids and the Gradient Editor's bar: drawn with the same
/// evaluator the tool paints with, transparency shown over a light checkerboard, as Photoshop does.
/// </summary>
public static class GradientImages
{
    /// <summary>A horizontal strip of <paramref name="gradient"/> (stops already resolved), <paramref name="width"/>×<paramref name="height"/> pixels.</summary>
    public static WriteableBitmap Strip(Gradient gradient, int width, int height, GradientMethod method = GradientMethod.Perceptual, bool reverse = false)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var lut = GradientLut.Build(gradient, method);
        var rgba = new byte[width * height * 4];
        for (int x = 0; x < width; x++)
        {
            float t = width == 1 ? 0 : x / (float)(width - 1);
            var (r, g, b, a) = lut.At(reverse ? 1 - t : t);
            for (int y = 0; y < height; y++)
                Put(rgba, (y * width + x) * 4, r, g, b, a, Checker(x, y));
        }
        return BitmapFactory.FromRgba(rgba, width, height);
    }

    /// <summary>A pattern thumbnail: the tile repeated (or scaled down when larger) into <paramref name="size"/>×<paramref name="size"/>.</summary>
    public static WriteableBitmap Thumbnail(Pattern pattern, int size)
    {
        var rgba = new byte[size * size * 4];
        float scale = Math.Max(1f, Math.Max(pattern.Width, pattern.Height) / (float)size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var (r, g, b, a) = pattern.At((int)(x * scale), (int)(y * scale));
                Put(rgba, (y * size + x) * 4, r, g, b, a, Checker(x, y));
            }
        return BitmapFactory.FromRgba(rgba, size, size);
    }

    private static float Checker(int x, int y) => ((x / 4 + y / 4) & 1) == 0 ? 1f : 0.8f;

    private static void Put(byte[] rgba, int i, float r, float g, float b, float a, float checker)
    {
        rgba[i] = B(r * a + checker * (1 - a));
        rgba[i + 1] = B(g * a + checker * (1 - a));
        rgba[i + 2] = B(b * a + checker * (1 - a));
        rgba[i + 3] = 255;
    }

    private static byte B(float v) => (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
}
