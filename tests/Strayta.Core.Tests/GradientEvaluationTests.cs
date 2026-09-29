using Strayta.Core.Painting;

namespace Strayta.Core.Tests;

public class GradientEvaluationTests
{
    private static readonly RgbColor Black = new(0, 0, 0), White = new(1, 1, 1), Red = new(1, 0, 0), Green = new(0, 1, 0);

    private static Gradient Stops(float smoothness, params (float Location, float Midpoint, RgbColor Color)[] stops) =>
        new(stops.Select(s => new GradientColorStop(s.Location, s.Midpoint, s.Color)).ToList(),
            [new GradientOpacityStop(0, 0.5f, 1), new GradientOpacityStop(1, 0.5f, 1)]) { Smoothness = smoothness };

    private static float Red0(GradientLut lut, float t) => lut.At(t).R;

    [Fact]
    public void Two_stops_blend_linearly_in_classic_whatever_the_smoothness()
    {
        foreach (float smooth in new[] { 0f, 0.5f, 1f })
        {
            var lut = GradientLut.Build(Stops(smooth, (0, 0.5f, Black), (1, 0.5f, White)), GradientMethod.Classic);
            Assert.Equal(0f, Red0(lut, 0), 5);
            Assert.Equal(0.25f, Red0(lut, 0.25f), 4);
            Assert.Equal(0.7f, Red0(lut, 0.7f), 4);
            Assert.Equal(1f, Red0(lut, 1), 5);
        }
    }

    [Fact]
    public void Colors_hold_before_the_first_stop_and_after_the_last()
    {
        var lut = GradientLut.Build(Stops(1, (0.2f, 0.5f, Black), (0.8f, 0.5f, White)), GradientMethod.Classic);
        Assert.Equal(0f, Red0(lut, 0.1f), 5);
        Assert.Equal(0.5f, Red0(lut, 0.5f), 3);
        Assert.Equal(1f, Red0(lut, 0.9f), 5);
    }

    [Fact]
    public void The_midpoint_is_where_the_blend_is_half_way()
    {
        var lut = GradientLut.Build(Stops(0, (0, 0.25f, Black), (1, 0.5f, White)), GradientMethod.Classic);
        Assert.Equal(0.5f, Red0(lut, 0.25f), 3);
        Assert.Equal(0.25f, Red0(lut, 0.125f), 3);
        Assert.Equal(0.75f, Red0(lut, 0.625f), 3);
    }

    [Fact]
    public void Smoothness_rounds_interior_stops_without_overshooting()
    {
        // Black, white at the middle, black: linear has a sharp peak; smooth has zero slope at the peak.
        var linear = GradientLut.Build(Stops(0, (0, 0.5f, Black), (0.5f, 0.5f, White), (1, 0.5f, Black)), GradientMethod.Classic);
        var smooth = GradientLut.Build(Stops(1, (0, 0.5f, Black), (0.5f, 0.5f, White), (1, 0.5f, Black)), GradientMethod.Classic);
        Assert.Equal(1f, Red0(smooth, 0.5f), 4);
        Assert.Equal(0.8f, Red0(linear, 0.4f), 3);
        Assert.True(Red0(smooth, 0.4f) > 0.85f, $"smooth {Red0(smooth, 0.4f)}");
        for (int i = 0; i <= 100; i++)
        {
            float v = Red0(smooth, i / 100f);
            Assert.InRange(v, 0f, 1f);
        }
        // Monotone between stops: no ripples.
        for (int i = 1; i <= 50; i++) Assert.True(Red0(smooth, i / 100f) >= Red0(smooth, (i - 1) / 100f) - 1e-5f);
    }

    [Fact]
    public void Linear_method_blends_light_not_encoded_values()
    {
        var lut = GradientLut.Build(Stops(1, (0, 0.5f, Black), (1, 0.5f, White)), GradientMethod.Linear);
        Assert.Equal(GradientLut.LinearToSrgb(0.5f), Red0(lut, 0.5f), 3); // ≈ 0.735
        Assert.Equal(0.735f, Red0(lut, 0.5f), 2);
    }

    [Fact]
    public void Perceptual_method_blends_in_oklab()
    {
        var lut = GradientLut.Build(Stops(1, (0, 0.5f, Black), (1, 0.5f, White)), GradientMethod.Perceptual);
        // Oklab lightness 0.5 is a linear luminance of 0.125, which sRGB encodes as about 0.389.
        Assert.Equal(GradientLut.LinearToSrgb(0.125f), Red0(lut, 0.5f), 2);
        var (r, g, b, _) = lut.At(0.5f);
        Assert.Equal(r, g, 4);
        Assert.Equal(r, b, 4);

        // Red to green: the perceptual middle is lighter than Classic's muddy one.
        var classic = GradientLut.Build(Stops(1, (0, 0.5f, Red), (1, 0.5f, Green)), GradientMethod.Classic).At(0.5f);
        var perceptual = GradientLut.Build(Stops(1, (0, 0.5f, Red), (1, 0.5f, Green)), GradientMethod.Perceptual).At(0.5f);
        static float Luma((float R, float G, float B, float A) c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
        Assert.True(Luma(perceptual) > Luma(classic) + 0.05f, $"perceptual {Luma(perceptual)} classic {Luma(classic)}");
        // Stops themselves come back exactly.
        Assert.Equal(1f, GradientLut.Build(Stops(1, (0, 0.5f, Red), (1, 0.5f, Green)), GradientMethod.Perceptual).At(0).R, 3);
    }

    [Fact]
    public void Opacity_stops_blend_and_transparency_off_ignores_them()
    {
        var g = new Gradient([new GradientColorStop(0, 0.5f, Red), new GradientColorStop(1, 0.5f, Red)],
            [new GradientOpacityStop(0, 0.5f, 1), new GradientOpacityStop(0.5f, 0.5f, 0), new GradientOpacityStop(1, 0.5f, 1)]) { Smoothness = 0 };
        var on = GradientLut.Build(g, GradientMethod.Classic);
        Assert.Equal(0.5f, on.At(0.25f).A, 3);
        Assert.Equal(0f, on.At(0.5f).A, 3);
        var off = GradientLut.Build(g, GradientMethod.Classic, transparency: false);
        Assert.Equal(1f, off.At(0.5f).A);
    }

    [Fact]
    public void Noise_gradients_are_repeatable_and_stay_in_range()
    {
        var noise = new GradientNoise { Seed = 7, Roughness = 0.8f, C1 = new(0.2f, 0.4f), C2 = new(0f, 1f), C3 = new(0.5f, 0.5f) };
        var g = new Gradient([], []) { Noise = noise };
        var a = GradientLut.Build(g, GradientMethod.Classic);
        var b = GradientLut.Build(g, GradientMethod.Classic);
        var c = GradientLut.Build(g with { Noise = noise with { Seed = 8 } }, GradientMethod.Classic);
        bool differs = false;
        for (int i = 0; i <= 64; i++)
        {
            float t = i / 64f;
            Assert.Equal(a.At(t), b.At(t));
            Assert.InRange(a.At(t).R, 0.2f - 1e-4f, 0.4f + 1e-4f);
            Assert.Equal(0.5f, a.At(t).B, 4);
            Assert.Equal(1f, a.At(t).A);
            differs |= Math.Abs(a.At(t).G - c.At(t).G) > 0.01f;
        }
        Assert.True(differs);
    }

    [Fact]
    public void Foreground_and_background_stops_resolve_to_the_current_colors()
    {
        var g = new Gradient(
            [new GradientColorStop(0, 0.5f, Black) { Kind = GradientStopKind.Foreground }, new GradientColorStop(1, 0.5f, Black) { Kind = GradientStopKind.Background }],
            [new GradientOpacityStop(0, 0.5f, 1), new GradientOpacityStop(1, 0.5f, 1)]);
        var r = g.Resolve(Red, Green);
        Assert.Equal(Red, r.Colors[0].Color);
        Assert.Equal(Green, r.Colors[1].Color);
        Assert.Equal(GradientStopKind.Foreground, r.Colors[0].Kind);
        Assert.NotEqual(g, r);
        Assert.Equal(r, r.Resolve(Red, Green));
    }

    [Fact]
    public void Gradient_equality_includes_smoothness_and_noise()
    {
        var a = GradientModel.TwoColor("A", Black, White);
        Assert.Equal(a, a with { });
        Assert.NotEqual(a, a with { Smoothness = 0.5f });
        Assert.NotEqual(a, a with { Noise = new GradientNoise() });
    }

    [Fact]
    public void A_spec_with_stops_draws_them_and_rebuilds_its_table_when_they_change()
    {
        var spec = new GradientSpec(GradientType.Linear, new(0, 0), new(100, 0), Black, 1, White, 1)
        {
            Stops = Stops(0, (0, 0.5f, Red), (1, 0.5f, Green)),
        };
        Assert.Equal(1f, spec.ColorAt(0).R, 4);
        var changed = spec with { Stops = Stops(0, (0, 0.5f, Green), (1, 0.5f, Red)) };
        Assert.Equal(0f, changed.ColorAt(0).R, 4);
        Assert.Equal(1f, spec.ColorAt(0).R, 4);
        Assert.Equal(spec with { }, spec); // the cached table takes no part in equality
    }

    [Fact]
    public void Painting_modes_blend_the_gradient_with_the_pixels()
    {
        var canvas = PixelRect.FromSize(4, 1);
        var gray = new Raster(ColorMode.Rgb, Enumerable.Range(0, 3).Select(_ => Filled(4, 128)).ToArray(), null);
        var spec = new GradientSpec(GradientType.Linear, new(0, 0), new(4, 0), Red, 1, Red, 1) { Mode = PaintMode.Multiply };
        var (px, _) = GradientPainter.Apply(gray, canvas, spec, null, canvas, ColorMode.Rgb, 8);
        Assert.Equal(128, px!.ColorPlanes[0].Data[1]);
        Assert.Equal(0, px.ColorPlanes[1].Data[1]);

        // Behind leaves opaque pixels alone; Clear on a transparent-capable layer removes opacity.
        var layer = new Raster(ColorMode.Rgb, Enumerable.Range(0, 3).Select(_ => Filled(4, 128)).ToArray(), Filled(4, 255));
        var (behind, _) = GradientPainter.Apply(layer, canvas, spec with { Mode = PaintMode.Behind }, null, canvas, ColorMode.Rgb, 8);
        Assert.Equal(128, behind!.ColorPlanes[1].Data[2]);
        var (cleared, _) = GradientPainter.Apply(layer, canvas, spec with { Mode = PaintMode.Clear, Opacity = 0.5f }, null, canvas, ColorMode.Rgb, 8);
        Assert.InRange(cleared!.Alpha!.Data[2], 127, 128);
    }

    private static Plane Filled(int w, byte v)
    {
        var p = Plane.Create(w, 1, 8);
        p.Data.AsSpan().Fill(v);
        return p;
    }

    [Fact]
    public void PaintBlender_matches_the_compositing_formulas()
    {
        Span<float> c = stackalloc float[3];
        c[0] = 0.5f; c[1] = 0.5f; c[2] = 0.5f;
        float a = 0f;
        // Over a transparent pixel every mode shows the paint as is.
        a = PaintBlender.Paint(PaintMode.Multiply, c, a, [1f, 0f, 0f], 1f, 3, 0, 0);
        Assert.Equal((1f, 0f, 1f), (c[0], c[1], a));
        // Screen at half coverage over opaque gray.
        c[0] = 0.5f; c[1] = 0.5f; c[2] = 0.5f;
        a = 1f;
        a = PaintBlender.Paint(PaintMode.Screen, c, a, [1f, 1f, 1f], 0.5f, 3, 0, 0);
        Assert.Equal(0.75f, c[0], 4);
        Assert.Equal(1f, a);
    }
}
