namespace Strayta.Core.Selection;

/// <summary>
/// Select and Mask's Decontaminate Colors: replaces the colors of partly selected pixels (the fringe, where the old
/// background still shows through hair or a soft edge) with the colors of fully selected pixels nearby, so the cut-out
/// does not carry a halo of its old background onto a new one.
/// </summary>
/// <remarks>
/// Fully selected pixels (coverage ≥ 98%) are the known foreground. For every other pixel, the mean color of the
/// known foreground in a window around it is the replacement; windows of 16, 64 and 256 pixels are tried in turn, so
/// thin strands far from the body still find a color. The means come from block sums box-filtered over the window,
/// so the cost does not depend on the window. The replacement weighs by how transparent the pixel is: at 50% coverage
/// or less the pixel takes the foreground color entirely (times the amount), at full coverage it keeps its own.
/// </remarks>
public static class ColorDecontamination
{
    private const int Block = 8;
    private const byte Known = 250;

    /// <summary>
    /// Decontaminated straight RGBA (alpha unchanged) for <paramref name="rgba"/> (straight RGBA, <paramref name="w"/> ×
    /// <paramref name="h"/>) under <paramref name="coverage"/> (one byte per pixel, 255 = selected).
    /// <paramref name="amount"/> is 0..1 (Photoshop's Amount / 100).
    /// </summary>
    public static byte[] Apply(byte[] rgba, int w, int h, byte[] coverage, float amount, CancellationToken cancel = default)
    {
        if (rgba.LongLength != (long)w * h * 4) throw new ArgumentException("Expected 4 bytes per pixel.", nameof(rgba));
        if (coverage.LongLength != (long)w * h) throw new ArgumentException("Expected one byte per pixel.", nameof(coverage));
        var result = (byte[])rgba.Clone();
        amount = Math.Clamp(amount, 0f, 1f);
        if (amount <= 0f) return result;

        // 1. Sums of the known foreground's colors per block: R, G, B (weighted by the pixel's own alpha) and weight.
        int bw = (w + Block - 1) / Block, bh = (h + Block - 1) / Block;
        var blocks = new float[bw * bh * 4];
        Parallel.For(0, bh, by =>
        {
            for (int y = by * Block, y1 = Math.Min(y + Block, h); y < y1; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (coverage[i] < Known) continue;
                    int p = i * 4;
                    float a = rgba[p + 3];
                    if (a <= 0) continue;
                    int o = (by * bw + x / Block) * 4;
                    blocks[o] += rgba[p] * a;
                    blocks[o + 1] += rgba[p + 1] * a;
                    blocks[o + 2] += rgba[p + 2] * a;
                    blocks[o + 3] += a;
                }
        });
        cancel.ThrowIfCancellationRequested();
        var scales = new[] { BoxSum(blocks, bw, bh, 1), BoxSum(blocks, bw, bh, 4), BoxSum(blocks, bw, bh, 16) };
        cancel.ThrowIfCancellationRequested();

        // 2. Each fringe pixel toward its local foreground color.
        Parallel.For(0, h, y =>
        {
            int by = Math.Min(y / Block, bh - 1);
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int m = coverage[i];
                if (m >= Known) continue;
                float weight = amount * Math.Min(1f, (255 - m) / 127.5f);
                int o = (by * bw + Math.Min(x / Block, bw - 1)) * 4;
                foreach (var sums in scales)
                {
                    float n = sums[o + 3];
                    if (n <= 0) continue;
                    int p = i * 4;
                    for (int c = 0; c < 3; c++)
                        result[p + c] = MaskFilters.ClampByte(rgba[p + c] + (sums[o + c] / n - rgba[p + c]) * weight);
                    break;
                }
            }
        });
        return result;
    }

    /// <summary>Box sums of the block grid over (2r+1)² blocks, as running sums along rows then columns.</summary>
    private static float[] BoxSum(float[] blocks, int bw, int bh, int r)
    {
        var rows = new float[blocks.Length];
        Parallel.For(0, bh, by =>
        {
            Span<float> sum = stackalloc float[4];
            int row = by * bw;
            for (int bx = 0; bx <= Math.Min(r, bw - 1); bx++)
                for (int c = 0; c < 4; c++) sum[c] += blocks[(row + bx) * 4 + c];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int c = 0; c < 4; c++) rows[(row + bx) * 4 + c] = sum[c];
                if (bx + r + 1 < bw) for (int c = 0; c < 4; c++) sum[c] += blocks[(row + bx + r + 1) * 4 + c];
                if (bx - r >= 0) for (int c = 0; c < 4; c++) sum[c] -= blocks[(row + bx - r) * 4 + c];
            }
        });
        var sums = new float[blocks.Length];
        Parallel.For(0, bw, bx =>
        {
            Span<float> sum = stackalloc float[4];
            for (int by = 0; by <= Math.Min(r, bh - 1); by++)
                for (int c = 0; c < 4; c++) sum[c] += rows[(by * bw + bx) * 4 + c];
            for (int by = 0; by < bh; by++)
            {
                for (int c = 0; c < 4; c++) sums[(by * bw + bx) * 4 + c] = sum[c];
                if (by + r + 1 < bh) for (int c = 0; c < 4; c++) sum[c] += rows[((by + r + 1) * bw + bx) * 4 + c];
                if (by - r >= 0) for (int c = 0; c < 4; c++) sum[c] -= rows[((by - r) * bw + bx) * 4 + c];
            }
        });
        return sums;
    }
}
