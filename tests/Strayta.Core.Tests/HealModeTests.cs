using Strayta.Core.Painting;

namespace Strayta.Core.Tests;

/// <summary>The healing tools' Mode, Diffusion and multiplicative (log-domain) heal.</summary>
public class HealModeTests(ITestOutputHelper output)
{
    private const int W = 160, H = 120;
    private static readonly PixelRect Canvas = PixelRect.FromSize(W, H);

    private static PixelSource Image(Func<int, int, float> f)
    {
        var px = new float[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float v = f(x, y);
                (px[(y * W + x) * 4], px[(y * W + x) * 4 + 1], px[(y * W + x) * 4 + 2], px[(y * W + x) * 4 + 3]) = (v, v, v, 1f);
            }
        return PixelSource.FromFloats(px, 3, Canvas);
    }

    private static float Texture(int x, int y) => 0.5f + 0.25f * MathF.Sin(x * 0.9f) * MathF.Cos(y * 0.7f);

    private static (float[] Cov, PixelRect Area) Disc(float cx, float cy, float r)
    {
        var area = new PixelRect((int)(cx - r - 1), (int)(cy - r - 1), (int)(cx + r + 2), (int)(cy + r + 2));
        var cov = new float[area.Width * area.Height];
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if ((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy) <= r * r)
                    cov[(y - area.Top) * area.Width + x - area.Left] = 1;
        return (cov, area);
    }

    private static float At(PixelSource s, int x, int y)
    {
        Span<float> c = stackalloc float[3];
        s.Read(x, y, c);
        return c[0];
    }

    [Fact]
    public void Lower_diffusion_keeps_more_of_the_source_tone_in_the_middle()
    {
        var source = Image((_, _) => 0.3f);
        var destination = Image((_, _) => 0.6f);
        var (cov, area) = Disc(80, 60, 30);
        float previous = 0.6f;
        for (int d = 7; d >= 1; d--)
        {
            var healed = Healing.Heal(destination, source, 0, 0, cov, area, Canvas, new HealOptions { Diffusion = d, BrushSize = 60 });
            float center = At(healed, 80, 60);
            output.WriteLine($"diffusion {d}: center {center:F4}");
            if (d == 7) Assert.Equal(0.6f, center, 3); // full diffusion: the surroundings' tone everywhere
            else Assert.True(center <= previous + 1e-4f, $"diffusion {d} center {center} above {previous}");
            previous = center;
            Assert.Equal(0.6f, At(healed, 80, 60 - 29), 0.1f); // at the edge the surroundings always win
        }
        Assert.True(previous < 0.35f, "diffusion 1 keeps nearly the source's own tone in the middle");
    }

    [Fact]
    public void Multiplicative_heal_scales_texture_into_a_shadow_where_additive_keeps_its_full_contrast()
    {
        // The destination is the same texture in a shadow: 35% of the brightness (contrast scales with it).
        var source = Image(Texture);
        var destination = Image((x, y) => 0.35f * Texture(x, y));
        var (cov, area) = Disc(80, 60, 25);
        var additive = Healing.Heal(destination, source, 0, 0, cov, area, Canvas);
        var multiplicative = Healing.Heal(destination, source, 0, 0, cov, area, Canvas, new HealOptions { Multiplicative = true });
        double ea = 0, em = 0;
        int n = 0;
        for (int y = 45; y < 75; y++)
            for (int x = 65; x < 95; x++)
            {
                if ((x - 80) * (x - 80) + (y - 60) * (y - 60) > 15 * 15) continue;
                float truth = 0.35f * Texture(x, y);
                ea += Math.Pow(At(additive, x, y) - truth, 2);
                em += Math.Pow(At(multiplicative, x, y) - truth, 2);
                n++;
            }
        ea = Math.Sqrt(ea / n);
        em = Math.Sqrt(em / n);
        output.WriteLine($"shadow heal RMSE: additive {ea:F4}, multiplicative {em:F4}");
        Assert.True(em < 0.01, $"multiplicative {em}");
        Assert.True(em < ea * 0.2, $"multiplicative {em} vs additive {ea}");
    }

    [Fact]
    public void Heal_modes_blend_the_healed_patch_over_the_layer()
    {
        Assert.Equal(PaintMode.Darken, HealOptions.PaintModeFor(HealMode.Darken));
        Assert.Equal(PaintMode.Luminosity, HealOptions.PaintModeFor(HealMode.Luminosity));
        Assert.Equal(PaintMode.Normal, HealOptions.PaintModeFor(HealMode.Replace));

        // A mid-gray layer with a bright patch healed in: Darken keeps the layer, Lighten takes the patch.
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(W, H, 8)).ToArray();
        foreach (var p in planes) p.Data.AsSpan().Fill(128);
        var layer = new PixelLayer { Bounds = Canvas, Pixels = new Raster(ColorMode.Rgb, planes, null) };
        var stroke = new PaintStroke(layer, new BrushSettings(20, 1f, 1f), default, false, Canvas);
        stroke.StrokeTo(80, 60);
        var bright = new CloneSource(0, 0, Image((_, _) => 0.9f));
        foreach (var (mode, expected) in new[] { (HealMode.Darken, 128), (HealMode.Lighten, 230), (HealMode.Normal, 230), (HealMode.Multiply, 115) })
        {
            var final = stroke.WithSource(bright, 1f, HealOptions.PaintModeFor(mode));
            var (px, b) = StrokeBaker.Bake(layer, final, ColorMode.Rgb, 8);
            Assert.Equal(expected, px!.ColorPlanes[0].Data[(60 - b.Top) * b.Width + 80 - b.Left], 1.0);
            Assert.Null(px.Alpha); // a Background stays opaque
        }
    }
}
