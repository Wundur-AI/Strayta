using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

/// <summary>Synthetic images for the color-based selection tools.</summary>
internal static class Synthetic
{
    /// <summary>An opaque image from a color function (straight RGB).</summary>
    public static SampleImage Image(int w, int h, Func<int, int, (int R, int G, int B)> color)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = color(x, y);
                int i = (y * w + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), 255);
            }
        return SampleImage.FromStraightRgba(rgba, w, h);
    }

    public static PixelLayer Layer(PixelRect bounds, Func<int, int, (byte R, byte G, byte B, byte A)> color)
    {
        int w = bounds.Width, h = bounds.Height;
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, 8)).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = color(bounds.Left + x, bounds.Top + y);
                int i = y * w + x;
                (planes[0].Data[i], planes[1].Data[i], planes[2].Data[i], planes[3].Data[i]) = (r, g, b, a);
            }
        return new PixelLayer { Bounds = bounds, Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) };
    }

    /// <summary>Deterministic noise in [-amplitude, amplitude].</summary>
    public static int Noise(int x, int y, int channel, int amplitude)
    {
        uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(channel * 83492791);
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return (int)(h % (uint)(2 * amplitude + 1)) - amplitude;
    }

    public static int Count(SelectionMask? s, PixelRect area, byte min = 128)
    {
        int n = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (s is not null && s.CoverageAt(x, y) >= min) n++;
        return n;
    }

    /// <summary>Intersection over union of a selection (at 50%) with a predicate.</summary>
    public static double IoU(SelectionMask? s, PixelRect area, Func<int, int, bool> truth)
    {
        int both = 0, either = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                bool a = s is not null && s.CoverageAt(x, y) >= 128, b = truth(x, y);
                if (a && b) both++;
                if (a || b) either++;
            }
        return either == 0 ? 1 : (double)both / either;
    }
}

public class MagicWandTests
{
    private static readonly MagicWandOptions Hard = new(Tolerance: 32, AntiAlias: false, Contiguous: true);

    [Fact]
    public void Tolerance_is_compared_per_channel_and_inclusive()
    {
        // A horizontal ramp: column x has value x in every channel.
        var image = Synthetic.Image(100, 20, (x, _) => (x, x, x));
        var s = MagicWand.Select(image, 50, 10, Hard with { Tolerance = 10 })!;
        Assert.Equal(new PixelRect(40, 0, 61, 20), s.Bounds);
        Assert.True(s.IsRectangular);
        Assert.Equal(new PixelRect(50, 0, 51, 20), MagicWand.Select(image, 50, 10, Hard with { Tolerance = 0 })!.Bounds);
        Assert.Equal(image.Bounds, MagicWand.Select(image, 50, 10, Hard with { Tolerance = 255 })!.Bounds);

        // One channel out of range is enough to exclude a pixel.
        var channels = Synthetic.Image(10, 10, (x, _) => (100, x < 5 ? 100 : 140, 100));
        Assert.Equal(new PixelRect(0, 0, 5, 10), MagicWand.Select(channels, 0, 0, Hard with { Tolerance = 39 })!.Bounds);
        Assert.Equal(channels.Bounds, MagicWand.Select(channels, 0, 0, Hard with { Tolerance = 40 })!.Bounds);
    }

    [Fact]
    public void Contiguous_selects_the_connected_region_and_non_contiguous_every_match()
    {
        // Two red squares on white, and a red ring around the second one's corner that touches nothing.
        static bool Red(int x, int y) => (x is >= 10 and < 30 && y is >= 10 and < 30) || (x is >= 50 and < 70 && y is >= 10 and < 30);
        var image = Synthetic.Image(80, 40, (x, y) => Red(x, y) ? (220, 20, 20) : (255, 255, 255));
        var one = MagicWand.Select(image, 15, 15, Hard)!;
        Assert.Equal(new PixelRect(10, 10, 30, 30), one.Bounds);
        Assert.True(one.IsRectangular);

        var all = MagicWand.Select(image, 15, 15, Hard with { Contiguous = false })!;
        Assert.Equal(new PixelRect(10, 10, 70, 30), all.Bounds);
        Assert.Equal(800, Synthetic.Count(all, image.Bounds));

        // The white background around both squares is one connected region, holes excluded.
        var background = MagicWand.Select(image, 0, 0, Hard)!;
        Assert.Equal(80 * 40 - 800, Synthetic.Count(background, image.Bounds));
        Assert.Equal(0, background.CoverageAt(20, 20));
    }

    [Fact]
    public void Diagonal_neighbors_are_not_connected()
    {
        // A checkerboard: 4-connected fill from one black pixel selects only that pixel.
        var image = Synthetic.Image(8, 8, (x, y) => (x + y) % 2 == 0 ? (0, 0, 0) : (255, 255, 255));
        Assert.Equal(1, Synthetic.Count(MagicWand.Select(image, 2, 2, Hard), image.Bounds));
        Assert.Equal(32, Synthetic.Count(MagicWand.Select(image, 2, 2, Hard with { Contiguous = false }), image.Bounds));
    }

    [Fact]
    public void Anti_aliasing_softens_the_edge_without_moving_it()
    {
        var image = Synthetic.Image(60, 60, (x, y) => (x - 30) * (x - 30) + (y - 30) * (y - 30) < 400 ? (0, 0, 200) : (255, 255, 255));
        var hard = MagicWand.Select(image, 30, 30, Hard)!;
        var soft = MagicWand.Select(image, 30, 30, Hard with { AntiAlias = true })!;

        Assert.Equal(Synthetic.Count(hard, image.Bounds), Synthetic.Count(soft, image.Bounds)); // same 50% outline
        Assert.Equal(255, soft.CoverageAt(30, 30));
        int partial = 0;
        double hardArea = 0, softArea = 0;
        for (int y = 0; y < 60; y++)
            for (int x = 0; x < 60; x++)
            {
                if (soft.CoverageAt(x, y) is > 0 and < 255) partial++;
                hardArea += hard.CoverageAt(x, y) / 255.0;
                softArea += soft.CoverageAt(x, y) / 255.0;
            }
        Assert.True(partial > 100, $"{partial} partially selected pixels");
        Assert.Equal(hardArea, softArea, hardArea * 0.02);
        // Edge pixels just inside are mostly selected, those just outside a little.
        Assert.InRange(soft.CoverageAt(11, 30), 128, 254);
        Assert.InRange(soft.CoverageAt(10, 30), 1, 127);
        // A selection filling the image up to its border stays solid there.
        var full = MagicWand.Select(Synthetic.Image(10, 10, (_, _) => (1, 2, 3)), 5, 5, Hard with { AntiAlias = true })!;
        Assert.True(full.IsRectangular);
    }

    [Fact]
    public void Current_layer_sampling_sees_its_transparency_and_sample_all_sees_the_composite()
    {
        const int W = 60, H = 40;
        // A layer with an opaque red square and transparent pixels holding arbitrary color data.
        var layer = Synthetic.Layer(new PixelRect(10, 10, 40, 30), (x, y) =>
            x is >= 20 and < 30 ? ((byte)220, (byte)20, (byte)20, (byte)255) : ((byte)(x * 7), (byte)(y * 5), (byte)90, (byte)0));
        var own = SampleImage.FromLayer(layer, W, H);

        // Transparent pixels all match each other, inside the layer bounds and outside them.
        var empty = MagicWand.Select(own, 0, 0, Hard)!;
        Assert.Equal(W * H - 200, Synthetic.Count(empty, own.Bounds));
        Assert.Equal(255, empty.CoverageAt(15, 15));
        Assert.Equal(0, empty.CoverageAt(25, 15));
        Assert.Equal(new PixelRect(20, 10, 30, 30), MagicWand.Select(own, 25, 15, Hard)!.Bounds);

        // The flattened document: the same square over a blue left half and a white right half.
        var composite = Synthetic.Image(W, H, (x, y) => x is >= 20 and < 30 && y is >= 10 and < 30 ? (220, 20, 20) : x < 30 ? (0, 0, 255) : (255, 255, 255));
        var blue = MagicWand.Select(composite, 0, 0, Hard)!;
        Assert.Equal(30 * H - 200, Synthetic.Count(blue, composite.Bounds));
        Assert.Equal(0, blue.CoverageAt(45, 5));
    }

    [Fact]
    public void Partially_transparent_pixels_compare_premultiplied()
    {
        var layer = Synthetic.Layer(new PixelRect(0, 0, 10, 1), (x, _) => ((byte)200, (byte)200, (byte)200, (byte)(x < 5 ? 255 : 128)));
        var image = SampleImage.FromLayer(layer, 10, 1);
        Assert.Equal(new PixelRect(0, 0, 5, 1), MagicWand.Select(image, 0, 0, Hard)!.Bounds);
    }

    [Fact]
    public void Wand_results_combine_with_the_selection_modifiers()
    {
        static bool Red(int x, int y) => x is >= 10 and < 30 && y is >= 10 and < 30;
        static bool Green(int x, int y) => x is >= 50 and < 70 && y is >= 10 and < 30;
        var image = Synthetic.Image(80, 40, (x, y) => Red(x, y) ? (220, 20, 20) : Green(x, y) ? (20, 200, 20) : (255, 255, 255));
        var red = MagicWand.Select(image, 15, 15, Hard);
        var both = SelectionMask.Combine(red, MagicWand.Select(image, 55, 15, Hard), SelectionMode.Add)!;
        Assert.Equal(800, Synthetic.Count(both, image.Bounds));
        var back = SelectionMask.Combine(both, MagicWand.Select(image, 55, 15, Hard), SelectionMode.Subtract)!;
        Assert.Equal(new PixelRect(10, 10, 30, 30), back.Bounds);
        // Intersecting the whole canvas minus the squares with the red square leaves nothing.
        Assert.Null(SelectionMask.Combine(red, MagicWand.Select(image, 0, 0, Hard), SelectionMode.Intersect));
    }

    [Fact]
    public void Clicks_outside_the_image_select_nothing()
    {
        var image = Synthetic.Image(10, 10, (_, _) => (0, 0, 0));
        Assert.Null(MagicWand.Select(image, -1, 5, Hard));
        Assert.Null(MagicWand.Select(image, 5, 10, Hard));
    }

    [Fact]
    public void Large_images_fill_quickly()
    {
        // 4000 × 3000 with a big flat area: the worst case for a contiguous fill.
        const int W = 4000, H = 3000;
        var rgba = new byte[W * H * 4];
        Array.Fill(rgba, (byte)200);
        var image = new SampleImage(W, H, rgba);
        MagicWand.Select(image, 10, 10, new MagicWandOptions()); // warm up
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = MagicWand.Select(image, 10, 10, new MagicWandOptions())!;
        Assert.True(s.IsRectangular);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"{sw.ElapsedMilliseconds} ms");
    }
}
