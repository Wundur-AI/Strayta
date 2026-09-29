using Strayta.Core.Painting;

namespace Strayta.Core.Tests;

/// <summary>
/// Content-Aware synthesis of holes wider than the structure around them, at the canvas edge (the Crop tool's
/// Content-Aware option, Content-Aware Fill near a border), at a corner and inside the image: structure crossing the
/// hole's known edge must be continued, not averaged into grey.
/// </summary>
public class ContentAwareEdgeTests(ITestOutputHelper output)
{
    private const float Light = 200 / 255f, Dark = 40 / 255f;

    /// <summary>Straight RGB + alpha: bands <paramref name="band"/> pixels wide alternating 200/40, horizontal or vertical.</summary>
    private static float[] Bands(int w, int h, int band, bool horizontal)
    {
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = ((horizontal ? y : x) / band) % 2 == 0 ? Light : Dark;
                for (int k = 0; k < 3; k++) px[(y * w + x) * 4 + k] = v;
                px[(y * w + x) * 4 + 3] = 1;
            }
        return px;
    }

    /// <summary>Content-Aware Fill as Edit › Content-Aware Fill and the crop hook run it: synthesis with the default options, then the heal.</summary>
    private static PixelSource Fill(float[] truth, int w, int h, PixelRect hole, float blemish = 1f)
    {
        var canvas = PixelRect.FromSize(w, h);
        var px = (float[])truth.Clone();
        for (int y = hole.Top; y < hole.Bottom; y++)
            for (int x = hole.Left; x < hole.Right; x++)
                for (int k = 0; k < 3; k++) px[(y * w + x) * 4 + k] = blemish;
        var image = PixelSource.FromFloats(px, 3, canvas);
        var cov = Enumerable.Repeat(1f, hole.Width * hole.Height).ToArray();
        var texture = PatchSynthesis.Synthesize(image, cov, hole, canvas, new SynthesisOptions())!;
        return Healing.Heal(image, texture, 0, 0, cov, hole, canvas);
    }

    /// <summary>
    /// Mean absolute error in levels along each band's center line, from 5 px inside the hole's near edge to 5 px inside
    /// its far one; one value per band crossing the hole.
    /// </summary>
    private static double[] BandErrors(PixelSource result, float[] truth, int w, PixelRect hole, int band, bool horizontal)
    {
        Span<float> c = stackalloc float[3];
        var errors = new List<double>();
        int across0 = horizontal ? hole.Top : hole.Left, across1 = horizontal ? hole.Bottom : hole.Right;
        int along0 = (horizontal ? hole.Left : hole.Top) + 5, along1 = (horizontal ? hole.Right : hole.Bottom) - 5;
        for (int b = across0 / band; b * band < across1; b++)
        {
            int centre = b * band + band / 2;
            if (centre < across0 || centre >= across1) continue;
            double sum = 0;
            int n = 0;
            for (int t = along0; t < along1; t++)
            {
                int x = horizontal ? t : centre, y = horizontal ? centre : t;
                result.Read(x, y, c);
                sum += Math.Abs(c[0] - truth[(y * w + x) * 4]) * 255;
                n++;
            }
            errors.Add(sum / n);
        }
        return [.. errors];
    }

    [Theory]
    [InlineData("right edge", 200, 80, 10, true, 150, 0, 200, 80)]
    [InlineData("right edge", 200, 80, 20, true, 150, 0, 200, 80)]
    [InlineData("right edge", 200, 80, 10, true, 180, 0, 200, 80)]
    [InlineData("right edge, wider", 240, 80, 10, true, 160, 0, 240, 80)]
    [InlineData("left edge", 200, 80, 20, true, 0, 0, 50, 80)]
    [InlineData("top edge", 80, 200, 10, false, 0, 0, 80, 50)]
    [InlineData("top edge", 80, 200, 20, false, 0, 0, 80, 50)]
    [InlineData("top edge", 200, 120, 10, false, 0, 0, 200, 50)]
    [InlineData("top edge", 200, 120, 20, false, 0, 0, 200, 50)]
    [InlineData("bottom edge", 200, 120, 20, false, 0, 70, 200, 120)]
    [InlineData("top-right corner", 200, 120, 10, true, 140, 0, 200, 60)]
    [InlineData("top-right corner", 200, 120, 20, false, 140, 0, 200, 60)]
    [InlineData("bottom-left corner", 200, 120, 20, true, 0, 60, 60, 120)]
    [InlineData("interior", 200, 120, 10, true, 75, 30, 125, 90)]
    [InlineData("interior", 200, 120, 20, true, 75, 30, 125, 90)]
    [InlineData("interior", 200, 120, 20, false, 75, 30, 125, 90)]
    [InlineData("full-height strip", 200, 80, 20, true, 75, 0, 125, 80)]
    public void Bands_continue_through_the_hole(string where, int w, int h, int band, bool horizontal, int l, int t, int r, int b)
    {
        var truth = Bands(w, h, band, horizontal);
        var hole = new PixelRect(l, t, r, b);
        var result = Fill(truth, w, h, hole);
        var errors = BandErrors(result, truth, w, hole, band, horizontal);
        double mean = errors.Average();
        output.WriteLine($"{where}, {(horizontal ? "horizontal" : "vertical")} bands {band}, hole {hole.Width}×{hole.Height}: mean error {mean:F1} levels, per band {string.Join(" ", errors.Select(e => e.ToString("F0")))}");
        Assert.True(mean < 5, $"{where}: mean error {mean:F1} levels ({string.Join(" ", errors.Select(e => e.ToString("F0")))})");
        Assert.True(errors.Max() < 20, $"{where}: worst band {errors.Max():F1} levels");
    }

    /// <summary>A natural-looking image: two textured regions with a light gradient, split by a diagonal edge.</summary>
    private static float[] Natural(int w, int h)
    {
        var rng = new Random(11);
        const int cell = 6;
        int gw = w / cell + 2, gh = h / cell + 2;
        var grid = Enumerable.Range(0, gw * gh).Select(_ => (float)rng.NextDouble()).ToArray();
        var fine = Enumerable.Range(0, w * h).Select(_ => (float)rng.NextDouble() - 0.5f).ToArray();
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // Value noise (bilinear over a 6 px grid) plus a little per-pixel grain.
                float u = x / (float)cell, v = y / (float)cell;
                int x0 = (int)u, y0 = (int)v;
                float fx = u - x0, fy = v - y0;
                float n = (grid[y0 * gw + x0] * (1 - fx) + grid[y0 * gw + x0 + 1] * fx) * (1 - fy)
                          + (grid[(y0 + 1) * gw + x0] * (1 - fx) + grid[(y0 + 1) * gw + x0 + 1] * fx) * fy;
                bool below = y > 0.45f * x + 40;
                float baseValue = below ? 0.28f : 0.66f;
                for (int k = 0; k < 3; k++)
                {
                    float tint = below ? 0.04f * (k - 1) : -0.03f * (k - 1);
                    px[(y * w + x) * 4 + k] = Math.Clamp(baseValue + tint + 0.12f * (n - 0.5f) + 0.03f * fine[y * w + x] + 0.0005f * x - 0.0003f * y, 0f, 1f);
                }
                px[(y * w + x) * 4 + 3] = 1;
            }
        return px;
    }

    /// <summary>RMSE over the hole, of the image itself and of a 5×5 box blur of it (structure, ignoring grain).</summary>
    private static (double Raw, double Blurred) NaturalError(PixelSource result, float[] truth, int w, int h, PixelRect hole)
    {
        var got = new float[w * h];
        Span<float> c = stackalloc float[3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                result.Read(x, y, c);
                got[y * w + x] = (c[0] + c[1] + c[2]) / 3;
            }
        float Truth(int x, int y) => (truth[(y * w + x) * 4] + truth[(y * w + x) * 4 + 1] + truth[(y * w + x) * 4 + 2]) / 3;
        float Blur(Func<int, int, float> f, int x, int y)
        {
            float s = 0;
            int n = 0;
            for (int j = -2; j <= 2; j++)
                for (int i = -2; i <= 2; i++)
                {
                    int sx = Math.Clamp(x + i, 0, w - 1), sy = Math.Clamp(y + j, 0, h - 1);
                    s += f(sx, sy);
                    n++;
                }
            return s / n;
        }
        double raw = 0, blurred = 0;
        int count = 0;
        for (int y = hole.Top; y < hole.Bottom; y++)
            for (int x = hole.Left; x < hole.Right; x++)
            {
                double d = got[y * w + x] - Truth(x, y);
                raw += d * d;
                double e = Blur((sx, sy) => got[sy * w + sx], x, y) - Blur(Truth, x, y);
                blurred += e * e;
                count++;
            }
        return (Math.Sqrt(raw / count), Math.Sqrt(blurred / count));
    }

    [Theory]
    [InlineData("right edge", 190, 0, 240, 180, 0.06)]
    [InlineData("top edge", 0, 0, 240, 45, 0.06)]
    [InlineData("top-right corner", 180, 0, 240, 60, 0.06)]
    [InlineData("interior", 95, 55, 145, 105, 0.06)]
    public void A_natural_texture_with_a_diagonal_edge_is_rebuilt(string where, int l, int t, int r, int b, double limit)
    {
        const int w = 240, h = 180;
        var truth = Natural(w, h);
        var hole = new PixelRect(l, t, r, b);
        var result = Fill(truth, w, h, hole, blemish: 0.95f);
        var (raw, blurred) = NaturalError(result, truth, w, h, hole);
        output.WriteLine($"natural texture, {where} hole {hole.Width}×{hole.Height}: RMSE {raw:F4}, blurred RMSE {blurred:F4}");
        Assert.True(blurred < limit, $"{where}: blurred RMSE {blurred:F4}");
    }
}
