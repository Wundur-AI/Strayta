using Strayta.Core;

namespace Strayta.Segmentation;

/// <summary>
/// Moves a model's mask edge onto the edge actually visible in the image.
/// </summary>
/// <remarks>
/// A SAM mask is 256 cells across whatever it covers, so on a 4000-pixel photo each cell is 16 pixels and the
/// interpolated edge can be several pixels off, leaving a rim of background or cutting into the object. Near the
/// edge (a band about one cell wide) this estimates the local object color F from confidently selected pixels
/// and the local background color B from confidently unselected ones (block averages, so it costs two passes),
/// then re-decides each band pixel that clearly has one of the two colors, or lies on the line between them (an
/// anti-aliased edge pixel, α = (I − B)·(F − B) / |F − B|²). Anything else, like the darker rim of a shaded
/// object, keeps the model's answer: a color model that cannot explain a pixel should not overrule the network.
/// Where F and B are hard to tell apart the model's edge is kept too, and the correction fades out toward the
/// band's rim so the refined edge never jumps.
/// </remarks>
internal static class EdgeRefiner
{
    private const int Channels = 8; // inside R, G, B, count; outside R, G, B, count

    public static void Refine(MaskLogits mask, RgbaImage image, PixelRect region, byte[] coverage, float[] distance)
    {
        var ip = image.Placement;
        var area = region.Intersect(ip);
        if (area != region) return; // the guide must cover the region; masks always come from their own image
        var mp = mask.Placement;
        double cell = Math.Max((double)mp.Width / mask.Width, (double)mp.Height / mask.Height);
        // The model's edge can be off by up to about a cell, so that is how far it may move.
        float band = (float)Math.Max(2.0, cell);
        int bs = Math.Max(2, (int)MathF.Round(band / 2));
        int w = region.Width, h = region.Height;
        int gw = (w + bs - 1) / bs, gh = (h + bs - 1) / bs;
        var px = image.Pixels;

        // 1. Block sums of confidently inside / outside colors.
        var sums = new float[gw * gh * Channels];
        Parallel.For(0, gh, by =>
        {
            int y0 = region.Top + by * bs, y1 = Math.Min(region.Bottom, y0 + bs);
            for (int y = y0; y < y1; y++)
                for (int x = region.Left; x < region.Right; x++)
                {
                    float d = distance[(y - region.Top) * w + x - region.Left];
                    if (float.IsNaN(d) || MathF.Abs(d) <= band) continue;
                    var (r, g, b) = Color(px, image, x, y);
                    int o = ((by * gw) + (x - region.Left) / bs) * Channels + (d > 0 ? 0 : 4);
                    sums[o] += r;
                    sums[o + 1] += g;
                    sums[o + 2] += b;
                    sums[o + 3] += 1;
                }
        });

        // 2. Widen each block's view to ±3 blocks (about 1.75 bands), so the edge band always sees both sides.
        var local = BoxBlur(BoxBlur(sums, gw, gh, 3, horizontal: true), gw, gh, 3, horizontal: false);

        // 3. Re-decide the pixels in the band.
        var refined = new bool[w * h];
        Parallel.For(region.Top, region.Bottom, y =>
        {
            Span<float> f = stackalloc float[Channels];
            int row = (y - region.Top) * w - region.Left;
            double gy = (y - region.Top + 0.5) / bs - 0.5;
            for (int x = region.Left; x < region.Right; x++)
            {
                float d = distance[row + x];
                if (!(MathF.Abs(d) <= band)) continue;
                Sample(local, gw, gh, (x - region.Left + 0.5) / bs - 0.5, gy, f);
                if (f[3] < 1 || f[7] < 1) continue;
                float fr = f[0] / f[3], fg = f[1] / f[3], fb = f[2] / f[3];
                float br = f[4] / f[7], bg = f[5] / f[7], bb = f[6] / f[7];
                float dr = fr - br, dg = fg - bg, db = fb - bb;
                float sep2 = dr * dr + dg * dg + db * db;
                // Colors closer than ~16 levels are indistinguishable in practice; beyond ~48 fully trusted.
                float trust = Math.Clamp((MathF.Sqrt(sep2) - 16f) / 32f, 0f, 1f);
                if (trust <= 0f) continue;
                var (r, g, b) = Color(px, image, x, y);
                float ir = r - br, ig = g - bg, ib = b - bb;
                float jr = r - fr, jg = g - fg, jb = b - fb;
                float toB = ir * ir + ig * ig + ib * ib, toF = jr * jr + jg * jg + jb * jb;
                float alpha = (ir * dr + ig * dg + ib * db) / sep2;
                float target;
                if (toF * 4 < toB) target = 1f;       // clearly the object's color
                else if (toB * 4 < toF) target = 0f;  // clearly the background's
                else if (alpha is > 0f and < 1f && toB - alpha * alpha * sep2 < sep2 / 16)
                    target = alpha;                   // on the line between them: a mixed edge pixel
                else continue;                        // neither (shading, texture): keep the model's answer
                // Fade back to the model's edge over the outer 40% of the band.
                trust *= Math.Clamp((1f - MathF.Abs(d) / band) / 0.4f, 0f, 1f);
                float model = coverage[row + x] / 255f;
                // Steepen once more so a half-trusted blend reads as in or out; only the edge itself stays partial.
                float c = model + (target - model) * trust;
                coverage[row + x] = MaskUpscaler.ToByte((c - 0.5f) * 2f + 0.5f);
                refined[row + x] = true;
            }
        });

        // 4. Per-pixel decisions are noisy on texture; a 3×3 average (steepened again) over the re-decided pixels
        //    removes isolated specks and leaves a smooth one-pixel anti-aliased edge.
        var decided = (byte[])coverage.Clone();
        Parallel.For(1, h - 1, ly =>
        {
            for (int lx = 1; lx < w - 1; lx++)
            {
                int i = ly * w + lx;
                if (!refined[i]) continue;
                int sum = decided[i - w - 1] + decided[i - w] + decided[i - w + 1] + decided[i - 1] + decided[i] + decided[i + 1]
                        + decided[i + w - 1] + decided[i + w] + decided[i + w + 1];
                coverage[i] = MaskUpscaler.ToByte((sum / (9f * 255f) - 0.5f) * 2f + 0.5f);
            }
        });
    }

    /// <summary>The pixel's color as the models saw it: composited over the preprocessing matte.</summary>
    private static (float R, float G, float B) Color(byte[] px, RgbaImage image, int x, int y)
    {
        long i = ((long)(y - image.Placement.Top) * image.Width + (x - image.Placement.Left)) * 4;
        float a = px[i + 3] * (1f / 255f), m = ImagePreprocessor.Matte * (1f - a);
        return (px[i] * a + m, px[i + 1] * a + m, px[i + 2] * a + m);
    }

    private static float[] BoxBlur(float[] src, int gw, int gh, int radius, bool horizontal)
    {
        var dst = new float[src.Length];
        Parallel.For(0, horizontal ? gh : gw, line =>
        {
            int n = horizontal ? gw : gh;
            for (int i = 0; i < n; i++)
            {
                int lo = Math.Max(0, i - radius), hi = Math.Min(n - 1, i + radius);
                int to = (horizontal ? line * gw + i : i * gw + line) * Channels;
                for (int k = lo; k <= hi; k++)
                {
                    int from = (horizontal ? line * gw + k : k * gw + line) * Channels;
                    for (int c = 0; c < Channels; c++) dst[to + c] += src[from + c];
                }
            }
        });
        return dst;
    }

    /// <summary>Bilinear interpolation of all channels between block centers.</summary>
    private static void Sample(float[] grid, int gw, int gh, double u, double v, Span<float> result)
    {
        u = Math.Clamp(u, 0, gw - 1);
        v = Math.Clamp(v, 0, gh - 1);
        int x0 = (int)u, y0 = (int)v, x1 = Math.Min(x0 + 1, gw - 1), y1 = Math.Min(y0 + 1, gh - 1);
        float fx = (float)(u - x0), fy = (float)(v - y0);
        int a = (y0 * gw + x0) * Channels, b = (y0 * gw + x1) * Channels, c = (y1 * gw + x0) * Channels, d = (y1 * gw + x1) * Channels;
        for (int k = 0; k < Channels; k++)
        {
            float top = grid[a + k] + (grid[b + k] - grid[a + k]) * fx;
            float bottom = grid[c + k] + (grid[d + k] - grid[c + k]) * fx;
            result[k] = top + (bottom - top) * fy;
        }
    }
}
