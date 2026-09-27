using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

public class PaintBucketTests
{
    private static readonly PixelRect Canvas = PixelRect.FromSize(30, 20);

    // Two white areas split by a black bar at x = 10..11; a light gray square at 20..24 inside the right one.
    private static (int R, int G, int B) Scene(int x, int y) =>
        x is 10 or 11 ? (0, 0, 0) : x is >= 20 and < 25 && y is >= 5 and < 10 ? (240, 240, 240) : (255, 255, 255);

    private static int Count(SelectionMask? m) =>
        m is null ? 0 : Enumerable.Range(0, Canvas.Height).Sum(y => Enumerable.Range(0, Canvas.Width).Count(x => m.CoverageAt(x, y) >= 128));

    [Fact]
    public void Contiguous_fills_stop_at_edges_and_non_contiguous_fills_reach_everything_similar()
    {
        var image = Synthetic.Image(30, 20, Scene);
        var tight = new MagicWandOptions(Tolerance: 5, AntiAlias: false, Contiguous: true);
        var left = PaintBucket.Region(image, 2, 2, tight, null);
        Assert.Equal(10 * 20, Count(left));
        Assert.Equal(0, left!.CoverageAt(15, 2));

        var everywhere = PaintBucket.Region(image, 2, 2, tight with { Contiguous = false }, null);
        Assert.Equal(30 * 20 - 2 * 20 - 25, Count(everywhere)); // all white, not the bar or the gray square

        var loose = PaintBucket.Region(image, 15, 2, tight with { Tolerance = 32 }, null);
        Assert.Equal(18 * 20, Count(loose)); // the gray square is within tolerance
    }

    [Fact]
    public void The_selection_limits_the_fill_and_a_click_outside_it_does_nothing()
    {
        var image = Synthetic.Image(30, 20, Scene);
        var options = new MagicWandOptions(Tolerance: 5, AntiAlias: false, Contiguous: true);
        var selection = SelectionMask.Rectangle(new PixelRect(0, 0, 5, 20), Canvas);
        Assert.Equal(5 * 20, Count(PaintBucket.Region(image, 2, 2, options, selection)));
        Assert.Null(PaintBucket.Region(image, 15, 2, options, selection));
    }

    [Fact]
    public void Filling_paints_the_region_with_the_color_and_leaves_the_rest()
    {
        var image = Synthetic.Image(30, 20, Scene);
        var layer = Synthetic.Layer(Canvas, (x, y) => { var (r, g, b) = Scene(x, y); return ((byte)r, (byte)g, (byte)b, 255); });
        var result = PaintBucket.Fill(layer, image, 2, 2, new MagicWandOptions(5, false, true), null, new RgbColor(1, 0, 0), ColorMode.Rgb, 8);
        Assert.NotNull(result);
        var (px, bounds) = result!.Value;
        Assert.Equal(Canvas, bounds);
        int At(int x, int y) => y * bounds.Width + x;
        Assert.Equal((255, 0), (px!.ColorPlanes[0].Data[At(3, 3)], px.ColorPlanes[1].Data[At(3, 3)]));
        Assert.Equal((255, 255), (px.ColorPlanes[0].Data[At(15, 3)], px.ColorPlanes[1].Data[At(15, 3)]));
        Assert.Equal(0, px.ColorPlanes[0].Data[At(10, 3)]);
    }

    [Fact]
    public void An_empty_layer_is_filled_wherever_it_is_transparent()
    {
        var image = SampleImage.FromPixels(null, PixelRect.Empty, 30, 20);
        var result = PaintBucket.Fill(new PixelLayer(), image, 5, 5, new MagicWandOptions(), null, new RgbColor(0, 1, 0), ColorMode.Rgb, 8);
        Assert.Equal(Canvas, result!.Value.Bounds);
        Assert.All(result.Value.Pixels!.Alpha!.Data, a => Assert.Equal(255, a));
    }

    [Fact]
    public void The_eyedropper_averages_by_opacity_and_ignores_transparency()
    {
        var image = Synthetic.Image(30, 20, (x, _) => x < 15 ? (200, 0, 0) : (0, 0, 100));
        Assert.Equal(((byte)200, (byte)0, (byte)0), ColorSampler.Average(image, 3, 3, 1));
        Assert.Equal(((byte)200, (byte)0, (byte)0), ColorSampler.Average(image, 0, 0, 5)); // clipped at the corner
        var mixed = ColorSampler.Average(image, 15, 10, 2 * 1 + 1)!.Value;             // columns 14..16: 1 red, 2 blue
        Assert.Equal((67, 0, 67), ((int)mixed.R, (int)mixed.G, (int)mixed.B));

        var layer = Synthetic.Layer(Canvas, (x, _) => x < 15 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)10, (byte)200, (byte)30, (byte)255));
        var sample = SampleImage.FromLayer(layer, 30, 20);
        Assert.Null(ColorSampler.Average(sample, 3, 3, 5));
        Assert.Equal(((byte)10, (byte)200, (byte)30), ColorSampler.Average(sample, 14, 3, 3)); // transparent neighbors do not darken it
    }
}
