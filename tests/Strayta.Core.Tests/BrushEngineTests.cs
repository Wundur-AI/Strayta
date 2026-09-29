using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

/// <summary>Flow, tip shape, pressure, paint modes, clone source transforms and filtered source sampling.</summary>
public class BrushEngineTests
{
    private static readonly PixelRect Canvas = new(0, 0, 60, 60);

    private static PaintStroke Stroke(BrushSettings brush, PixelLayer? layer = null, SelectionMask? clip = null) =>
        new(layer ?? new PixelLayer(), brush, new RgbColor(1, 0, 0), false, Canvas, clip);

    // ---- Flow ----------------------------------------------------------------------------------------

    [Fact]
    public void Flow_builds_up_where_dabs_overlap_and_never_passes_full_coverage()
    {
        var stroke = Stroke(new BrushSettings(10, 1f, 1f) { Flow = 0.3f });
        stroke.StrokeTo(30, 30);
        Assert.Equal(0.3f, stroke.CoverageAt(30, 30), 4);
        stroke.StrokeTo(30, 30.001f); // no new dab (less than the spacing)
        Assert.Equal(0.3f, stroke.CoverageAt(30, 30), 4);
        for (int i = 0; i < 3; i++) stroke.Airbrush();
        // Four dabs at 30%: 1 − 0.7⁴.
        Assert.Equal(1 - MathF.Pow(0.7f, 4), stroke.CoverageAt(30, 30), 4);
        for (int i = 0; i < 200; i++) stroke.Airbrush();
        Assert.InRange(stroke.CoverageAt(30, 30), 0.999f, 1f);
    }

    [Fact]
    public void Full_flow_puts_a_hard_brush_at_full_coverage_at_once_and_opacity_caps_the_bake()
    {
        var layer = new PixelLayer();
        var stroke = Stroke(new BrushSettings(10, 1f, 0.4f), layer);
        stroke.StrokeTo(20, 30);
        stroke.StrokeTo(40, 30);
        Assert.Equal(1f, stroke.CoverageAt(30, 30));
        var (px, bounds) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        Assert.Equal(102, px!.Alpha!.Data[(30 - bounds.Top) * bounds.Width + (30 - bounds.Left)]); // 40%, however many dabs overlap
    }

    [Fact]
    public void Selection_clipping_is_exact_with_partial_flow()
    {
        var half = SelectionMask.Rectangle(new PixelRect(0, 0, 30, 60), Canvas)!;
        var clipped = Stroke(new BrushSettings(20, 0.5f, 1f) { Flow = 0.4f }, clip: half);
        var free = Stroke(new BrushSettings(20, 0.5f, 1f) { Flow = 0.4f });
        foreach (var s in new[] { clipped, free })
        {
            s.StrokeTo(20, 30);
            s.StrokeTo(40, 32);
            s.Airbrush();
        }
        for (int x = 10; x < 50; x++)
            Assert.Equal(x < 30 ? free.CoverageAt(x, 31) : 0f, clipped.CoverageAt(x, 31), 5);
    }

    // ---- Tip and pressure ----------------------------------------------------------------------------

    [Fact]
    public void Roundness_and_angle_make_an_elliptical_tip()
    {
        var flat = Stroke(new BrushSettings(20, 1f, 1f) { Roundness = 0.3f });
        flat.StrokeTo(30, 30);
        Assert.Equal(1f, flat.CoverageAt(37, 30)); // along the long axis (radius 10)
        Assert.Equal(0f, flat.CoverageAt(30, 36)); // across it (radius 3)
        var turned = Stroke(new BrushSettings(20, 1f, 1f) { Roundness = 0.3f, Angle = 90 });
        turned.StrokeTo(30, 30);
        Assert.Equal(1f, turned.CoverageAt(30, 22)); // now vertical
        Assert.Equal(0f, turned.CoverageAt(36, 30));
    }

    [Fact]
    public void Spacing_sets_the_distance_between_dabs()
    {
        var sparse = Stroke(new BrushSettings(4, 1f, 1f) { SpacingPercent = 400 });
        sparse.StrokeTo(10, 30);
        sparse.StrokeTo(50, 30);
        Assert.Equal(1f, sparse.CoverageAt(10, 30));
        Assert.Equal(0f, sparse.CoverageAt(18, 30)); // between dabs 16 px apart
        Assert.Equal(1f, sparse.CoverageAt(26, 30));
    }

    [Fact]
    public void Pressure_scales_size_and_opacity()
    {
        var size = Stroke(new BrushSettings(20, 1f, 1f) { PressureSize = true });
        size.StrokeTo(30, 30, 0.3f);
        Assert.Equal(1f, size.CoverageAt(30, 30));
        Assert.Equal(0f, size.CoverageAt(36, 30)); // radius 3 at 30% pressure
        var opacity = Stroke(new BrushSettings(20, 1f, 1f) { PressureOpacity = true });
        opacity.StrokeTo(30, 30, 0.25f);
        Assert.Equal(0.25f, opacity.CoverageAt(30, 30), 4);
        opacity.StrokeTo(30.2f, 30, 0.25f);
        for (int i = 0; i < 5; i++) opacity.Airbrush();
        Assert.Equal(0.25f, opacity.CoverageAt(30, 30), 4); // light pressure caps the stroke there
        var mouse = Stroke(new BrushSettings(20, 1f, 1f));
        mouse.StrokeTo(30, 30, 0.1f);
        Assert.Equal(1f, mouse.CoverageAt(30, 30)); // pressure ignored unless a pressure option is on
    }

    // ---- Paint modes ---------------------------------------------------------------------------------

    private static PixelLayer Half(float r, float g, float b)
    {
        // Left half opaque (r, g, b), right half transparent.
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(60, 60, 8)).ToArray();
        for (int y = 0; y < 60; y++)
            for (int x = 0; x < 30; x++)
            {
                int i = y * 60 + x;
                (planes[0].Data[i], planes[1].Data[i], planes[2].Data[i], planes[3].Data[i]) =
                    ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), 255);
            }
        return new PixelLayer { Bounds = Canvas, Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) };
    }

    private static (float R, float G, float B, float A) Paint(PaintMode mode, PixelLayer layer, RgbColor color, float opacity, int x, int y)
    {
        var stroke = new PaintStroke(layer, new BrushSettings(80, 1f, opacity) { Mode = mode }, color, false, Canvas);
        stroke.StrokeTo(30, 30);
        var (px, b) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        int i = (y - b.Top) * b.Width + (x - b.Left);
        return (px!.ColorPlanes[0].GetNormalized(i), px.ColorPlanes[1].GetNormalized(i), px.ColorPlanes[2].GetNormalized(i), px.Alpha!.GetNormalized(i));
    }

    [Fact]
    public void Behind_paints_only_where_the_layer_is_transparent()
    {
        var layer = Half(0.2f, 0.4f, 0.6f);
        var opaque = Paint(PaintMode.Behind, layer, new RgbColor(1, 0, 0), 1f, 10, 30);
        Assert.Equal((0.2f, 0.4f, 0.6f, 1f), (MathF.Round(opaque.R, 2), MathF.Round(opaque.G, 2), MathF.Round(opaque.B, 2), opaque.A));
        var empty = Paint(PaintMode.Behind, layer, new RgbColor(1, 0, 0), 0.5f, 50, 30);
        Assert.Equal(1f, empty.R);
        Assert.Equal(0.5f, empty.A, 2);
    }

    [Fact]
    public void Clear_removes_alpha_like_an_eraser_and_does_not_grow_the_layer()
    {
        var layer = Half(0.2f, 0.4f, 0.6f);
        var stroke = new PaintStroke(layer, new BrushSettings(80, 1f, 0.75f) { Mode = PaintMode.Clear }, new RgbColor(1, 0, 0), false, Canvas);
        stroke.StrokeTo(30, 30);
        var (px, b) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        Assert.Equal(Canvas, b);
        Assert.Equal(0.25f, px!.Alpha!.GetNormalized(30 * 60 + 10), 2);
        Assert.Equal(0.2f, px.ColorPlanes[0].GetNormalized(30 * 60 + 10), 2); // colors untouched
    }

    [Theory]
    [InlineData(PaintMode.Multiply, 0.5f * 0.2f, 0.5f * 0.4f, 0.5f * 0.6f)]
    [InlineData(PaintMode.Screen, 0.5f + 0.2f - 0.1f, 0.5f + 0.4f - 0.2f, 0.5f + 0.6f - 0.3f)]
    [InlineData(PaintMode.Darken, 0.2f, 0.4f, 0.5f)]
    [InlineData(PaintMode.Lighten, 0.5f, 0.5f, 0.6f)]
    [InlineData(PaintMode.Difference, 0.3f, 0.1f, 0.1f)]
    public void Blend_modes_combine_with_opaque_pixels_and_paint_plainly_on_transparent_ones(PaintMode mode, float r, float g, float b)
    {
        var layer = Half(0.2f, 0.4f, 0.6f);
        var gray = new RgbColor(0.5f, 0.5f, 0.5f);
        var over = Paint(mode, layer, gray, 1f, 10, 30);
        Assert.Equal(r, over.R, 1.5f / 255);
        Assert.Equal(g, over.G, 1.5f / 255);
        Assert.Equal(b, over.B, 1.5f / 255);
        var bare = Paint(mode, layer, gray, 1f, 50, 30);
        Assert.Equal((0.5f, 1f), (MathF.Round(bare.R, 2), bare.A));
    }

    [Fact]
    public void Color_and_luminosity_swap_hue_and_brightness()
    {
        var layer = Half(0.2f, 0.4f, 0.6f);
        var red = new RgbColor(1, 0, 0);
        var color = Paint(PaintMode.Color, layer, red, 1f, 10, 30);
        float lum = 0.3f * 0.2f + 0.59f * 0.4f + 0.11f * 0.6f;
        Assert.Equal(lum, 0.3f * color.R + 0.59f * color.G + 0.11f * color.B, 0.01f); // keeps the layer's brightness
        Assert.True(color.R > color.G && color.R > color.B, "takes the paint's hue");
        var luminosity = Paint(PaintMode.Luminosity, layer, new RgbColor(0.9f, 0.9f, 0.9f), 1f, 10, 30);
        Assert.Equal(0.9f, 0.3f * luminosity.R + 0.59f * luminosity.G + 0.11f * luminosity.B, 0.01f);
        Assert.True(luminosity.B > luminosity.R, "keeps the layer's hue");
    }

    [Fact]
    public void Dissolve_paints_whole_pixels_with_the_opacity_as_probability()
    {
        var layer = Half(0f, 0f, 0f);
        var stroke = new PaintStroke(layer, new BrushSettings(80, 1f, 0.3f) { Mode = PaintMode.Dissolve }, new RgbColor(1, 1, 1), false, Canvas);
        stroke.StrokeTo(30, 30);
        var (px, b) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        int painted = 0, total = 0;
        for (int y = 10; y < 50; y++)
            for (int x = 5; x < 25; x++)
            {
                float v = px!.ColorPlanes[0].GetNormalized((y - b.Top) * b.Width + x - b.Left);
                Assert.True(v is 0f or 1f);
                if (v == 1f) painted++;
                total++;
            }
        Assert.InRange(painted / (double)total, 0.22, 0.38);
    }

    // ---- Clone source transforms and filtered sampling -----------------------------------------------

    /// <summary>R encodes x/255, G encodes y/255.</summary>
    private static PixelSource Coordinates(int w = 200, int h = 200)
    {
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                (px[(y * w + x) * 4], px[(y * w + x) * 4 + 1], px[(y * w + x) * 4 + 2], px[(y * w + x) * 4 + 3]) = (x / 255f, y / 255f, 0.5f, 1f);
        return PixelSource.FromFloats(px, 3, PixelRect.FromSize(w, h));
    }

    [Fact]
    public void An_identity_clone_source_reads_exact_pixels_at_the_offset()
    {
        var source = new CloneSource(30, -10, Coordinates(), SourceTransform.Identity, 50.5f, 60.5f);
        Span<float> c = stackalloc float[3];
        source.Read(100, 100, c, 3);
        Assert.Equal((70 / 255f, 110 / 255f), (c[0], c[1]));
    }

    [Fact]
    public void Scaled_and_rotated_sources_map_about_the_anchor()
    {
        // Source point (50, 60); painting starts 40 px right of it. At 200% the painted pixel 10 px right of the start
        // shows the source 5 px right of the source point.
        var twice = new CloneSource(40, 0, Coordinates(), new SourceTransform(2f, 2f, 0f), 50.5f, 60.5f);
        Span<float> c = stackalloc float[3];
        twice.Read(100, 60, c, 3);
        Assert.Equal(55f, c[0] * 255, 0.51f);
        Assert.Equal(60f, c[1] * 255, 0.51f);
        // 90° counter-clockwise: painting 10 px right of the start shows what was 10 px below the source point
        // (the source turned left puts its "down" on the right).
        var turned = new CloneSource(40, 0, Coordinates(), new SourceTransform(1f, 1f, 90f), 50.5f, 60.5f);
        turned.Read(100, 60, c, 3);
        Assert.Equal(50f, c[0] * 255, 0.51f);
        Assert.Equal(70f, c[1] * 255, 0.51f);
        var (sx, sy) = turned.SourcePoint(100.5f, 60.5f);
        Assert.Equal((50.5f, 70.5f), (MathF.Round(sx, 3), MathF.Round(sy, 3)));
        // A negative width flips horizontally.
        var flipped = new CloneSource(40, 0, Coordinates(), new SourceTransform(-1f, 1f, 0f), 50.5f, 60.5f);
        flipped.Read(100, 60, c, 3);
        Assert.Equal(40f, c[0] * 255, 0.51f);
    }

    [Fact]
    public void Placed_source_heals_with_a_transformed_texture()
    {
        var image = Coordinates();
        var source = new CloneSource(40, 0, image, new SourceTransform(1f, 1f, 90f), 50.5f, 60.5f);
        var placed = source.Placed();
        Span<float> a = stackalloc float[3], b = stackalloc float[3];
        placed.Read(100, 60, a);
        source.Read(100, 60, b, 3);
        Assert.True(a.SequenceEqual(b));
    }

    [Fact]
    public void Area_reads_average_the_block_instead_of_point_sampling()
    {
        // A one-pixel checkerboard: point samples alias to pure black or white; a 4×4 area is mid-gray.
        const int w = 64;
        var px = new float[w * w * 4];
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                float v = (x + y) % 2;
                (px[(y * w + x) * 4], px[(y * w + x) * 4 + 1], px[(y * w + x) * 4 + 2], px[(y * w + x) * 4 + 3]) = (v, v, v, 1f);
            }
        var source = new CloneSource(3, 1, PixelSource.FromFloats(px, 3, PixelRect.FromSize(w, w)));
        Span<float> c = stackalloc float[3];
        for (int y = 8; y < 40; y += 4)
            for (int x = 8; x < 40; x += 4)
            {
                float alpha = source.ReadArea(x, y, 4, c, 3);
                Assert.Equal(1f, alpha, 3);
                Assert.Equal(0.5f, c[0], 0.02f);
            }
    }

    [Fact]
    public void Aligner_offset_can_be_typed()
    {
        var aligner = new CloneAligner();
        aligner.SetOffset(5, 5);
        Assert.Null(aligner.Offset); // no source point yet
        aligner.SetSource(10, 20);
        aligner.SetOffset(30, -4);
        Assert.Equal((30, -4), aligner.BeginStroke(100, 100, aligned: true));
    }
}
