using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

/// <summary>Dodge / Burn / Sponge curves, Blur / Sharpen / Smudge strokes, sampled tips and Shape Dynamics.</summary>
public class ToningAndFocusTests
{
    private static readonly PixelRect Canvas = new(0, 0, 64, 64);

    private static float[] Apply(ToneSettings tone, float t, params float[] color)
    {
        var c = (float[])color.Clone();
        Toning.Apply(tone, c, c.Length, t);
        return c;
    }

    // ---- Dodge and Burn --------------------------------------------------------------------------------

    [Fact]
    public void Midtone_dodge_is_a_gamma_that_keeps_black_and_white()
    {
        var dodge = new ToneSettings(ToneTool.Dodge) { Range = ToneRange.Midtones, ProtectTones = false };
        Assert.Equal(MathF.Pow(0.5f, 1f / 1.8f), Apply(dodge, 1f, 0.5f)[0], 5); // ≈ 0.68
        Assert.Equal(0f, Apply(dodge, 1f, 0f)[0]);
        Assert.Equal(1f, Apply(dodge, 1f, 1f)[0], 5);
        var burn = dodge with { Tool = ToneTool.Burn };
        Assert.Equal(MathF.Pow(0.5f, 1.8f), Apply(burn, 1f, 0.5f)[0], 5);
        Assert.Equal(0.5f, Apply(dodge, 0f, 0.5f)[0]); // no strength, no change
    }

    [Fact]
    public void Ranges_change_their_own_tones_most()
    {
        float Change(ToneRange range, float v) => Apply(new ToneSettings(ToneTool.Dodge) { Range = range, ProtectTones = false }, 1f, v)[0] - v;
        Assert.True(Change(ToneRange.Shadows, 0.1f) > Change(ToneRange.Shadows, 0.8f));
        Assert.True(Change(ToneRange.Highlights, 0.8f) > Change(ToneRange.Highlights, 0.1f));
        Assert.Equal(0.5f, Apply(new ToneSettings(ToneTool.Dodge) { Range = ToneRange.Shadows, ProtectTones = false }, 1f, 0f)[0], 5); // lifts black
        Assert.Equal(0.5f, Apply(new ToneSettings(ToneTool.Burn) { Range = ToneRange.Highlights, ProtectTones = false }, 1f, 1f)[0], 5); // greys white
        Assert.Equal(1f, Apply(new ToneSettings(ToneTool.Dodge) { Range = ToneRange.Highlights, ProtectTones = false }, 1f, 0.9f)[0]); // clips
    }

    [Theory]
    [InlineData(ToneTool.Dodge, ToneRange.Shadows)]
    [InlineData(ToneTool.Dodge, ToneRange.Midtones)]
    [InlineData(ToneTool.Dodge, ToneRange.Highlights)]
    [InlineData(ToneTool.Burn, ToneRange.Shadows)]
    [InlineData(ToneTool.Burn, ToneRange.Midtones)]
    [InlineData(ToneTool.Burn, ToneRange.Highlights)]
    public void Every_curve_is_monotonic(ToneTool tool, ToneRange range)
    {
        foreach (float t in new[] { 0.25f, 1f })
        {
            float prev = -1f;
            for (int i = 0; i <= 100; i++)
            {
                float v = Toning.Curve(tool == ToneTool.Dodge, range, i / 100f, t);
                Assert.True(v >= prev - 1e-6f, $"{tool} {range} at {i}%");
                prev = v;
            }
        }
    }

    [Fact]
    public void Protect_tones_keeps_hue_and_never_clips()
    {
        var protect = new ToneSettings(ToneTool.Dodge) { Range = ToneRange.Highlights, ProtectTones = true };
        var c = Apply(protect, 1f, 0.9f, 0.5f, 0.3f);
        Assert.All(c, v => Assert.InRange(v, 0f, 1f));
        Assert.True(c[0] > c[1] && c[1] > c[2], "channel order (hue) is kept");
        Assert.True(Toning.Luma(c[0], c[1], c[2]) > Toning.Luma(0.9f, 0.5f, 0.3f));
        // Without protection the red channel clips and the color shifts.
        var raw = Apply(protect with { ProtectTones = false }, 1f, 0.9f, 0.5f, 0.3f);
        Assert.Equal(1f, raw[0]);

        var burn = new ToneSettings(ToneTool.Burn) { Range = ToneRange.Midtones, ProtectTones = true };
        var b = Apply(burn, 0.8f, 0.6f, 0.4f, 0.2f);
        Assert.Equal(0.6f / 0.2f, b[0] / b[2], 3); // burning scales the color: ratios (hue, saturation) stay
    }

    // ---- Sponge -------------------------------------------------------------------------------------------

    [Fact]
    public void Sponge_desaturates_toward_gray_and_leaves_gray_alone()
    {
        var sponge = new ToneSettings(ToneTool.Sponge) { Saturate = false, Vibrance = false };
        var c = Apply(sponge, 1f, 0.8f, 0.2f, 0.4f);
        float y = Toning.Luma(0.8f, 0.2f, 0.4f);
        Assert.All(c, v => Assert.Equal(y, v, 5));
        var half = Apply(sponge, 0.5f, 0.8f, 0.2f, 0.4f);
        Assert.Equal(y + (0.8f - y) * 0.5f, half[0], 5);
        Assert.Equal(new[] { 0.3f, 0.3f, 0.3f }, Apply(sponge with { Saturate = true }, 1f, 0.3f, 0.3f, 0.3f));
        Assert.Equal(0.3f, Apply(sponge, 1f, 0.3f)[0]); // one channel (grayscale, masks): nothing to do
    }

    [Fact]
    public void Vibrance_saturates_dull_colors_most_and_never_clips()
    {
        var vib = new ToneSettings(ToneTool.Sponge) { Saturate = true, Vibrance = true };
        float Sat(float[] c) => c.Max() - c.Min();
        var dull = Apply(vib, 1f, 0.55f, 0.5f, 0.45f);
        var vivid = Apply(vib, 1f, 0.9f, 0.2f, 0.1f);
        Assert.True(Sat(dull) / 0.1f > Sat(vivid) / 0.8f, "dull colors gain relatively more");
        Assert.All(vivid, v => Assert.InRange(v, 0f, 1f));
        Assert.True(vivid[0] < 1f || vivid[2] > 0f || Sat(vivid) >= 0.8f);
        var plain = Apply(vib with { Vibrance = false }, 1f, 0.9f, 0.2f, 0.1f);
        Assert.Equal(1f, plain[0]); // without Vibrance the saturated red clips
    }

    // ---- Toning strokes -----------------------------------------------------------------------------------

    private static PixelLayer Gray(int w, int h, byte v) => new()
    {
        Bounds = new PixelRect(0, 0, w, h),
        Pixels = new Raster(ColorMode.Rgb, [Plane(w, h, v), Plane(w, h, v), Plane(w, h, v)], null),
    };

    private static Plane Plane(int w, int h, byte v) => new(w, h, 8, Enumerable.Repeat(v, w * h).ToArray());

    private static float At(Raster r, PixelRect b, int ch, int x, int y) => r.ColorPlanes[ch].GetNormalized((y - b.Top) * b.Width + (x - b.Left));

    [Fact]
    public void A_toning_stroke_is_capped_by_the_exposure_however_often_it_crosses_itself()
    {
        var layer = Gray(64, 64, 128);
        var tone = new ToneSettings(ToneTool.Dodge) { Range = ToneRange.Midtones, ProtectTones = false };
        var stroke = PaintStroke.Toning(layer, false, new BrushSettings(16, 1f, 0.5f), tone, Canvas);
        for (int i = 0; i < 6; i++)
        {
            stroke.StrokeTo(10, 32);
            stroke.StrokeTo(54, 32);
        }
        var (px, b) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        float expected = Toning.Curve(true, ToneRange.Midtones, 128 / 255f, 0.5f);
        Assert.Equal(expected, At(px!, b, 0, 32, 32), 2);
        Assert.Null(px!.Alpha); // the Background stays opaque
        Assert.Equal(128 / 255f, At(px, b, 0, 32, 5), 3); // outside the stroke nothing changes
    }

    [Fact]
    public void Toning_is_clipped_by_the_selection()
    {
        var layer = Gray(64, 64, 128);
        var half = SelectionMask.Rectangle(new PixelRect(0, 0, 32, 64), Canvas)!;
        var stroke = PaintStroke.Toning(layer, false, new BrushSettings(20, 1f, 1f), new ToneSettings(ToneTool.Burn), Canvas, half);
        stroke.StrokeTo(20, 32);
        stroke.StrokeTo(44, 32);
        var (px, b) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        Assert.True(At(px!, b, 0, 25, 32) < 0.45f);
        Assert.Equal(128 / 255f, At(px!, b, 0, 40, 32), 3);
    }

    // ---- Blur, Sharpen, Smudge ----------------------------------------------------------------------------

    /// <summary>A vertical edge: black left of x = 32, white right.</summary>
    private static PixelLayer Edge()
    {
        var layer = Gray(64, 64, 0);
        foreach (var p in layer.Pixels!.ColorPlanes)
            for (int y = 0; y < 64; y++)
                for (int x = 32; x < 64; x++) p.Data[y * 64 + x] = 255;
        return layer;
    }

    private static (PaintStroke Stroke, LocalStroke Local) Focus(PixelLayer layer, LocalToolSettings settings, float size = 20, SelectionMask? clip = null)
    {
        var local = new LocalStroke(settings, PixelSource.FromRaster(layer.Pixels, layer.Bounds), Canvas, size);
        return (PaintStroke.Retouching(layer, false, new BrushSettings(size, 1f, 1f), local, Canvas, clip), local);
    }

    [Fact]
    public void Blur_softens_an_edge_and_more_passes_blur_more()
    {
        var layer = Edge();
        var (stroke, local) = Focus(layer, new LocalToolSettings(LocalTool.Blur) { Strength = 1f });
        Span<float> c = stackalloc float[3];
        stroke.StrokeTo(32, 10);
        stroke.StrokeTo(32, 54);
        local.Read(31, 32, c);
        float once = c[0];
        Assert.InRange(once, 0.05f, 0.5f);
        for (int i = 0; i < 4; i++)
        {
            stroke.StrokeTo(32, 10);
            stroke.StrokeTo(32, 54);
        }
        local.Read(29, 32, c);
        Assert.True(c[0] > 0.02f, "repeated passes spread the blur further");
        local.Read(31, 32, c);
        Assert.True(c[0] > once);
        var (px, b) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        Assert.True(MathF.Abs(c[0] - At(px!, b, 0, 31, 32)) <= 0.5f / 255f + 1e-5f); // the commit is the working image (8-bit)
        Assert.Equal(0f, At(px!, b, 0, 5, 32));
        Assert.Null(px!.Alpha);
    }

    [Fact]
    public void Sharpen_raises_edge_contrast_and_protect_detail_prevents_halos()
    {
        var layer = Gray(64, 64, 100);
        foreach (var p in layer.Pixels!.ColorPlanes)
            for (int y = 0; y < 64; y++)
                for (int x = 32; x < 64; x++) p.Data[y * 64 + x] = 160;
        Span<float> c = stackalloc float[3];
        var (raw, rawLocal) = Focus(layer, new LocalToolSettings(LocalTool.Sharpen) { Strength = 1f, ProtectDetail = false });
        raw.StrokeTo(32, 32);
        rawLocal.Read(31, 32, c);
        Assert.True(c[0] < 100 / 255f - 0.01f, $"the dark side gets darker ({c[0] * 255:F0})");
        rawLocal.Read(32, 32, c);
        Assert.True(c[0] > 160 / 255f + 0.01f, "the light side gets lighter");

        var (safe, safeLocal) = Focus(layer, new LocalToolSettings(LocalTool.Sharpen) { Strength = 1f, ProtectDetail = true });
        safe.StrokeTo(32, 32);
        safeLocal.Read(31, 32, c);
        Assert.True(c[0] >= 100 / 255f - 1e-4f, "Protect Detail never overshoots the neighbourhood");
    }

    [Fact]
    public void Smudge_drags_color_along_the_stroke()
    {
        var layer = Edge();
        var (stroke, local) = Focus(layer, new LocalToolSettings(LocalTool.Smudge) { Strength = 0.9f }, size: 12);
        stroke.StrokeTo(20, 32); // start in the black
        for (float x = 22; x <= 50; x += 2) stroke.StrokeTo(x, 32);
        Span<float> c = stackalloc float[3];
        local.Read(40, 32, c);
        Assert.True(c[0] < 0.7f, $"black paint is dragged into the white ({c[0]:F2})");
        local.Read(40, 10, c);
        Assert.Equal(1f, c[0]); // off the path nothing moves
    }

    [Fact]
    public void Smudge_with_finger_painting_starts_with_the_foreground_color()
    {
        var layer = Gray(64, 64, 255);
        var settings = new LocalToolSettings(LocalTool.Smudge) { Strength = 1f, FingerPainting = true, FingerColor = new RgbColor(1, 0, 0) };
        var (stroke, local) = Focus(layer, settings, size: 10);
        stroke.StrokeTo(20, 32);
        stroke.StrokeTo(30, 32);
        Span<float> c = stackalloc float[3];
        local.Read(20, 32, c);
        Assert.True(c[0] > 0.99f && c[1] < 0.05f, "the first dab paints the foreground color");
    }

    [Fact]
    public void Sample_all_layers_waits_for_the_image_and_paints_it_onto_an_empty_layer()
    {
        var empty = new PixelLayer();
        var local = new LocalStroke(new LocalToolSettings(LocalTool.Blur) { Strength = 1f }, PixelSource.FromRaster(null, PixelRect.Empty), Canvas, 16, sampleSeparately: true);
        var stroke = PaintStroke.Retouching(empty, false, new BrushSettings(16, 1f, 1f), local, Canvas);
        int version = stroke.Version;
        stroke.StrokeTo(32, 32);
        Span<float> c = stackalloc float[3];
        Assert.False(local.IsReady);
        Assert.Equal(0f, local.Read(32, 32, c)); // nothing yet
        var below = Gray(64, 64, 200);
        local.SetSample(PixelSource.FromRaster(below.Pixels, below.Bounds));
        Assert.True(stroke.Version > version + 1);
        float a = local.Read(32, 32, c);
        Assert.True(a > 0.99f && Math.Abs(c[0] - 200 / 255f) < 0.01f, $"the blurred image lands on the empty layer (a {a:F2}, {c[0] * 255:F0})");
        var (px, b) = StrokeBaker.Bake(empty, stroke, ColorMode.Rgb, 8);
        Assert.False(b.IsEmpty);
        Assert.NotNull(px!.Alpha);
    }

    [Fact]
    public void Focus_tools_on_a_mask_change_the_mask_values()
    {
        var layer = Gray(64, 64, 128);
        var mask = new LayerMask { Bounds = Canvas, Pixels = Plane(64, 64, 255), DefaultColor = 255 };
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 32; x++) mask.Pixels.Data[y * 64 + x] = 0;
        layer.Mask = mask;
        var local = new LocalStroke(new LocalToolSettings(LocalTool.Blur) { Strength = 1f }, PixelSource.FromMask(mask), Canvas, 20);
        var stroke = PaintStroke.Retouching(layer, true, new BrushSettings(20, 1f, 1f), local, Canvas);
        stroke.StrokeTo(32, 20);
        stroke.StrokeTo(32, 44);
        var baked = MaskBaker.Bake(mask, stroke, 8);
        float v = MaskBaker.Sample(baked, 31, 32);
        Assert.InRange(v, 0.05f, 0.5f);

        var dodge = PaintStroke.Toning(layer, true, new BrushSettings(20, 1f, 1f), new ToneSettings(ToneTool.Burn) { Range = ToneRange.Highlights }, Canvas);
        dodge.StrokeTo(48, 32);
        Assert.Equal(0.5f, MaskBaker.Sample(MaskBaker.Bake(mask, dodge, 8), 48, 32), 2);
    }

    // ---- Sampled tips and dynamics ------------------------------------------------------------------------

    [Fact]
    public void A_sampled_tip_paints_its_image_scaled_and_turned()
    {
        // A 20×4 bar: at size 20 it paints a horizontal bar; at 90° a vertical one.
        var tip = new BrushTip("bar", 20, 4, Enumerable.Repeat((byte)255, 80).ToArray());
        var flat = new PaintStroke(new PixelLayer(), new BrushSettings(20, 1f, 1f) { Tip = tip }, new RgbColor(1, 0, 0), false, Canvas);
        flat.StrokeTo(32, 32);
        Assert.Equal(1f, flat.CoverageAt(38, 32), 2);
        Assert.Equal(0f, flat.CoverageAt(32, 38));
        var turned = new PaintStroke(new PixelLayer(), new BrushSettings(20, 1f, 1f) { Tip = tip, Angle = 90 }, new RgbColor(1, 0, 0), false, Canvas);
        turned.StrokeTo(32, 32);
        Assert.Equal(0f, turned.CoverageAt(38, 32));
        Assert.Equal(1f, turned.CoverageAt(32, 38), 2);
        var small = new PaintStroke(new PixelLayer(), new BrushSettings(10, 1f, 1f) { Tip = tip }, new RgbColor(1, 0, 0), false, Canvas);
        small.StrokeTo(32, 32);
        Assert.Equal(0f, small.CoverageAt(39, 32)); // half size: 10 px long
        Assert.True(small.CoverageAt(35, 32) > 0.9f);
    }

    [Fact]
    public void Mip_levels_keep_a_downscaled_tip_smooth()
    {
        // A checkerboard tip drawn at a tenth of its size averages to gray instead of aliasing to 0 or 1.
        var data = new byte[100 * 100];
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++) data[y * 100 + x] = (byte)((x + y) % 2 == 0 ? 255 : 0);
        var tip = new BrushTip("checker", 100, 100, data);
        int level = tip.LevelFor(0.1f);
        Assert.True(level >= 3);
        Assert.InRange(tip.Sample(level, 50, 50), 0.4f, 0.6f);
    }

    [Fact]
    public void Scattering_and_size_jitter_vary_dabs_repeatably()
    {
        var dyn = new BrushDynamics { SizeJitter = 1f, Scatter = 2f, Count = 3, Seed = 7 };
        PaintStroke Paint()
        {
            var s = new PaintStroke(new PixelLayer(), new BrushSettings(6, 1f, 1f) { Dynamics = dyn }, new RgbColor(1, 0, 0), false, Canvas);
            s.StrokeTo(10, 32);
            s.StrokeTo(54, 32);
            return s;
        }
        var a = Paint();
        var b = Paint();
        Assert.Equal(a.Bounds, b.Bounds);
        Assert.True(a.Bounds.Height > 8, $"dabs scatter across the path ({a.Bounds})");
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++) Assert.Equal(a.CoverageAt(x, y), b.CoverageAt(x, y));
    }
}
