using System.Diagnostics;
using Strayta.Core.Painting;

namespace Strayta.Core.Tests;

/// <summary>Content-Aware synthesis (PatchMatch + voting + Poisson heal) against ground truth on synthetic images.</summary>
public class ContentAwareTests(ITestOutputHelper output)
{
    /// <summary>Straight RGB + alpha floats from a function of (x, y) per channel.</summary>
    private static float[] Image(int w, int h, Func<int, int, int, float> f)
    {
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                for (int k = 0; k < 3; k++) px[(y * w + x) * 4 + k] = Math.Clamp(f(x, y, k), 0f, 1f);
                px[(y * w + x) * 4 + 3] = 1f;
            }
        return px;
    }

    private static float[] Disc(PixelRect area, float cx, float cy, float r)
    {
        var cov = new float[area.Width * area.Height];
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if ((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy) <= r * r)
                    cov[(y - area.Top) * area.Width + (x - area.Left)] = 1f;
        return cov;
    }

    /// <summary>RMSE over the hole between a result and the ground truth.</summary>
    private static double HoleError(PixelSource result, float[] truth, int w, float[] cov, PixelRect area)
    {
        Span<float> c = stackalloc float[3];
        double sum = 0;
        int n = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                if (cov[(y - area.Top) * area.Width + x - area.Left] <= 0) continue;
                result.Read(x, y, c);
                for (int k = 0; k < 3; k++)
                {
                    double d = c[k] - truth[(y * w + x) * 4 + k];
                    sum += d * d;
                    n++;
                }
            }
        return Math.Sqrt(sum / n);
    }

    private static PixelSource Damage(float[] truth, int w, int h, float[] cov, PixelRect area, float value)
    {
        var px = (float[])truth.Clone();
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (cov[(y - area.Top) * area.Width + x - area.Left] > 0)
                    for (int k = 0; k < 3; k++) px[(y * w + x) * 4 + k] = value;
        return PixelSource.FromFloats(px, 3, PixelRect.FromSize(w, h));
    }

    [Fact]
    public void Content_aware_rebuilds_a_periodic_texture_much_better_than_a_smooth_fill()
    {
        const int w = 260, h = 200;
        var canvas = PixelRect.FromSize(w, h);
        // A two-dimensional texture with a gentle light gradient.
        var truth = Image(w, h, (x, y, k) =>
            0.45f + 0.22f * MathF.Sin(2 * MathF.PI * x / 14f) * MathF.Sin(2 * MathF.PI * y / 18f) + 0.1f * (k - 1) * MathF.Sin(2 * MathF.PI * (x + y) / 11f)
            + 0.0006f * x);
        var area = new PixelRect(100, 70, 160, 130);
        var cov = Disc(area, 130, 100, 26);
        var damaged = Damage(truth, w, h, cov, area, 0.05f);

        var clock = Stopwatch.StartNew();
        var texture = PatchSynthesis.Synthesize(damaged, cov, area, canvas, new SynthesisOptions { SampleMargin = 100 })!;
        var healed = Healing.Heal(damaged, texture, 0, 0, cov, area, canvas);
        double ms = clock.Elapsed.TotalMilliseconds;
        var membrane = Healing.Heal(damaged, PixelSource.FromFloats([], 3, PixelRect.Empty), 0, 0, cov, area, canvas);

        double synthError = HoleError(healed, truth, w, cov, area);
        double smoothError = HoleError(membrane, truth, w, cov, area);
        output.WriteLine($"periodic texture, hole r=26: content-aware RMSE {synthError:F4} vs smooth fill {smoothError:F4} ({ms:F0} ms)");
        Assert.True(synthError < 0.02, $"content-aware RMSE {synthError}");
        Assert.True(synthError < smoothError * 0.35, $"content-aware {synthError} vs smooth {smoothError}");
    }

    [Fact]
    public void Content_aware_continues_a_straight_edge_through_the_hole()
    {
        const int w = 240, h = 180;
        var canvas = PixelRect.FromSize(w, h);
        // Two textured regions separated by a horizontal edge through the hole.
        var truth = Image(w, h, (x, y, k) =>
            (y < 90 ? 0.25f : 0.7f) + 0.06f * MathF.Sin(2 * MathF.PI * x / 9f) * MathF.Cos(2 * MathF.PI * y / 7f) + 0.03f * k);
        var area = new PixelRect(95, 65, 145, 115);
        var cov = Disc(area, 120, 90, 22);
        var damaged = Damage(truth, w, h, cov, area, 1f);

        var healed = PatchSynthesisHeal(damaged, cov, area, canvas);
        double error = HoleError(healed, truth, w, cov, area);
        // Rows well above and below the edge keep their own side's brightness.
        Span<float> c = stackalloc float[3];
        healed.Read(120, 76, c);
        float above = c[0];
        healed.Read(120, 104, c);
        float below = c[0];
        output.WriteLine($"edge continuation: RMSE {error:F4}, above {above:F3} (0.25±0.06), below {below:F3} (0.70±0.06)");
        Assert.InRange(above, 0.15f, 0.36f);
        Assert.InRange(below, 0.6f, 0.8f);
        Assert.True(error < 0.06, $"RMSE {error}");
    }

    private static PixelSource PatchSynthesisHeal(PixelSource damaged, float[] cov, PixelRect area, PixelRect canvas) =>
        Healing.Heal(damaged, PatchSynthesis.Synthesize(damaged, cov, area, canvas, new SynthesisOptions { SampleMargin = 120 })!, 0, 0, cov, area, canvas);

    [Fact]
    public void Content_aware_on_a_stroke_leaves_everything_outside_it_alone_and_is_repeatable()
    {
        const int w = 200, h = 150;
        var canvas = PixelRect.FromSize(w, h);
        var truth = Image(w, h, (x, y, k) => 0.5f + 0.2f * MathF.Sin(x / 5f + k) * MathF.Sin(y / 6f));
        var layer = new PixelLayer { Bounds = canvas };
        var stroke = new PaintStroke(layer, new BrushSettings(20, 1f, 1f), default, false, canvas);
        stroke.StrokeTo(80, 70);
        stroke.StrokeTo(120, 80);
        var cov = Healing.Coverage(stroke);
        var damaged = Damage(truth, w, h, cov, stroke.Bounds, 0f);

        var a = PatchSynthesis.HealStroke(damaged, stroke, canvas, SynthesisKind.ContentAware)!;
        var b = PatchSynthesis.HealStroke(damaged, stroke, canvas, SynthesisKind.ContentAware)!;
        Span<float> ca = stackalloc float[3], cb = stackalloc float[3], cd = stackalloc float[3];
        for (int y = 0; y < h; y += 3)
            for (int x = 0; x < w; x += 3)
            {
                a.Read(x, y, ca);
                b.Read(x, y, cb);
                Assert.True(ca.SequenceEqual(cb), $"not repeatable at {x},{y}");
                if (stroke.CoverageAt(x, y) > 0 || a.Bounds.IsEmpty) continue;
                if (x < a.Bounds.Left || x >= a.Bounds.Right || y < a.Bounds.Top || y >= a.Bounds.Bottom) continue;
                damaged.Read(x, y, cd);
                Assert.True(ca.SequenceEqual(cd), $"changed outside the stroke at {x},{y}");
            }
        double error = HoleError(a, truth, w, cov, stroke.Bounds);
        output.WriteLine($"stroke heal RMSE {error:F4}");
        Assert.True(error < 0.05, $"RMSE {error}");
    }

    [Fact]
    public void Create_texture_fills_with_the_surrounding_grain_not_a_flat_color()
    {
        const int w = 160, h = 120;
        var canvas = PixelRect.FromSize(w, h);
        // Fine noise-like grain.
        var rng = new Random(3);
        var noise = Enumerable.Range(0, w * h).Select(_ => (float)rng.NextDouble()).ToArray();
        var truth = Image(w, h, (x, y, _) => 0.4f + 0.3f * noise[y * w + x]);
        var area = new PixelRect(60, 40, 100, 80);
        var cov = Disc(area, 80, 60, 18);
        var damaged = Damage(truth, w, h, cov, area, 0.9f);
        var texture = PatchSynthesis.Synthesize(damaged, cov, area, canvas, new SynthesisOptions { Kind = SynthesisKind.CreateTexture })!;
        var healed = Healing.Heal(damaged, texture, 0, 0, cov, area, canvas);

        // Mean and spread inside should match the grain's (mean 0.55, sd 0.087), not the blemish or a flat fill.
        Span<float> c = stackalloc float[3];
        var values = new List<double>();
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (cov[(y - area.Top) * area.Width + x - area.Left] > 0 && (x - 80) * (x - 80) + (y - 60) * (y - 60) < 12 * 12)
                {
                    healed.Read(x, y, c);
                    values.Add(c[0]);
                }
        double mean = values.Average(), sd = Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        output.WriteLine($"create texture: mean {mean:F3}, sd {sd:F3}");
        Assert.InRange(mean, 0.48, 0.62);
        Assert.InRange(sd, 0.04, 0.14);
    }

    [Fact]
    public void Nothing_to_sample_returns_null()
    {
        var canvas = PixelRect.FromSize(10, 10);
        var image = PixelSource.FromFloats(Image(10, 10, (_, _, _) => 0.5f), 3, canvas);
        var cov = Enumerable.Repeat(1f, 100).ToArray();
        Assert.Null(PatchSynthesis.Synthesize(image, cov, canvas, canvas));
    }
}

/// <summary>A selection-sized hole in a lit, periodic texture: an exact repeat exists nearby, and synthesis should find it.</summary>
public class ContentAwareSelectionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(null)]
    [InlineData(100)]
    public void Content_aware_fill_of_a_disc_matches_the_ground_truth(int? margin)
    {
        const int w = 320, h = 200;
        var canvas = PixelRect.FromSize(w, h);
        var px = new float[w * h * 4];
        var truth = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float t = 0.25f * MathF.Sin(2 * MathF.PI * x / 16) * MathF.Sin(2 * MathF.PI * y / 24) + 0.1f * MathF.Sin(2 * MathF.PI * (x + y) / 12);
                float v = 0.45f + t + 0.06f * MathF.Sin(x / 90f) + 0.0004f * y;
                truth[y * w + x] = v;
                float d = MathF.Sqrt((x - 100) * (x - 100) + (y - 60) * (y - 60));
                float dark = 1 - 0.8f * Math.Clamp(9 - d, 0, 1);
                for (int k = 0; k < 3; k++) px[(y * w + x) * 4 + k] = v * dark;
                px[(y * w + x) * 4 + 3] = 1;
            }
        var image = PixelSource.FromFloats(px, 3, canvas);
        var area = new PixelRect(88, 48, 113, 73);
        var cov = new float[area.Width * area.Height];
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if ((x + 0.5f - 100.5f) * (x + 0.5f - 100.5f) + (y + 0.5f - 60.5f) * (y + 0.5f - 60.5f) <= 12.5f * 12.5f) cov[(y - 48) * 25 + x - 88] = 1;
        var tex = PatchSynthesis.Synthesize(image, cov, area, canvas, new SynthesisOptions { SampleMargin = margin })!;
        var healed = Healing.Heal(image, tex, 0, 0, cov, area, canvas);
        Span<float> c = stackalloc float[3];
        double sum = 0; int n = 0;
        for (int y = 51; y < 70; y++)
            for (int x = 91; x < 110; x++)
            {
                if ((x - 100) * (x - 100) + (y - 60) * (y - 60) > 81) continue;
                healed.Read(x, y, c);
                sum += Math.Pow(c[0] - truth[y * w + x], 2); n++;
            }
        double rmse = Math.Sqrt(sum / n);
        output.WriteLine($"sample margin {margin?.ToString() ?? "whole image"}: RMSE {rmse:F4}");
        Assert.True(rmse < 0.01, $"RMSE {rmse}");
    }
}
