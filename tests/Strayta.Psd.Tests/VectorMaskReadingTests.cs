using Strayta.Core;

namespace Strayta.Psd.Tests;

/// <summary>
/// Photoshop stores a fill (shape) layer's pixels already cut out by its vector mask, and the vector mask rasterized
/// as the user mask (mask flag bit 3). Reading marks such a mask as applied to the pixels, so renderers do not apply
/// its anti-aliased edge a second time; other masks keep multiplying.
/// </summary>
public class VectorMaskReadingTests
{
    private static PsdTestBuilder.Layer Layer(bool fill, byte maskFlags)
    {
        var layer = new PsdTestBuilder.Layer
        {
            Name = fill ? "Shape" : "Pixels", Left = 0, Top = 0, Right = 2, Bottom = 1,
            Channels = { [-1] = [128, 255], [0] = [10, 20], [1] = [30, 40], [2] = [50, 60], [-2] = [128, 255] },
            Mask = (Top: 0, Left: 0, Bottom: 1, Right: 2, Default: 0, Flags: maskFlags),
        };
        // The classifier only looks for the blocks; their contents do not matter here.
        if (fill) layer.ExtraBlocks.Add(("SoCo", new byte[4]));
        layer.ExtraBlocks.Add(("vmsk", new byte[8]));
        return layer;
    }

    private static LayerMask MaskOf(PsdTestBuilder.Layer layer)
    {
        var doc = PsdFile.Read(new MemoryStream(new PsdTestBuilder(2, 1) { Layers = { layer } }.Build())).ToDocument();
        return Assert.IsType<PixelLayer>(doc.Root.Children[0]).Mask!;
    }

    [Fact]
    public void A_fill_layers_rasterized_vector_mask_is_applied_to_its_pixels()
    {
        var mask = MaskOf(Layer(fill: true, maskFlags: 0x08));
        Assert.True(mask.AppliedToPixels);
        Assert.Equal([128, 255], mask.Pixels!.Data);
    }

    [Fact]
    public void Other_masks_are_not()
    {
        Assert.False(MaskOf(Layer(fill: true, maskFlags: 0)).AppliedToPixels);      // a painted mask on a fill layer
        Assert.False(MaskOf(Layer(fill: false, maskFlags: 0x08)).AppliedToPixels);  // a vector mask on a pixel layer
    }
}
