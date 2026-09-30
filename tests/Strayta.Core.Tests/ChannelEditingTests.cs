using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

/// <summary>Painting with some color channels targeted, and painting a saved selection as a mask.</summary>
public class ChannelEditingTests
{
    private static Plane Filled(int w, int h, byte v) => new(w, h, 8, Enumerable.Repeat(v, w * h).ToArray());

    private static PixelLayer Red(int w, int h) => new()
    {
        Name = "Red",
        Bounds = new PixelRect(10, 10, 10 + w, 10 + h),
        Pixels = new Raster(ColorMode.Rgb, [Filled(w, h, 255), Filled(w, h, 0), Filled(w, h, 0)], Filled(w, h, 255)),
    };

    [Fact]
    public void A_stroke_with_only_green_targeted_changes_only_green_and_keeps_bounds_and_alpha()
    {
        var layer = Red(20, 20);
        var stroke = new PaintStroke(layer, new BrushSettings(30, 1f, 1f), new RgbColor(1, 1, 1), erase: false, PixelRect.FromSize(100, 100));
        stroke.StrokeTo(20, 20);
        var (baked, bakedBounds) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        Assert.NotEqual(layer.Bounds, bakedBounds); // the brush alone grows the layer

        var (pixels, bounds) = ChannelRestriction.Keep(layer.Pixels, layer.Bounds, baked, bakedBounds, [false, true, false]);
        Assert.Equal(layer.Bounds, bounds);
        Assert.Same(layer.Pixels!.ColorPlanes[0], pixels!.ColorPlanes[0]);
        Assert.Same(layer.Pixels.ColorPlanes[2], pixels.ColorPlanes[2]);
        Assert.Same(layer.Pixels.Alpha, pixels.Alpha);
        int center = 10 * 20 + 10; // document (20, 20)
        Assert.Equal(255, pixels.ColorPlanes[1].Data[center]);
        Assert.Equal(0, layer.Pixels.ColorPlanes[1].Data[center]); // the old raster is untouched
    }

    [Fact]
    public void Nothing_targeted_changed_returns_the_old_pixels()
    {
        var layer = Red(4, 4);
        var (pixels, _) = ChannelRestriction.Keep(layer.Pixels, layer.Bounds, layer.Pixels, layer.Bounds, [true, true, true]);
        Assert.Same(layer.Pixels, pixels);
        Assert.Equal((null, PixelRect.Empty), ChannelRestriction.Keep(null, PixelRect.Empty, layer.Pixels, layer.Bounds, [true, true, true]));
    }

    [Fact]
    public void A_saved_selection_is_painted_through_the_mask_tools()
    {
        var canvas = PixelRect.FromSize(50, 40);
        var channel = ChannelSelection.Solid(50, 40, 8, 1f);
        var owner = new LayerGroup { Mask = ChannelSelection.AsMask(channel) };
        var stroke = PaintStroke.ForMask(owner, new BrushSettings(10, 1f, 1f), 0f, canvas);
        stroke.StrokeTo(25, 20);
        var painted = ChannelSelection.FromMask(MaskBaker.Bake(owner.Mask!, stroke, 8), 50, 40, 8);
        Assert.Equal((50, 40), (painted.Width, painted.Height));
        Assert.Equal(0, painted.Data[20 * 50 + 25]);
        Assert.Equal(255, painted.Data[0]);
        Assert.Equal(255, channel.Data[20 * 50 + 25]);
        var selection = ChannelSelection.ToSelection(painted, canvas)!;
        Assert.Equal(0, selection.CoverageAt(25, 20));
        Assert.Equal(255, selection.CoverageAt(1, 1));
    }

    [Fact]
    public void A_mask_smaller_than_the_canvas_becomes_a_full_channel()
    {
        var mask = new LayerMask { Bounds = new PixelRect(2, 2, 4, 4), Pixels = Filled(2, 2, 128), DefaultColor = 255 };
        var plane = ChannelSelection.FromMask(mask, 6, 6, 8);
        Assert.Equal(255, plane.Data[0]);
        Assert.Equal(128, plane.Data[2 * 6 + 2]);
        Assert.Equal(0, ChannelSelection.Invert(plane).Data[0]);
    }
}
