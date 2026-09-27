namespace Strayta.Core.Tests;

public class PixelRectTests
{
    [Fact]
    public void Intersect_returns_overlap_or_empty()
    {
        var a = new PixelRect(0, 0, 10, 10);
        Assert.Equal(new PixelRect(5, 5, 10, 10), a.Intersect(new PixelRect(5, 5, 20, 20)));
        Assert.True(a.Intersect(new PixelRect(10, 0, 20, 10)).IsEmpty);
    }

    [Fact]
    public void Inverted_rects_have_zero_size()
    {
        var r = new PixelRect(5, 5, 2, 2);
        Assert.Equal(0, r.Width);
        Assert.True(r.IsEmpty);
    }
}

public class PlaneTests
{
    [Fact]
    public void Rejects_data_of_wrong_length() =>
        Assert.Throws<ArgumentException>(() => new Plane(2, 2, 16, new byte[4]));

    [Fact]
    public void Normalizes_samples_by_depth()
    {
        var p16 = Plane.Create(1, 1, 16);
        p16.AsUInt16()[0] = 65535;
        Assert.Equal(1f, p16.GetNormalized(0));

        var p32 = Plane.Create(1, 1, 32);
        p32.AsSingle()[0] = 0.25f;
        Assert.Equal(0.25f, p32.GetNormalized(0));
    }
}

public class LayerGroupTests
{
    [Fact]
    public void A_layer_can_only_have_one_parent()
    {
        var a = new LayerGroup();
        var b = new LayerGroup();
        var layer = new PixelLayer();
        a.Add(layer);

        Assert.Throws<InvalidOperationException>(() => b.Add(layer));
        Assert.True(a.Remove(layer));
        b.Add(layer);
        Assert.Same(b, layer.Parent);
    }
}

public class RgbaConverterTests
{
    [Fact]
    public void Converts_inverted_cmyk()
    {
        // Stored CMYK is inverted: 255 = no ink. No ink at all is white.
        var planes = Enumerable.Range(0, 4).Select(_ => new Plane(1, 1, 8, [255])).ToArray();
        var rgba = RgbaConverter.ToRgba8(new Raster(ColorMode.Cmyk, planes, null));
        Assert.Equal([255, 255, 255, 255], rgba);
    }

    [Fact]
    public void Converts_lab_mid_gray()
    {
        // L=50, a=b=0 is roughly sRGB 119.
        var planes = new[] { new Plane(1, 1, 8, [128]), new Plane(1, 1, 8, [128]), new Plane(1, 1, 8, [128]) };
        var rgba = RgbaConverter.ToRgba8(new Raster(ColorMode.Lab, planes, null));
        Assert.InRange(rgba[0], 116, 122);
        Assert.InRange(rgba[1], 116, 122);
        Assert.InRange(rgba[2], 116, 122);
    }

    [Fact]
    public void Uses_palette_for_indexed()
    {
        var palette = new byte[768];
        palette[3 * 5] = 10; palette[3 * 5 + 1] = 20; palette[3 * 5 + 2] = 30;
        var rgba = RgbaConverter.ToRgba8(new Raster(ColorMode.Indexed, [new Plane(1, 1, 8, [5])], null), palette);
        Assert.Equal([10, 20, 30, 255], rgba);
    }
}
