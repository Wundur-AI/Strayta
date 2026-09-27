using Strayta.Core.Painting;

namespace Strayta.Core.Tests;

public class PaintingTests
{
    private static readonly PixelRect Canvas = new(0, 0, 40, 40);

    private static PaintStroke Stroke(PixelLayer layer, float size = 10, float hardness = 1f, float opacity = 1f, bool erase = false) =>
        new(layer, new BrushSettings(size, hardness, opacity), new RgbColor(1, 0, 0), erase, Canvas);

    [Fact]
    public void A_hard_dab_covers_its_disc_with_an_antialiased_edge()
    {
        var stroke = Stroke(new PixelLayer(), size: 10);
        stroke.StrokeTo(20, 20);

        Assert.Equal(1f, stroke.CoverageAt(20, 20));
        Assert.Equal(1f, stroke.CoverageAt(16, 20));   // 3.5 px from center, inside radius 5
        Assert.Equal(0f, stroke.CoverageAt(26, 20));   // 6.5 px out
        Assert.InRange(stroke.CoverageAt(24, 20), 0.5f, 1f); // edge pixel partially covered
    }

    [Fact]
    public void Soft_brushes_fall_off_toward_the_edge()
    {
        var stroke = Stroke(new PixelLayer(), size: 20, hardness: 0f);
        stroke.StrokeTo(20, 20);
        Assert.True(stroke.CoverageAt(20, 20) > stroke.CoverageAt(25, 20));
        Assert.True(stroke.CoverageAt(25, 20) > stroke.CoverageAt(28, 20));
    }

    [Fact]
    public void Strokes_interpolate_between_points_without_gaps()
    {
        var stroke = Stroke(new PixelLayer(), size: 4);
        stroke.StrokeTo(5, 20);
        stroke.StrokeTo(35, 20);
        for (int x = 5; x <= 35; x++) Assert.True(stroke.CoverageAt(x, 20) > 0.9f, $"gap at x={x}");
        Assert.Equal(new PixelRect(2, 17, 38, 23).Left, stroke.Bounds.Left);
    }

    [Fact]
    public void Baking_a_brush_stroke_onto_an_empty_layer_creates_pixels()
    {
        var layer = new PixelLayer();
        var stroke = Stroke(layer, size: 10, opacity: 0.5f);
        stroke.StrokeTo(20, 20);

        var (pixels, bounds) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);

        Assert.Equal(stroke.Bounds, bounds);
        int center = (20 - bounds.Top) * bounds.Width + (20 - bounds.Left);
        Assert.Equal(255, pixels!.ColorPlanes[0].Data[center]);
        Assert.Equal(128, pixels.Alpha!.Data[center]); // 50% opacity
    }

    [Fact]
    public void Baking_leaves_the_original_raster_untouched_and_the_eraser_removes_alpha()
    {
        Plane P(byte v) => new(40, 40, 8, Enumerable.Repeat(v, 1600).ToArray());
        var original = new Raster(ColorMode.Rgb, [P(0), P(0), P(255)], null);
        var layer = new PixelLayer { Bounds = Canvas, Pixels = original };
        var stroke = Stroke(layer, size: 10, erase: true);
        stroke.StrokeTo(20, 20);

        var (pixels, _) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);

        Assert.Null(original.Alpha);                        // the source is immutable
        Assert.Equal(0, pixels!.Alpha!.Data[20 * 40 + 20]); // erased
        Assert.Equal(255, pixels.Alpha.Data[0]);            // untouched corner stays opaque
    }
}
