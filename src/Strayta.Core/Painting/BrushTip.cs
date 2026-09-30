namespace Strayta.Core.Painting;

/// <summary>
/// A sampled brush tip: a grayscale image whose values are how much paint each point of the tip lays down (255 full,
/// 0 none), as captured by Define Brush Preset or read from a brush file (.abr). Dabs draw it scaled so its longer side
/// is the brush size, turned by the brush angle and squashed by its roundness (<see cref="PaintStroke"/>).
/// </summary>
/// <remarks>
/// Scaling a large tip down to a small brush by point sampling would alias badly, so the tip keeps a chain of box-filtered
/// half-size copies (a mip chain) and each dab samples the level closest to its scale, bilinearly.
/// </remarks>
public sealed class BrushTip
{
    private readonly Level[] _levels;

    /// <param name="alpha">Row-major tip values, <paramref name="width"/> × <paramref name="height"/>; 255 paints fully.</param>
    public BrushTip(string name, int width, int height, byte[] alpha)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "A tip needs pixels.");
        if (alpha.Length != width * height) throw new ArgumentException("Tip data does not match its size.", nameof(alpha));
        Name = name;
        Width = width;
        Height = height;
        Alpha = alpha;

        var levels = new List<Level>();
        var first = new float[width * height];
        for (int i = 0; i < first.Length; i++) first[i] = alpha[i] * (1f / 255f);
        levels.Add(new Level(width, height, first));
        while (levels[^1].Width > 2 || levels[^1].Height > 2)
        {
            var prev = levels[^1];
            int w = Math.Max(1, (prev.Width + 1) / 2), h = Math.Max(1, (prev.Height + 1) / 2);
            var next = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    int n = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = x * 2 + dx, sy = y * 2 + dy;
                            if (sx >= prev.Width || sy >= prev.Height) continue;
                            sum += prev.Values[sy * prev.Width + sx];
                            n++;
                        }
                    next[y * w + x] = sum / n;
                }
            levels.Add(new Level(w, h, next));
        }
        _levels = [.. levels];
    }

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>The tip values as given (255 paints fully).</summary>
    public byte[] Alpha { get; }

    /// <summary>The longer side in pixels: a tip drawn at this brush size is drawn 1:1.</summary>
    public int NativeSize => Math.Max(Width, Height);

    /// <summary>
    /// The mip level to sample for a tip drawn at <paramref name="scale"/> (drawn pixels per tip pixel), as an index for
    /// <see cref="Sample"/>.
    /// </summary>
    public int LevelFor(float scale)
    {
        int level = 0;
        while (scale < 0.75f && level < _levels.Length - 1)
        {
            scale *= 2f;
            level++;
        }
        return level;
    }

    /// <summary>
    /// The tip's value (0..1) at tip coordinates (<paramref name="u"/>, <paramref name="v"/>) in full-size tip pixels,
    /// (0, 0) being the tip's top-left corner, read bilinearly from mip <paramref name="level"/>; 0 outside the tip.
    /// </summary>
    public float Sample(int level, float u, float v)
    {
        var l = _levels[level];
        float s = 1f / (1 << level); // level pixel i covers full-size pixels [2^level·i, 2^level·(i+1))
        float fx = u * s - 0.5f, fy = v * s - 0.5f;
        if (fx <= -1f || fy <= -1f || fx >= l.Width || fy >= l.Height) return 0f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        float a = l.At(x0, y0), b = l.At(x0 + 1, y0), c = l.At(x0, y0 + 1), d = l.At(x0 + 1, y0 + 1);
        return (a + (b - a) * tx) * (1f - ty) + (c + (d - c) * tx) * ty;
    }

    /// <summary>
    /// A computed round tip rendered as an image (for thumbnails and "Define Brush" of a round preset): diameter
    /// <paramref name="size"/> with the brush engine's hardness falloff.
    /// </summary>
    public static BrushTip Round(string name, int size, float hardness)
    {
        size = Math.Clamp(size, 1, 2500);
        var alpha = new byte[size * size];
        float r = size / 2f, inner = r * Math.Clamp(hardness, 0f, 1f);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = MathF.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                float a = hardness >= 0.99f ? Math.Clamp(r - d + 0.5f, 0f, 1f)
                    : d <= inner ? 1f : d >= r ? 0f : 1f - Smooth((d - inner) / (r - inner));
                alpha[y * size + x] = (byte)MathF.Round(a * 255f);
            }
        return new BrushTip(name, size, size, alpha);

        static float Smooth(float t) => t * t * (3f - 2f * t);
    }

    private sealed record Level(int Width, int Height, float[] Values)
    {
        public float At(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height ? 0f : Values[y * Width + x];
    }
}
