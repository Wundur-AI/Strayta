using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

public class FillAndStrokeTests
{
    private static readonly PixelRect Canvas = PixelRect.FromSize(60, 40);

    private static PixelLayer Opaque(byte value)
    {
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(Canvas.Width, Canvas.Height, 8)).ToArray();
        foreach (var p in planes) p.Data.AsSpan().Fill(value);
        return new PixelLayer { Name = "Background", Bounds = Canvas, Pixels = new Raster(ColorMode.Rgb, planes, null) };
    }

    private static Pattern TwoByTwo()
    {
        // Red, green / blue, transparent.
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 0, 0];
        return Pattern.FromRgba("test-id", "Test", 2, 2, rgba, withAlpha: true);
    }

    private static (byte R, byte G, byte B, byte A) Pixel(Raster r, PixelRect bounds, int x, int y)
    {
        int i = (y - bounds.Top) * bounds.Width + (x - bounds.Left);
        return (r.ColorPlanes[0].Data[i], r.ColorPlanes[1].Data[i], r.ColorPlanes[2].Data[i], r.Alpha?.Data[i] ?? (byte)255);
    }

    [Fact]
    public void Patterns_tile_from_the_document_origin()
    {
        var p = TwoByTwo();
        Assert.Equal((1f, 0f, 0f, 1f), p.At(0, 0));
        Assert.Equal((1f, 0f, 0f, 1f), p.At(4, -2));
        Assert.Equal((0f, 1f, 0f, 1f), p.At(-1, 0));
        Assert.Equal(0f, p.At(3, 3).A);
    }

    [Fact]
    public void Pattern_fill_paints_the_tile_through_the_selection()
    {
        var layer = new PixelLayer { Name = "Layer" };
        var selection = SelectionMask.Rectangle(new PixelRect(10, 10, 14, 14), Canvas)!;
        var (px, bounds) = FillPainter.Fill(layer, selection, new FillOptions(new FillSource.Tiled(TwoByTwo())), ColorMode.Rgb, 8);
        Assert.Equal(new PixelRect(10, 10, 14, 14), bounds);
        Assert.Equal((255, 0, 0, 255), Pixel(px!, bounds, 10, 10));
        Assert.Equal((0, 255, 0, 255), Pixel(px!, bounds, 11, 10));
        Assert.Equal((0, 0, 255, 255), Pixel(px!, bounds, 12, 11));
        Assert.Equal(0, Pixel(px!, bounds, 13, 13).A);
    }

    [Fact]
    public void Fill_opacity_and_mode_apply()
    {
        var layer = Opaque(200);
        var all = SelectionMask.All(Canvas);
        var (half, b) = FillPainter.Fill(layer, all, new FillOptions(new FillSource.Color(new RgbColor(0, 0, 0))) { Opacity = 0.5f }, ColorMode.Rgb, 8);
        Assert.Equal((100, 100, 100, 255), Pixel(half!, b, 5, 5));
        Assert.Null(half!.Alpha); // a Background stays opaque

        var (mult, _) = FillPainter.Fill(layer, all, new FillOptions(new FillSource.Color(new RgbColor(0.5f, 1, 1))) { Mode = PaintMode.Multiply }, ColorMode.Rgb, 8);
        Assert.Equal((100, 200, 200, 255), Pixel(mult!, b, 5, 5));
    }

    [Fact]
    public void Preserve_transparency_keeps_alpha_and_never_grows_the_layer()
    {
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(4, 1, 8)).ToArray();
        planes[3].Data[0] = 255;
        planes[3].Data[1] = 128;
        var layer = new PixelLayer { Name = "L", Bounds = new PixelRect(0, 0, 4, 1), Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) };
        var fill = new FillOptions(new FillSource.Color(new RgbColor(1, 0, 0))) { PreserveTransparency = true };
        var (px, bounds) = FillPainter.Fill(layer, SelectionMask.All(Canvas), fill, ColorMode.Rgb, 8);
        Assert.Equal(layer.Bounds, bounds);
        Assert.Equal((255, 0, 0, 255), Pixel(px!, bounds, 0, 0));
        Assert.Equal((255, 0, 0, 128), Pixel(px!, bounds, 1, 0));
        Assert.Equal(0, Pixel(px!, bounds, 2, 0).A);
    }

    [Theory]
    [InlineData(StrokeLocation.Outside, 17, 19)]
    [InlineData(StrokeLocation.Inside, 20, 22)]
    [InlineData(StrokeLocation.Center, 19, 20)]
    public void Stroke_bands_sit_where_the_location_says(StrokeLocation location, int first, int last)
    {
        var selection = SelectionMask.Rectangle(new PixelRect(20, 10, 40, 30), Canvas)!;
        var band = SelectionStroke.Band(selection, location == StrokeLocation.Center ? 2 : 3, location, Canvas)!;
        for (int x = 14; x < 26; x++)
        {
            byte c = band.CoverageAt(x, 20);
            if (x >= first && x <= last) Assert.True(c == 255, $"{location}: x={x} coverage {c}");
            else Assert.True(c == 0, $"{location}: x={x} coverage {c}");
        }
        // The middle of the selection is never stroked.
        Assert.Equal(0, band.CoverageAt(30, 20));
    }

    [Fact]
    public void A_stroke_of_the_whole_canvas_runs_along_its_edges()
    {
        var band = SelectionStroke.Band(SelectionMask.All(Canvas), 2, StrokeLocation.Center, Canvas)!;
        Assert.Equal(255, band.CoverageAt(0, 20));
        Assert.Equal(0, band.CoverageAt(3, 20));
        Assert.Equal(255, band.CoverageAt(30, Canvas.Height - 1));
    }

    [Fact]
    public void Circular_strokes_are_anti_aliased_and_follow_the_outline()
    {
        var circle = SelectionMask.Ellipse(new PixelRect(10, 5, 40, 35), Canvas)!;
        var band = SelectionStroke.Band(circle, 4, StrokeLocation.Outside, Canvas)!;
        // Along the horizontal through the center the band is x in [6, 10).
        Assert.Equal(255, band.CoverageAt(8, 20));
        Assert.Equal(0, band.CoverageAt(12, 20));
        // Somewhere on the diagonal edge coverage is partial.
        bool partial = false;
        for (int x = 0; x < 60 && !partial; x++)
            for (int y = 0; y < 40 && !partial; y++)
                partial = band.CoverageAt(x, y) is > 20 and < 235;
        Assert.True(partial);
    }

    [Fact]
    public void Built_in_patterns_have_unique_ids_and_names()
    {
        var set = PatternLibrary.BuiltIn;
        Assert.True(set.Count >= 6);
        Assert.Equal(set.Count, set.Select(p => p.Id).Distinct().Count());
        Assert.Equal(set.Count, set.Select(p => p.Name).Distinct().Count());
        Assert.All(set, p => Assert.True(p.Width > 0 && p.Height > 0));
    }
}
