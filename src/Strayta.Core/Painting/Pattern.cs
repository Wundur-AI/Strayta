namespace Strayta.Core.Painting;

/// <summary>
/// A pattern tile, as Photoshop's pattern fills, Pattern Overlay and the Paint Bucket tile it. <see cref="Id"/> is the
/// identifier layer data refers to (a GUID string in Photoshop files); <see cref="Pixels"/> holds the tile in RGB or
/// grayscale, with transparency when the pattern has any.
/// </summary>
public sealed class Pattern
{
    public Pattern(string id, string name, Raster pixels)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (pixels.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale))
            throw new NotSupportedException($"{pixels.ColorMode} patterns are not supported.");
        Id = id;
        Name = name;
        Pixels = pixels;
    }

    public string Id { get; }
    public string Name { get; }
    public Raster Pixels { get; }
    public int Width => Pixels.Width;
    public int Height => Pixels.Height;

    /// <summary>A new pattern ID in Photoshop's form (a lowercase GUID).</summary>
    public static string NewId() => Guid.NewGuid().ToString("D");

    /// <summary>The tile's color at document pixel (<paramref name="x"/>, <paramref name="y"/>): tiles start at the document's origin.</summary>
    public (float R, float G, float B, float A) At(int x, int y)
    {
        int px = Mod(x, Width), py = Mod(y, Height), i = py * Width + px;
        float a = Pixels.Alpha?.GetNormalized(i) ?? 1f;
        if (Pixels.ColorMode == ColorMode.Grayscale)
        {
            float g = Pixels.ColorPlanes[0].GetNormalized(i);
            return (g, g, g, a);
        }
        return (Pixels.ColorPlanes[0].GetNormalized(i), Pixels.ColorPlanes[1].GetNormalized(i), Pixels.ColorPlanes[2].GetNormalized(i), a);
    }

    private static int Mod(int v, int m)
    {
        int r = v % m;
        return r < 0 ? r + m : r;
    }

    /// <summary>A pattern made from pixels read out of a layer (8-bit RGBA, row-major).</summary>
    public static Pattern FromRgba(string id, string name, int width, int height, ReadOnlySpan<byte> rgba, bool withAlpha)
    {
        var planes = Enumerable.Range(0, withAlpha ? 4 : 3).Select(_ => Plane.Create(width, height, 8)).ToArray();
        for (int i = 0, n = width * height; i < n; i++)
            for (int c = 0; c < planes.Length; c++) planes[c].Data[i] = rgba[i * 4 + c];
        return new Pattern(id, name, new Raster(ColorMode.Rgb, planes[..3], withAlpha ? planes[3] : null));
    }
}

/// <summary>Strayta's built-in patterns: small, generated tiles for fills and textures (not copies of Photoshop's).</summary>
public static class PatternLibrary
{
    private static readonly Lazy<IReadOnlyList<Pattern>> Lazy = new(Build);

    /// <summary>The built-in set, in the order the pickers show it.</summary>
    public static IReadOnlyList<Pattern> BuiltIn => Lazy.Value;

    private static IReadOnlyList<Pattern> Build() =>
    [
        Generate("5a3e0a51-6f0b-4c8e-9b3a-0d1c2e3f4a01", "Checkerboard", 16, 16, (x, y) =>
        {
            float v = (x / 8 + y / 8) % 2 == 0 ? 0.8f : 1f;
            return (v, v, v, 1f);
        }),
        Generate("5a3e0a51-6f0b-4c8e-9b3a-0d1c2e3f4a02", "Grid", 16, 16, (x, y) =>
            x == 0 || y == 0 ? (0.62f, 0.66f, 0.72f, 1f) : (1f, 1f, 1f, 1f)),
        Generate("5a3e0a51-6f0b-4c8e-9b3a-0d1c2e3f4a03", "Diagonal Stripes", 16, 16, (x, y) =>
            ((x + y) % 16 < 5) ? (0.15f, 0.15f, 0.15f, 1f) : (0.95f, 0.95f, 0.95f, 1f)),
        Generate("5a3e0a51-6f0b-4c8e-9b3a-0d1c2e3f4a04", "Dots", 12, 12, (x, y) =>
        {
            float dx = x + 0.5f - 6f, dy = y + 0.5f - 6f, d = MathF.Sqrt(dx * dx + dy * dy);
            float ink = Math.Clamp(3.5f - d, 0f, 1f);
            return (1f - ink * 0.85f, 1f - ink * 0.85f, 1f - ink * 0.85f, 1f);
        }),
        Generate("5a3e0a51-6f0b-4c8e-9b3a-0d1c2e3f4a05", "Bricks", 32, 16, (x, y) =>
        {
            int row = y / 8, off = row % 2 == 0 ? 0 : 8;
            bool mortar = y % 8 == 7 || (x + off) % 16 == 15;
            float n = Hash(x, y) * 0.08f;
            return mortar ? (0.82f, 0.8f, 0.76f, 1f) : (0.62f + n, 0.28f + n * 0.5f, 0.2f + n * 0.5f, 1f);
        }),
        Generate("5a3e0a51-6f0b-4c8e-9b3a-0d1c2e3f4a06", "Paper Grain", 64, 64, (x, y) =>
        {
            float v = 0.9f + (Hash(x, y) - 0.5f) * 0.12f + (Hash(x / 3, y / 3 + 97) - 0.5f) * 0.06f;
            return (v, v * 0.985f, v * 0.95f, 1f);
        }),
        Generate("5a3e0a51-6f0b-4c8e-9b3a-0d1c2e3f4a07", "Canvas Weave", 8, 8, (x, y) =>
        {
            bool warp = (x / 2 + y / 2) % 2 == 0;
            float v = warp ? 0.86f - (y % 2) * 0.05f : 0.8f - (x % 2) * 0.05f;
            return (v, v * 0.97f, v * 0.9f, 1f);
        }),
        Generate("5a3e0a51-6f0b-4c8e-9b3a-0d1c2e3f4a08", "Halftone Lines", 8, 8, (x, y) =>
            y % 8 < 2 ? (0f, 0f, 0f, 1f) : (0f, 0f, 0f, 0f)),
    ];

    private static Pattern Generate(string id, string name, int w, int h, Func<int, int, (float R, float G, float B, float A)> pixel)
    {
        var rgba = new byte[w * h * 4];
        bool alpha = false;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                int i = (y * w + x) * 4;
                rgba[i] = ToByte(r);
                rgba[i + 1] = ToByte(g);
                rgba[i + 2] = ToByte(b);
                rgba[i + 3] = ToByte(a);
                alpha |= rgba[i + 3] != 255;
            }
        return Pattern.FromRgba(id, name, w, h, rgba, alpha);
    }

    private static byte ToByte(float v) => (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);

    private static float Hash(int x, int y)
    {
        uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        return (h >> 8) * (1f / (1 << 24));
    }
}
