using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Rendering.Tests;

/// <summary>The color math of the adjustments added after Photoshop 6, on known inputs.</summary>
public class ExtendedAdjustmentMathTests
{
    private static void Near((float R, float G, float B) expected, (float R, float G, float B) actual, float tolerance = 0.002f)
    {
        Assert.InRange(actual.R, expected.R - tolerance, expected.R + tolerance);
        Assert.InRange(actual.G, expected.G - tolerance, expected.G + tolerance);
        Assert.InRange(actual.B, expected.B - tolerance, expected.B + tolerance);
    }

    private static float Luma((float R, float G, float B) c) => 0.3f * c.R + 0.59f * c.G + 0.11f * c.B;

    [Fact]
    public void Exposure_adds_stops_in_linear_light()
    {
        float v = GradientLut.LinearToSrgb(0.2f);
        Assert.Equal(GradientLut.LinearToSrgb(0.4f), AdjustmentMath.Exposure(new ExposureAdjustment(1, 0, 1), v), 3);
        Assert.Equal(GradientLut.LinearToSrgb(0.05f), AdjustmentMath.Exposure(new ExposureAdjustment(-2, 0, 1), v), 3);
        Assert.Equal(v, AdjustmentMath.Exposure(ExposureAdjustment.Default, v), 3);
        Assert.Equal(GradientLut.LinearToSrgb(0.3f), AdjustmentMath.Exposure(new ExposureAdjustment(0, 0.1f, 1), v), 3);
        // Gamma below 1 brightens, above 1 darkens (Photoshop's slider runs 9.99 to 0.01).
        Assert.Equal(GradientLut.LinearToSrgb(MathF.Pow(0.2f, 0.5f)), AdjustmentMath.Exposure(new ExposureAdjustment(0, 0, 0.5f), v), 3);
        Assert.Equal(1f, AdjustmentMath.Exposure(new ExposureAdjustment(20, 0, 1), 0.01f), 3);
    }

    [Fact]
    public void Black_and_white_weights_primary_and_secondary_parts()
    {
        var a = BlackWhiteAdjustment.Default;
        Assert.Equal(0.40f, AdjustmentMath.BlackWhiteGray(a, 1, 0, 0), 4);
        Assert.Equal(0.60f, AdjustmentMath.BlackWhiteGray(a, 1, 1, 0), 4);
        Assert.Equal(0.40f, AdjustmentMath.BlackWhiteGray(a, 0, 1, 0), 4);
        Assert.Equal(0.60f, AdjustmentMath.BlackWhiteGray(a, 0, 1, 1), 4);
        Assert.Equal(0.20f, AdjustmentMath.BlackWhiteGray(a, 0, 0, 1), 4);
        Assert.Equal(0.80f, AdjustmentMath.BlackWhiteGray(a, 1, 0, 1), 4);
        Assert.Equal(1f, AdjustmentMath.BlackWhiteGray(a, 1, 1, 1), 4);
        Assert.Equal(0.5f, AdjustmentMath.BlackWhiteGray(a, 0.5f, 0.5f, 0.5f), 4);
        // Orange: gray 0 + yellow part 0.5 × 60% + red part 0.5 × 40%.
        Assert.Equal(0.5f, AdjustmentMath.BlackWhiteGray(a, 1, 0.5f, 0), 4);
        Near((0.4f, 0.4f, 0.4f), AdjustmentMath.Apply(a, 1, 0, 0));
    }

    [Fact]
    public void Black_and_white_tint_keeps_the_gray_luminosity()
    {
        var a = BlackWhiteAdjustment.Default with { Tint = true };
        var c = AdjustmentMath.Apply(a, 1, 1, 0);
        Assert.Equal(0.6f, Luma(c), 3);
        Assert.True(c.R > c.B); // the default tint is warm
        Near((0, 0, 0), AdjustmentMath.Apply(a, 0, 0, 0));
        Near((1, 1, 1), AdjustmentMath.Apply(a, 1, 1, 1));
    }

    [Fact]
    public void Channel_mixer_is_a_weighted_sum_with_a_constant()
    {
        Near((0.2f, 0.5f, 0.9f), AdjustmentMath.Apply(ChannelMixerAdjustment.Default, 0.2f, 0.5f, 0.9f));
        var swap = ChannelMixerAdjustment.Default with { Red = new(0, 0, 100, 0), Blue = new(100, 0, 0, 10) };
        Near((0.9f, 0.5f, 0.3f), AdjustmentMath.Apply(swap, 0.2f, 0.5f, 0.9f));
        var infrared = ChannelMixerAdjustment.Default with { Monochrome = true, Gray = new(-70, 200, -30, 0) };
        float g = Math.Clamp(-0.7f * 0.2f + 2f * 0.3f - 0.3f * 0.1f, 0, 1);
        Near((g, g, g), AdjustmentMath.Apply(infrared, 0.2f, 0.3f, 0.1f));
    }

    [Fact]
    public void Selective_color_relative_leaves_white_alone_and_absolute_does_not()
    {
        var whites = SelectiveColorAdjustment.Default.With(SelectiveColorRange.Whites, new(50, 0, 0, 0));
        Near((1, 1, 1), AdjustmentMath.Apply(whites, 1, 1, 1));
        var absolute = whites with { Absolute = true };
        Near((0.5f, 1, 1), AdjustmentMath.Apply(absolute, 1, 1, 1));
    }

    [Fact]
    public void Selective_color_only_touches_its_range()
    {
        var reds = SelectiveColorAdjustment.Default.With(SelectiveColorRange.Reds, new(100, 0, 0, 0)) with { Absolute = true };
        Near((0, 0, 0), AdjustmentMath.Apply(reds, 1, 0, 0));
        Near((0, 0, 1), AdjustmentMath.Apply(reds, 0, 0, 1));
        Near((0.4f, 0.4f, 0.4f), AdjustmentMath.Apply(reds, 0.4f, 0.4f, 0.4f));
        Span<float> w = stackalloc float[9];
        AdjustmentMath.SelectiveColorWeights(1, 0.5f, 0, w);
        Assert.Equal(0.5f, w[(int)SelectiveColorRange.Reds], 4);
        Assert.Equal(0.5f, w[(int)SelectiveColorRange.Yellows], 4);
        Assert.Equal(0f, w[(int)SelectiveColorRange.Neutrals], 4);
    }

    [Fact]
    public void Gradient_map_maps_luminosity_along_the_gradient()
    {
        var bw = new GradientMapAdjustment(GradientModel.TwoColor("Black, White", RgbColor.Black, new RgbColor(1, 1, 1)), false, false, GradientMethod.Classic);
        float l = 0.3f * 0.8f + 0.59f * 0.2f + 0.11f * 0.4f;
        Near((l, l, l), AdjustmentMath.Apply(bw, 0.8f, 0.2f, 0.4f), 0.003f);
        Near((1 - l, 1 - l, 1 - l), AdjustmentMath.Apply(bw with { Reverse = true }, 0.8f, 0.2f, 0.4f), 0.003f);
        var toRed = new GradientMapAdjustment(GradientModel.TwoColor("x", RgbColor.Black, new RgbColor(1, 0, 0)), false, false, GradientMethod.Classic);
        Near((1, 0, 0), AdjustmentMath.Apply(toRed, 1, 1, 1));
    }

    [Fact]
    public void Photo_filter_multiplies_by_the_filter_at_its_density()
    {
        var f = new PhotoFilterAdjustment(new RgbColor(1, 0.5f, 0), 50, false);
        Near((0.8f, 0.6f, 0.4f), AdjustmentMath.Apply(f, 0.8f, 0.8f, 0.8f));
        var keep = f with { PreserveLuminosity = true };
        Assert.Equal(0.8f, Luma(AdjustmentMath.Apply(keep, 0.8f, 0.8f, 0.8f)), 3);
    }

    [Fact]
    public void Color_balance_moves_midtones_and_keeps_the_ends()
    {
        var warm = new ColorBalanceAdjustment(default, new(100, 0, 0), default, false);
        var mid = AdjustmentMath.Apply(warm, 0.5f, 0.5f, 0.5f);
        Assert.True(mid.R > 0.6f);
        Assert.Equal(0.5f, mid.G, 3);
        Near((0, 0, 0), AdjustmentMath.Apply(warm, 0, 0, 0));
        Near((1, 1, 1), AdjustmentMath.Apply(warm, 1, 1, 1));
        var kept = AdjustmentMath.Apply(warm with { PreserveLuminosity = true }, 0.5f, 0.5f, 0.5f);
        Assert.Equal(0.5f, Luma(kept), 3);
        Assert.True(kept.R > kept.G);
        var shadows = new ColorBalanceAdjustment(new(0, 0, 100), default, default, false);
        Assert.True(AdjustmentMath.Apply(shadows, 0, 0, 0).B > 0.2f);
    }

    [Fact]
    public void Vibrance_keeps_grays_and_saturation_minus_100_removes_color()
    {
        Near((0.3f, 0.3f, 0.3f), AdjustmentMath.Apply(new VibranceAdjustment(100, 100), 0.3f, 0.3f, 0.3f));
        var gray = AdjustmentMath.Apply(new VibranceAdjustment(0, -100), 0.8f, 0.2f, 0.4f);
        float l = 0.3f * 0.8f + 0.59f * 0.2f + 0.11f * 0.4f;
        Near((l, l, l), gray);
        // Vibrance boosts a muted color more than a saturated one.
        var muted = AdjustmentMath.Apply(new VibranceAdjustment(100, 0), 0.5f, 0.45f, 0.45f);
        var vivid = AdjustmentMath.Apply(new VibranceAdjustment(100, 0), 1f, 0f, 0.1f);
        Assert.True(muted.R - muted.G > 0.05f * 1.5f);
        Near((1f, 0f, 0.1f), vivid, 0.02f);
        var sat = AdjustmentMath.Apply(new VibranceAdjustment(0, 100), 0.9f, 0.2f, 0.4f);
        Assert.InRange(sat.R, 0f, 1f); // never clips
    }

    [Fact]
    public void Color_lookup_interpolates_a_cube_exactly_for_linear_maps()
    {
        var swap = LookupTable3D.WriteCube("swap", 5, (r, g, b) => (b, g, r));
        var a = new ColorLookupAdjustment(ColorLookupKind.Lut3D, "swap.cube", "CUBE", swap, false);
        Near((0.9f, 0.5f, 0.2f), AdjustmentMath.Apply(a, 0.2f, 0.5f, 0.9f), 0.0005f);
        var table = LookupTable3D.Parse(swap, "CUBE")!;
        Near((0.9f, 0.5f, 0.2f), table.Apply(0.2f, 0.5f, 0.9f, trilinear: true), 0.0005f);
        Assert.Equal("swap", table.Title);
    }

    [Fact]
    public void Color_lookup_reads_3dl_with_blue_fastest_and_integer_scale()
    {
        var sb = new System.Text.StringBuilder("0 1023\n");
        for (int r = 0; r < 2; r++)
            for (int g = 0; g < 2; g++)
                for (int b = 0; b < 2; b++)
                    sb.Append($"{b * 4095} {g * 4095} {r * 4095}\n"); // swap red and blue, 12-bit
        var table = LookupTable3D.Parse(System.Text.Encoding.ASCII.GetBytes(sb.ToString()), "3DL")!;
        Assert.Equal(2, table.Size);
        Near((1, 0, 0), table.Apply(0, 0, 1));
        Near((0.25f, 0.5f, 0.75f), table.Apply(0.75f, 0.5f, 0.25f));
    }

    [Fact]
    public void Color_lookup_nonlinear_cube_matches_at_grid_points_and_interpolates_between()
    {
        var gamma = LookupTable3D.WriteCube("g", 17, (r, g, b) => (r * r, g * g, b * b));
        var table = LookupTable3D.Parse(gamma)!;
        Near((0.25f, 0.0625f, 1f), table.Apply(0.5f, 0.25f, 1f), 0.0005f);
        var between = table.Apply(0.53f, 0.53f, 0.53f);
        Assert.InRange(between.R, 0.53f * 0.53f - 0.002f, 0.53f * 0.53f + 0.002f);
    }

    [Fact]
    public void Every_new_adjustment_renders_without_warnings()
    {
        Adjustment[] all =
        [
            new ExposureAdjustment(1, 0, 1), new VibranceAdjustment(20, 10), new ColorBalanceAdjustment(new(10, 0, 0), default, default, true),
            BlackWhiteAdjustment.Default, new PhotoFilterAdjustment(new RgbColor(1, 0.5f, 0), 25, true), ChannelMixerAdjustment.Default,
            SelectiveColorAdjustment.Default.With(SelectiveColorRange.Reds, new(10, 0, 0, 0)),
            new GradientMapAdjustment(GradientModel.TwoColor("x", RgbColor.Black, new RgbColor(1, 1, 1)), false, false, GradientMethod.Perceptual),
            new ColorLookupAdjustment(ColorLookupKind.Lut3D, "id", "CUBE", LookupTable3D.WriteCube("id", 2, (r, g, b) => (r, g, b)), false),
        ];
        foreach (var adjustment in all)
        {
            var doc = new Document(2, 2, ColorMode.Rgb, 8);
            Plane P(byte v) => new(2, 2, 8, Enumerable.Repeat(v, 4).ToArray());
            doc.Root.Add(new PixelLayer { Bounds = new PixelRect(0, 0, 2, 2), Pixels = new Raster(ColorMode.Rgb, [P(200), P(100), P(50)], null) });
            doc.Root.Add(new AdjustmentLayer { Adjustment = adjustment, Kind = adjustment.GetType().Name });
            var result = Compositor.Render(doc);
            Assert.DoesNotContain(result.Warnings, w => w.Contains("not applied"));
        }
    }
}
