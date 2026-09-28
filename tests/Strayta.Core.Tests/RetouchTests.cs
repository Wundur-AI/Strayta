using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

/// <summary>Clone Stamp, Healing Brush and Spot Healing: offsets, sampling, the Poisson heal and the spot search.</summary>
public class RetouchTests(ITestOutputHelper output)
{
    private static readonly PixelRect Canvas = PixelRect.FromSize(120, 80);

    /// <summary>An opaque RGB layer whose red channel encodes x and green encodes y, so every pixel says where it came from.</summary>
    private static PixelLayer Coordinates(int bitDepth = 8, bool background = false)
    {
        int w = Canvas.Width, h = Canvas.Height;
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, bitDepth)).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                Set(planes[0], i, x / 255f);
                Set(planes[1], i, y / 255f);
                Set(planes[2], i, 0.5f);
                Set(planes[3], i, 1f);
            }
        return new PixelLayer { Name = "Photo", Bounds = Canvas, Pixels = new Raster(ColorMode.Rgb, planes[..3], background ? null : planes[3]) };
    }

    private static void Set(Plane p, int i, float v)
    {
        if (p.BitDepth == 16) p.AsUInt16()[i] = (ushort)MathF.Round(v * 65535f);
        else p.Data[i] = (byte)MathF.Round(v * 255f);
    }

    private static float At(PixelLayer layer, int channel, int x, int y) =>
        layer.Pixels!.ColorPlanes[channel].GetNormalized((y - layer.Bounds.Top) * layer.Bounds.Width + (x - layer.Bounds.Left));

    // ---- Aligned ------------------------------------------------------------------------------------

    [Fact]
    public void Aligned_keeps_the_first_offset_and_non_aligned_restarts_at_the_source()
    {
        var a = new CloneAligner();
        Assert.Null(a.BeginStroke(10, 10, aligned: true));

        a.SetSource(20, 30);
        Assert.Equal((40, 5), a.BeginStroke(60.4f, 35.9f, aligned: true));
        Assert.Equal((40, 5), a.BeginStroke(100, 70, aligned: true)); // later strokes keep the offset
        Assert.Equal((60, 60), a.SourceFor(100, 65, aligned: true));

        Assert.Equal((80, 40), a.BeginStroke(100, 70, aligned: false)); // each stroke starts at the source again
        Assert.Equal((-10, -10), a.BeginStroke(10, 20, aligned: false));
        Assert.Equal((20.5f, 30.5f), a.SourceFor(0, 0, aligned: false));

        a.SetSource(0, 0); // a new source point forgets the aligned offset
        Assert.Null(a.Offset);
        Assert.Equal((5, 6), a.BeginStroke(5, 6, aligned: true));
    }

    // ---- Clone Stamp ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Clone_copies_source_pixels_offset_by_destination_minus_source(int bitDepth)
    {
        var layer = Coordinates(bitDepth);
        var source = new CloneSource(30, 10, PixelSource.FromRaster(layer.Pixels, layer.Bounds));
        var stroke = PaintStroke.Cloning(layer, false, new BrushSettings(12, 1f, 1f), source, Canvas);
        stroke.StrokeTo(70, 40);
        var (pixels, bounds) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, bitDepth);
        var result = new PixelLayer { Bounds = bounds, Pixels = pixels };

        for (int y = 37; y < 43; y++)
            for (int x = 67; x < 73; x++)
            {
                Assert.Equal((x - 30) / 255f, At(result, 0, x, y), 5);
                Assert.Equal((y - 10) / 255f, At(result, 1, x, y), 5);
            }
        Assert.Equal(80 / 255f, At(result, 0, 80, 40), 5); // outside the brush: untouched
        Assert.Equal(bitDepth, pixels!.BitDepth);
    }

    [Fact]
    public void Clone_samples_the_layer_as_it_was_when_the_stroke_started()
    {
        // The source overlaps the stroke: sampling pixels this stroke painted would smear x=20's column all the way.
        var layer = Coordinates();
        var source = new CloneSource(4, 0, PixelSource.FromRaster(layer.Pixels, layer.Bounds));
        var stroke = PaintStroke.Cloning(layer, false, new BrushSettings(9, 1f, 1f), source, Canvas);
        for (int x = 24; x <= 90; x += 2) stroke.StrokeTo(x, 40);
        var (pixels, bounds) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        var result = new PixelLayer { Bounds = bounds, Pixels = pixels };
        for (int x = 30; x < 85; x++) Assert.Equal((x - 4) / 255f, At(result, 0, x, 40), 5);
    }

    [Fact]
    public void Clone_keeps_a_Background_opaque_and_respects_the_selection()
    {
        var layer = Coordinates(background: true);
        var selection = SelectionMask.Rectangle(new PixelRect(60, 0, 120, 80), Canvas);
        var source = new CloneSource(-20, 0, PixelSource.FromRaster(layer.Pixels, layer.Bounds));
        var stroke = PaintStroke.Cloning(layer, false, new BrushSettings(20, 1f, 1f), source, Canvas, selection);
        stroke.StrokeTo(60, 40);
        var (pixels, bounds) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        var result = new PixelLayer { Bounds = bounds, Pixels = pixels };
        Assert.Null(pixels!.Alpha);
        Assert.Equal(Canvas, bounds);
        Assert.Equal(55 / 255f, At(result, 0, 55, 40), 5); // left of the selection: unchanged
        Assert.Equal(85 / 255f, At(result, 0, 65, 40), 5); // inside: cloned from 20 px to the right
    }

    [Fact]
    public void Baking_a_small_stroke_copies_the_rest_of_the_layer_exactly()
    {
        // The baker copies rows the stroke does not touch; a Background painted with the brush gains an opaque alpha.
        var layer = Coordinates(background: true);
        var stroke = new PaintStroke(layer, new BrushSettings(6, 1f, 1f), new RgbColor(1, 0, 0), erase: false, Canvas);
        stroke.StrokeTo(30, 30);
        var (pixels, bounds) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        Assert.Equal(Canvas, bounds);
        for (int k = 0; k < 3; k++)
            for (int i = 0; i < 120 * 80; i++)
            {
                int x = i % 120, y = i / 120;
                if (Math.Abs(x - 30) <= 4 && Math.Abs(y - 30) <= 4) continue;
                Assert.Equal(layer.Pixels!.ColorPlanes[k].Data[i], pixels!.ColorPlanes[k].Data[i]);
            }
        Assert.All(pixels!.Alpha!.Data, a => Assert.Equal(255, a));
        Assert.Equal(255, pixels.ColorPlanes[0].Data[30 * 120 + 30]);
        Assert.Equal(0, pixels.ColorPlanes[1].Data[30 * 120 + 30]);
    }

    [Fact]
    public void Clone_from_a_transparent_source_paints_nothing()
    {
        var layer = Coordinates();
        var empty = new CloneSource(0, 0, PixelSource.FromRaster(null, PixelRect.Empty));
        var stroke = PaintStroke.Cloning(layer, false, new BrushSettings(20, 1f, 1f), empty, Canvas);
        stroke.StrokeTo(60, 40);
        var (pixels, bounds) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        var result = new PixelLayer { Bounds = bounds, Pixels = pixels };
        Assert.Equal(60 / 255f, At(result, 0, 60, 40), 5);
        Assert.Equal(1f, result.Pixels!.Alpha!.GetNormalized(40 * 120 + 60));
    }

    [Fact]
    public void Clone_on_a_mask_copies_mask_values()
    {
        var plane = Plane.Create(120, 80, 8);
        for (int x = 0; x < 60; x++)
            for (int y = 0; y < 80; y++) plane.Data[y * 120 + x] = 255; // left half white, right half black
        var mask = new LayerMask { Bounds = Canvas, Pixels = plane, DefaultColor = 0 };
        var layer = new PixelLayer { Bounds = Canvas, Mask = mask };
        var source = new CloneSource(50, 0, PixelSource.FromMask(mask));
        var stroke = PaintStroke.Cloning(layer, true, new BrushSettings(10, 1f, 1f), source, Canvas);
        stroke.StrokeTo(90, 40);
        var baked = MaskBaker.Bake(mask, stroke, 8);
        Assert.Equal(1f, MaskBaker.Sample(baked, 90, 40));
        Assert.Equal(0f, MaskBaker.Sample(baked, 100, 40));
    }

    // ---- Healing ------------------------------------------------------------------------------------

    /// <summary>A texture with fine detail (the thing healing must carry over) on a W×H float image with alpha.</summary>
    private static float[] Texture(int w, int h, Func<int, int, float> lighting, int colors = 3)
    {
        var px = new float[w * h * (colors + 1)];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float t = 0.25f * MathF.Sin(2 * MathF.PI * x / 16) * MathF.Sin(2 * MathF.PI * y / 24)
                          + 0.1f * MathF.Sin(2 * MathF.PI * (x + y) / 12);
                int o = (y * w + x) * (colors + 1);
                for (int k = 0; k < colors; k++) px[o + k] = Math.Clamp(0.45f + t * (1 - 0.2f * k) + lighting(x, y), 0f, 1f);
                px[o + colors] = 1f;
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

    [Fact]
    public void Heal_matches_the_destination_boundary_and_reproduces_source_gradients()
    {
        const int w = 160, h = 120;
        var canvas = PixelRect.FromSize(w, h);
        // The destination is the source's texture under different, smoothly varying light: a harmonic ramp added.
        var source = PixelSource.FromFloats(Texture(w, h, (_, _) => 0f), 3, canvas);
        var destination = PixelSource.FromFloats(Texture(w, h, (x, y) => 0.05f + 0.0008f * x - 0.0006f * y), 3, canvas);
        var area = new PixelRect(60, 40, 110, 90);
        var cov = Disc(area, 85, 65, 24);

        // Heal from the same place (offset 0): the answer is exactly the destination.
        var healed = Healing.Heal(destination, source, 0, 0, cov, area, canvas);
        Span<float> a = stackalloc float[3], b = stackalloc float[3];
        double worst = 0;
        for (int y = area.Top - 1; y <= area.Bottom; y++)
            for (int x = area.Left - 1; x <= area.Right; x++)
            {
                healed.Read(x, y, a);
                destination.Read(x, y, b);
                bool inside = x >= area.Left && x < area.Right && y >= area.Top && y < area.Bottom && cov[(y - area.Top) * area.Width + x - area.Left] > 0;
                for (int k = 0; k < 3; k++)
                {
                    if (!inside) Assert.Equal(b[k], a[k]); // outside the painted area: the destination exactly
                    worst = Math.Max(worst, Math.Abs(a[k] - b[k]));
                }
            }
        output.WriteLine($"max error healing a relit texture: {worst:E2}");
        Assert.True(worst < 2e-3, $"healed pixels differ from the relit texture by up to {worst}");

        // Heal from elsewhere with a blemish in the destination: inside, the discrete Laplacian of the result equals the
        // source's (the texture's detail is carried over), and colors follow the destination's lighting.
        var blemished = Texture(w, h, (x, y) => 0.05f + 0.0008f * x - 0.0006f * y);
        for (int y = 55; y < 75; y++)
            for (int x = 75; x < 95; x++)
                for (int k = 0; k < 3; k++) blemished[(y * w + x) * 4 + k] = 0.05f;
        var dest2 = PixelSource.FromFloats(blemished, 3, canvas);
        var healed2 = Healing.Heal(dest2, source, 48, 0, cov, area, canvas); // 48 px: a whole period of the texture
        double lapError = 0;
        for (int y = 50; y < 80; y++)
            for (int x = 70; x < 100; x++)
            {
                if (cov[(y - area.Top) * area.Width + x - area.Left] == 0) continue;
                float Lap(PixelSource s, int ox, int k)
                {
                    var c = new float[3];
                    float Get(int px, int py) { s.Read(px - ox, py, c); return c[k]; }
                    return Get(x - 1, y) + Get(x + 1, y) + Get(x, y - 1) + Get(x, y + 1) - 4 * Get(x, y);
                }
                for (int k = 0; k < 3; k++) lapError = Math.Max(lapError, Math.Abs(Lap(healed2, 0, k) - Lap(source, 48, k)));
            }
        output.WriteLine($"max Laplacian difference inside: {lapError:E2}");
        Assert.True(lapError < 1e-3, $"Laplacian differs by {lapError}");
        healed2.Read(85, 65, a);
        destination.Read(85, 65, b);
        Assert.True(Math.Abs(a[0] - b[0]) < 0.01, $"healed center {a[0]} vs ground truth {b[0]}");
    }

    [Fact]
    public void Spot_heal_removes_a_blemish_on_a_textured_image()
    {
        const int w = 240, h = 180;
        var canvas = PixelRect.FromSize(w, h);
        Func<int, int, float> light = (x, y) => 0.08f * MathF.Sin(x / 70f) + 0.0005f * y;
        var truth = Texture(w, h, light);
        var image = (float[])truth.Clone();
        // A dark, soft-edged blemish of radius 9 at (120, 90).
        for (int y = 75; y < 105; y++)
            for (int x = 105; x < 135; x++)
            {
                float d = MathF.Sqrt((x - 120) * (x - 120) + (y - 90) * (y - 90));
                float t = Math.Clamp(10 - d, 0, 1);
                for (int k = 0; k < 3; k++) image[(y * w + x) * 4 + k] *= 1 - 0.8f * t;
            }
        var blemished = PixelSource.FromFloats(image, 3, canvas);

        // A brush stroke over it, as the tool would record it.
        var layer = new PixelLayer { Bounds = canvas };
        var stroke = new PaintStroke(layer, new BrushSettings(26, 1f, 1f), default, false, canvas);
        stroke.StrokeTo(118, 89);
        stroke.StrokeTo(122, 91);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var offset = SpotHealing.FindSource(blemished, stroke, canvas);
        double searchMs = clock.Elapsed.TotalMilliseconds;
        Assert.NotNull(offset);
        var healed = Healing.HealStroke(blemished, offset.Value.Dx, offset.Value.Dy, stroke, canvas);
        double totalMs = clock.Elapsed.TotalMilliseconds;

        double Rmse(PixelSource s)
        {
            Span<float> c = stackalloc float[3];
            double sum = 0;
            int n = 0;
            for (int y = 78; y < 102; y++)
                for (int x = 108; x < 132; x++)
                {
                    if ((x - 120) * (x - 120) + (y - 90) * (y - 90) > 100) continue;
                    s.Read(x, y, c);
                    for (int k = 0; k < 3; k++)
                    {
                        double d = c[k] - truth[(y * w + x) * 4 + k];
                        sum += d * d;
                        n++;
                    }
                }
            return Math.Sqrt(sum / n);
        }
        double before = Rmse(blemished), after = Rmse(healed);
        output.WriteLine($"offset {offset}, RMSE before {before:F4}, after {after:F4} (0..1 scale); search {searchMs:F1} ms, total {totalMs:F1} ms");
        Assert.True(after < before * 0.1 && after < 0.02, $"RMSE {before:F4} -> {after:F4}");
    }

    [Fact]
    public void Heal_fills_from_the_destination_alone_when_no_source_fits()
    {
        var canvas = PixelRect.FromSize(20, 20);
        var px = new float[20 * 20 * 2];
        for (int i = 0; i < 400; i++) (px[i * 2], px[i * 2 + 1]) = (i % 20 / 20f, 1f);
        var image = PixelSource.FromFloats(px, 1, canvas);
        var area = new PixelRect(5, 5, 15, 15);
        var cov = Enumerable.Repeat(1f, 100).ToArray();
        var healed = Healing.Heal(image, PixelSource.FromFloats([], 1, PixelRect.Empty), 0, 0, cov, area, canvas);
        Span<float> c = stackalloc float[1];
        Assert.Equal(1f, healed.Read(10, 10, c), 3);
        Assert.Equal(10 / 20f, c[0], 3); // a linear ramp is harmonic: the membrane reproduces it
    }
}
