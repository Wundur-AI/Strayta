using Strayta.Core;
using Strayta.Rendering.Filters;

namespace Strayta.Rendering.Tests;

/// <summary>The Channels panel's views over a rendered image, and Image › Adjustments as a filter.</summary>
public class ChannelViewTests
{
    private static byte[] Pixel(byte r, byte g, byte b, byte a = 255) => [r, g, b, a];

    private static Plane Gray(byte v) => new(1, 1, 8, [v]);

    [Fact]
    public void One_color_channel_shows_in_gray_and_two_in_color()
    {
        var rgba = Pixel(200, 100, 50);
        new ChannelView([false, true, false], []).Apply(rgba, 1, 1);
        Assert.Equal(Pixel(100, 100, 100), rgba);

        rgba = Pixel(200, 100, 50);
        new ChannelView([true, false, true], []).Apply(rgba, 1, 1);
        Assert.Equal(Pixel(200, 0, 50), rgba);

        rgba = Pixel(0, 0, 0, 0); // transparent shows as white in channel views
        new ChannelView([true, false, false], []).Apply(rgba, 1, 1);
        Assert.Equal(Pixel(255, 255, 255), rgba);
    }

    [Fact]
    public void Saved_selections_tint_masked_areas_and_show_alone_in_gray()
    {
        var red = new RgbColor(1, 0, 0);
        var rgba = Pixel(0, 0, 255);
        new ChannelView([true, true, true], [new ChannelOverlay(new PlaneSampler(Gray(0), 1), red, 0.5f, ChannelOverlayKind.Mask)]).Apply(rgba, 1, 1);
        Assert.Equal(Pixel(128, 0, 128), rgba);

        rgba = Pixel(0, 0, 255);
        new ChannelView([true, true, true], [new ChannelOverlay(new PlaneSampler(Gray(255), 1), red, 0.5f, ChannelOverlayKind.Mask)]).Apply(rgba, 1, 1);
        Assert.Equal(Pixel(0, 0, 255), rgba); // selected: untouched

        rgba = Pixel(0, 0, 255);
        new ChannelView([false, false, false], [new ChannelOverlay(new PlaneSampler(Gray(77), 1), red, 0.5f, ChannelOverlayKind.Mask)]).Apply(rgba, 1, 1);
        Assert.Equal(Pixel(77, 77, 77), rgba);
    }

    [Fact]
    public void Spot_ink_prints_over_the_image()
    {
        var rgba = Pixel(255, 255, 255);
        new ChannelView([true, true, true], [new ChannelOverlay(new PlaneSampler(Gray(0), 1), new RgbColor(0, 0.5f, 1), 1f, ChannelOverlayKind.Spot)]).Apply(rgba, 1, 1);
        Assert.Equal(Pixel(0, 128, 255), rgba);
        rgba = Pixel(100, 100, 100);
        new ChannelView([true, true, true], [new ChannelOverlay(new PlaneSampler(Gray(255), 1), new RgbColor(0, 0.5f, 1), 1f, ChannelOverlayKind.Spot)]).Apply(rgba, 1, 1);
        Assert.Equal(Pixel(100, 100, 100), rgba); // no ink
    }

    [Fact]
    public void Adjustments_apply_as_filters_to_layers_and_gray_masks()
    {
        var raster = new Raster(ColorMode.Rgb, [new Plane(1, 1, 8, [200]), new Plane(1, 1, 8, [100]), new Plane(1, 1, 8, [0])], new Plane(1, 1, 8, [128]));
        var inverted = FilterEngine.Apply(raster, new AdjustmentFilter(new InvertAdjustment(), "Invert"));
        Assert.InRange(inverted.ColorPlanes[0].Data[0], 54, 56);
        Assert.InRange(inverted.ColorPlanes[2].Data[0], 254, 255);
        Assert.Equal(128, inverted.Alpha!.Data[0]); // transparency kept

        var mask = new LayerMask { Bounds = new PixelRect(0, 0, 1, 1), Pixels = new Plane(1, 1, 8, [30]), DefaultColor = 0 };
        var result = FilterEngine.ApplyToMask(mask, new AdjustmentFilter(new InvertAdjustment(), "Invert"), new FilterScope(new PixelRect(0, 0, 1, 1)), 8);
        Assert.Equal(225, result.Pixels!.Data[0]);
    }
}
