using Strayta.Core.Painting;

namespace Strayta.Core.Tests;

public class MaskTests
{
    private static readonly PixelRect Canvas = new(0, 0, 40, 40);

    private static PaintStroke MaskStroke(LayerNode owner, float gray, float size = 10, float opacity = 1f) =>
        PaintStroke.ForMask(owner, new BrushSettings(size, 1f, opacity), gray, Canvas);

    [Fact]
    public void Painting_black_into_a_reveal_all_mask_allocates_pixels_only_where_the_brush_went()
    {
        var layer = new AdjustmentLayer { Mask = LayerMasks.Solid(reveal: true) };
        var stroke = MaskStroke(layer, gray: 0);
        stroke.StrokeTo(20, 20);

        var mask = MaskBaker.Bake(layer.Mask!, stroke, bitDepth: 8);

        Assert.Equal(stroke.Bounds, mask.Bounds);
        Assert.Equal(0f, MaskBaker.Sample(mask, 20, 20));     // painted black
        Assert.Equal(1f, MaskBaker.Sample(mask, 2, 2));       // outside: still the white default
        Assert.Equal(1f, MaskBaker.Sample(mask, mask.Bounds.Left, mask.Bounds.Top)); // inside bounds, untouched corner
        Assert.Null(layer.Mask!.Pixels);                      // the old mask is not modified
    }

    [Fact]
    public void Strokes_blend_toward_their_gray_and_grow_an_existing_mask()
    {
        var pixels = new Plane(4, 4, 8, Enumerable.Repeat((byte)200, 16).ToArray());
        var old = new LayerMask { Bounds = new PixelRect(0, 0, 4, 4), Pixels = pixels, DefaultColor = 0, Disabled = true };
        var stroke = MaskStroke(new PixelLayer(), gray: 1f, size: 6, opacity: 0.5f);
        stroke.StrokeTo(10, 10);

        var mask = MaskBaker.Bake(old, stroke, bitDepth: 8);

        Assert.Equal(new PixelRect(0, 0, stroke.Bounds.Right, stroke.Bounds.Bottom), mask.Bounds);
        Assert.Equal(200 / 255f, MaskBaker.Sample(mask, 1, 1));           // old samples kept
        Assert.Equal(0f, MaskBaker.Sample(mask, 5, 0));                   // grown area starts at the default (black)
        Assert.InRange(MaskBaker.Sample(mask, 10, 10), 0.49f, 0.51f);     // half-way to white at 50% opacity
        Assert.True(mask.Disabled);                                       // flags carry over
        Assert.Same(pixels, old.Pixels);
    }

    [Fact]
    public void Applying_a_mask_multiplies_it_into_transparency()
    {
        Plane P(byte v) => new(2, 1, 8, [v, v]);
        var layer = new PixelLayer { Bounds = new PixelRect(5, 5, 7, 6), Pixels = new Raster(ColorMode.Rgb, [P(10), P(20), P(30)], null) };
        var mask = new LayerMask { Bounds = new PixelRect(5, 5, 6, 6), Pixels = new Plane(1, 1, 8, [0]), DefaultColor = 255 };

        var applied = MaskBaker.ApplyToPixels(layer, mask)!;

        Assert.Equal([0, 255], applied.Alpha!.Data);          // hidden where black, default white elsewhere
        Assert.Same(layer.Pixels!.ColorPlanes[0], applied.ColorPlanes[0]);
    }

    [Fact]
    public void Masks_are_reached_the_same_way_on_every_layer_kind()
    {
        var mask = LayerMasks.Solid(reveal: false);
        foreach (LayerNode node in new LayerNode[] { new PixelLayer(), new LayerGroup(), new AdjustmentLayer() })
        {
            Assert.True(node.CanHaveMask());
            node.SetMask(mask);
            Assert.Same(mask, node.GetMask());
        }
        Assert.True(mask.WithDisabled(true).Disabled);
        Assert.Throws<InvalidOperationException>(() => MaskStroke(new PixelLayer(), 0).Target);
    }
}
