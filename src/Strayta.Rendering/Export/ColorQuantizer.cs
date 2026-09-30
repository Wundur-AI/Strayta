namespace Strayta.Rendering.Export;

/// <summary>An image reduced to at most 256 colors: palette entries (RGBA) and one palette index per pixel.</summary>
public sealed record IndexedImage(byte[] Palette, byte[] Indices, int Width, int Height)
{
    public int ColorCount => Palette.Length / 4;
}

/// <summary>
/// Median-cut color reduction (Heckbert's algorithm): the image's colors are counted in a histogram, the box of
/// colors with the most weighted spread is split at the median of its widest channel until there are as many boxes as
/// palette entries, and each box becomes the average of its colors. Pixels then take the nearest palette entry.
/// With <c>keepAlpha</c> alpha is a fourth channel (for 8-bit PNG); otherwise pixels below half opacity become one
/// fully transparent entry (GIF's single transparent index) and the rest count as opaque.
/// </summary>
public static class ColorQuantizer
{
    // Histogram resolution: 5 bits per color channel, 3 bits of alpha when alpha is kept.
    private const int ColorBits = 5, AlphaBits = 3;

    public static IndexedImage Quantize(byte[] rgba, int width, int height, int maxColors = 256, bool keepAlpha = false)
    {
        maxColors = Math.Clamp(maxColors, 2, 256);
        long pixels = (long)width * height;
        if (rgba.LongLength != pixels * 4) throw new ArgumentException("Pixel buffer does not match the size.", nameof(rgba));

        // An image with few distinct colors keeps them exactly.
        if (TryExact(rgba, pixels, maxColors, keepAlpha) is { } exact) return exact with { Width = width, Height = height };

        bool hasTransparent = false;
        int alphaLevels = keepAlpha ? 1 << AlphaBits : 1;
        int bins = (1 << (3 * ColorBits)) * alphaLevels;
        var count = new long[bins];
        var sum = new long[bins * 4];
        for (long i = 0; i < pixels; i++)
        {
            long p = i * 4;
            int a = rgba[p + 3];
            if (!keepAlpha && a < 128)
            {
                hasTransparent = true;
                continue;
            }
            int bin = Bin(rgba[p], rgba[p + 1], rgba[p + 2], keepAlpha ? a : 255, keepAlpha);
            count[bin]++;
            sum[bin * 4] += rgba[p];
            sum[bin * 4 + 1] += rgba[p + 1];
            sum[bin * 4 + 2] += rgba[p + 2];
            sum[bin * 4 + 3] += keepAlpha ? a : 255;
        }

        var used = new List<int>();
        for (int b = 0; b < bins; b++) if (count[b] > 0) used.Add(b);
        int target = maxColors - (hasTransparent ? 1 : 0);
        var boxes = new List<Box> { new(used.ToArray(), 0, used.Count) };
        var palette = new List<byte[]>();
        if (used.Count > 0)
        {
            while (boxes.Count < target)
            {
                // The box whose colors spread the most, weighted by how many pixels it holds, is split next.
                Box? worst = null;
                double worstScore = 0;
                foreach (var box in boxes)
                {
                    if (box.Length < 2) continue;
                    var (_, range) = box.WidestChannel(sum, count);
                    double score = range * (double)box.Weight(count);
                    if (score > worstScore) (worst, worstScore) = (box, score);
                }
                if (worst is null) break;
                boxes.Remove(worst);
                var (a, b) = worst.Split(sum, count);
                boxes.Add(a);
                boxes.Add(b);
            }
            foreach (var box in boxes) palette.Add(box.Average(sum, count));
        }
        if (hasTransparent) palette.Add([0, 0, 0, 0]);

        var flatPalette = palette.SelectMany(c => c).ToArray();
        return new IndexedImage(flatPalette, Map(rgba, pixels, flatPalette, keepAlpha, hasTransparent ? palette.Count - 1 : -1), width, height);
    }

    private static int Bin(int r, int g, int b, int a, bool keepAlpha)
    {
        int shift = 8 - ColorBits;
        int bin = ((r >> shift) << (2 * ColorBits)) | ((g >> shift) << ColorBits) | (b >> shift);
        return keepAlpha ? (bin << AlphaBits) | (a >> (8 - AlphaBits)) : bin;
    }

    /// <summary>The palette of an image with at most <paramref name="maxColors"/> distinct colors, or null.</summary>
    private static IndexedImage? TryExact(byte[] rgba, long pixels, int maxColors, bool keepAlpha)
    {
        var colors = new Dictionary<uint, byte>();
        var indices = new byte[pixels];
        for (long i = 0; i < pixels; i++)
        {
            long p = i * 4;
            byte a = rgba[p + 3];
            uint key = keepAlpha
                ? a == 0 ? 0u : (uint)(rgba[p] | rgba[p + 1] << 8 | rgba[p + 2] << 16 | a << 24)
                : a < 128 ? 0u : (uint)(rgba[p] | rgba[p + 1] << 8 | rgba[p + 2] << 16 | 255 << 24);
            if (!colors.TryGetValue(key, out byte index))
            {
                if (colors.Count == maxColors) return null;
                index = (byte)colors.Count;
                colors[key] = index;
            }
            indices[i] = index;
        }
        var palette = new byte[colors.Count * 4];
        foreach (var (key, index) in colors)
        {
            palette[index * 4] = (byte)key;
            palette[index * 4 + 1] = (byte)(key >> 8);
            palette[index * 4 + 2] = (byte)(key >> 16);
            palette[index * 4 + 3] = (byte)(key >> 24);
        }
        return new IndexedImage(palette, indices, 0, 0);
    }

    /// <summary>Each pixel's nearest palette entry, looked up once per histogram bin.</summary>
    private static byte[] Map(byte[] rgba, long pixels, byte[] palette, bool keepAlpha, int transparentIndex)
    {
        var indices = new byte[pixels];
        int colors = palette.Length / 4;
        int opaqueCount = transparentIndex >= 0 ? colors - 1 : colors;
        var cache = new int[(1 << (3 * ColorBits)) * (keepAlpha ? 1 << AlphaBits : 1)];
        Array.Fill(cache, -1);
        Parallel.For(0, (int)((pixels + 65535) / 65536), chunk =>
        {
            long end = Math.Min(pixels, (chunk + 1L) * 65536);
            for (long i = chunk * 65536L; i < end; i++)
            {
                long p = i * 4;
                int a = rgba[p + 3];
                if (!keepAlpha && a < 128)
                {
                    indices[i] = (byte)transparentIndex;
                    continue;
                }
                int r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
                if (!keepAlpha) a = 255;
                int bin = Bin(r, g, b, a, keepAlpha);
                int index = Volatile.Read(ref cache[bin]);
                if (index < 0)
                {
                    // Searched from the bin's center so every pixel of the bin gets the same entry.
                    int half = 1 << (8 - ColorBits - 1);
                    int cr = (r & ~((1 << (8 - ColorBits)) - 1)) + half, cg = (g & ~((1 << (8 - ColorBits)) - 1)) + half, cb = (b & ~((1 << (8 - ColorBits)) - 1)) + half;
                    int ca = keepAlpha ? (a & ~((1 << (8 - AlphaBits)) - 1)) + (1 << (8 - AlphaBits - 1)) : 255;
                    index = Nearest(palette, opaqueCount, cr, cg, cb, ca);
                    Volatile.Write(ref cache[bin], index);
                }
                indices[i] = (byte)index;
            }
        });
        return indices;
    }

    private static int Nearest(byte[] palette, int count, int r, int g, int b, int a)
    {
        int best = 0;
        long bestDistance = long.MaxValue;
        for (int k = 0; k < count; k++)
        {
            int dr = palette[k * 4] - r, dg = palette[k * 4 + 1] - g, db = palette[k * 4 + 2] - b, da = palette[k * 4 + 3] - a;
            // Weighted toward green, as the eye is most sensitive to it.
            long d = 2L * dr * dr + 4L * dg * dg + 3L * db * db + 3L * da * da;
            if (d < bestDistance) (best, bestDistance) = (k, d);
        }
        return best;
    }

    /// <summary>A run of histogram bins (a slice of one shared array) forming one box of colors.</summary>
    private sealed class Box(int[] bins, int start, int length)
    {
        public int Length => length;

        public long Weight(long[] count)
        {
            long w = 0;
            for (int i = start; i < start + length; i++) w += count[bins[i]];
            return w;
        }

        private static int Channel(int bin, long[] sum, long[] count, int c) => (int)(sum[bin * 4 + c] / count[bin]);

        public (int Channel, int Range) WidestChannel(long[] sum, long[] count)
        {
            Span<int> min = [255, 255, 255, 255], max = [0, 0, 0, 0];
            for (int i = start; i < start + length; i++)
                for (int c = 0; c < 4; c++)
                {
                    int v = Channel(bins[i], sum, count, c);
                    if (v < min[c]) min[c] = v;
                    if (v > max[c]) max[c] = v;
                }
            int best = 0;
            for (int c = 1; c < 4; c++) if (max[c] - min[c] > max[best] - min[best]) best = c;
            return (best, max[best] - min[best]);
        }

        public (Box, Box) Split(long[] sum, long[] count)
        {
            var (channel, _) = WidestChannel(sum, count);
            Array.Sort(bins, start, length, Comparer<int>.Create((x, y) => Channel(x, sum, count, channel).CompareTo(Channel(y, sum, count, channel))));
            long half = Weight(count) / 2, running = 0;
            int cut = start + 1;
            for (int i = start; i < start + length - 1; i++)
            {
                running += count[bins[i]];
                cut = i + 1;
                if (running >= half) break;
            }
            return (new Box(bins, start, cut - start), new Box(bins, cut, start + length - cut));
        }

        public byte[] Average(long[] sum, long[] count)
        {
            long n = Weight(count);
            var avg = new byte[4];
            for (int c = 0; c < 4; c++)
            {
                long s = 0;
                for (int i = start; i < start + length; i++) s += sum[bins[i] * 4 + c];
                avg[c] = (byte)Math.Clamp((s + n / 2) / Math.Max(1, n), 0, 255);
            }
            return avg;
        }
    }
}
