namespace Strayta.Rendering;

/// <summary>How closely a render matches a reference image (normally the file's embedded composite).</summary>
public sealed record FidelityReport(int Width, int Height, double MatchPercent, double MeanError, int MaxError, byte[] DiffRgba)
{
    /// <summary>Per-channel difference (0..255) at or below which a pixel counts as matching.</summary>
    public const int Tolerance = 3;

    /// <summary>
    /// Compares two straight-alpha RGBA8 images. Colors are compared premultiplied so that differences
    /// hidden by transparency do not count. The diff image shows mismatches in red over a dimmed reference.
    /// </summary>
    /// <param name="flattenOverWhite">
    /// Set when the reference has no transparency: files store such composites flattened onto white,
    /// so the render is flattened the same way before comparing.
    /// </param>
    public static FidelityReport Compare(byte[] rendered, byte[] reference, int width, int height,
        bool flattenOverWhite = false, bool includeDiff = true)
    {
        if (rendered.Length != reference.Length || rendered.Length != width * height * 4)
            throw new ArgumentException("Images must both be width × height RGBA8.");
        if (flattenOverWhite) rendered = FlattenOverWhite(rendered);

        var diff = includeDiff ? new byte[rendered.Length] : [];
        long matches = 0, sum = 0;
        int max = 0;
        var gate = new object();

        Parallel.For(0, height, () => (Matches: 0L, Sum: 0L, Max: 0), (y, _, acc) =>
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                int ra = rendered[o + 3], fa = reference[o + 3];
                int e = Math.Abs(ra - fa);
                for (int c = 0; c < 3; c++)
                    e = Math.Max(e, Math.Abs(rendered[o + c] * ra / 255 - reference[o + c] * fa / 255));

                acc.Sum += e;
                acc.Max = Math.Max(acc.Max, e);
                if (e <= Tolerance) acc.Matches++;

                if (includeDiff)
                {
                    byte gray = (byte)((reference[o] + reference[o + 1] + reference[o + 2]) / 3 * fa / 255 / 3 + 20);
                    diff[o] = e <= Tolerance ? gray : (byte)Math.Min(255, 128 + e * 2);
                    diff[o + 1] = e <= Tolerance ? gray : (byte)0;
                    diff[o + 2] = e <= Tolerance ? gray : (byte)0;
                    diff[o + 3] = 255;
                }
            }
            return acc;
        },
        acc =>
        {
            lock (gate)
            {
                matches += acc.Matches;
                sum += acc.Sum;
                max = Math.Max(max, acc.Max);
            }
        });

        long n = (long)width * height;
        return new FidelityReport(width, height, 100.0 * matches / n, (double)sum / n, max, diff);
    }

    public static byte[] FlattenOverWhite(byte[] rgba)
    {
        var o = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            for (int c = 0; c < 3; c++) o[i + c] = (byte)((rgba[i + c] * a + 255 * (255 - a) + 127) / 255);
            o[i + 3] = 255;
        }
        return o;
    }
}
