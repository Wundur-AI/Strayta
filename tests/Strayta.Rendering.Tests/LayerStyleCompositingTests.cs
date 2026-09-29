using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Rendering.Tests;

/// <summary>
/// How a layer's content, fill opacity, opacity and effects combine, as Photoshop draws them: interior effects inside
/// the shape, shadows and glows hidden under the shape below full fill, opacity fading layer and effects together,
/// Softer glows reaching exactly their size, type blended with gamma, fill layers' vector masks applied once, and
/// effects near the canvas edge.
/// </summary>
public class LayerStyleCompositingTests
{
    private static Plane P(int w, int h, byte v) => new(w, h, 8, Enumerable.Repeat(v, w * h).ToArray());

    /// <summary>A w × h layer of one color and one alpha at (x, y).</summary>
    private static PixelLayer Rect(int x, int y, int w, int h, byte r, byte g, byte b, byte alpha = 255) => new()
    {
        Name = "Rect", Bounds = new PixelRect(x, y, x + w, y + h),
        Pixels = new Raster(ColorMode.Rgb, [P(w, h, r), P(w, h, g), P(w, h, b)], P(w, h, alpha)),
    };

    private static Document Doc(int w, int h, params LayerNode[] layers)
    {
        var doc = new Document(w, h, ColorMode.Rgb, 8);
        foreach (var l in layers)
        {
            l.Parent?.Remove(l);
            doc.Root.Add(l);
        }
        return doc;
    }

    private static byte[] At(byte[] rgba, int width, int x, int y) => rgba[((y * width + x) * 4)..((y * width + x) * 4 + 4)];

    private static byte[] Render(Document doc, RenderOptions? options = null) => Compositor.Render(doc, options).ToRgba8();

    private static readonly RgbColor Red = new(1, 0, 0);

    [Fact]
    public void Interior_effects_do_not_add_coverage_at_soft_edges()
    {
        // A half-transparent layer with a Color Overlay is the overlay's color at the layer's own coverage.
        var layer = Rect(0, 0, 4, 4, 255, 255, 255, alpha: 128);
        layer.Effects = new LayerEffects([new ColorOverlayEffect { Color = Red }]);
        var px = At(Render(Doc(4, 4, layer)), 4, 1, 1);
        Assert.Equal([255, 0, 0, 128], px);
    }

    [Fact]
    public void Fill_opacity_hides_the_content_but_not_the_interior_effects()
    {
        var ground = Rect(0, 0, 8, 8, 0, 0, 0);
        var layer = Rect(0, 0, 8, 8, 255, 255, 255);
        layer.FillOpacity = 0f;
        Assert.Equal([0, 0, 0, 255], At(Render(Doc(8, 8, ground, layer)), 8, 4, 4));

        // The overlay blends with the backdrop at its own opacity, as if the content were not there.
        layer.Effects = new LayerEffects([new ColorOverlayEffect { Color = Red, Opacity = 0.5f }]);
        Assert.Equal([128, 0, 0, 255], At(Render(Doc(8, 8, ground, layer)), 8, 4, 4));
    }

    [Fact]
    public void Below_full_fill_the_shape_hides_its_own_glow()
    {
        var ground = Rect(0, 0, 30, 30, 0, 0, 0);
        var layer = Rect(10, 10, 10, 10, 255, 255, 255);
        layer.Effects = new LayerEffects([new OuterGlowEffect { Color = Red, Size = 4 }]);
        layer.FillOpacity = 0.5f;
        // Inside: half the white over black, and none of the glow.
        Assert.Equal([128, 128, 128, 255], At(Render(Doc(30, 30, ground, layer)), 30, 15, 15));
        // Outside it still glows.
        Assert.True(At(Render(Doc(30, 30, ground, layer)), 30, 9, 15)[0] > 100);

        // At full fill a half-transparent layer lets its glow show through.
        var soft = Rect(10, 10, 10, 10, 255, 255, 255, alpha: 128);
        soft.Effects = layer.Effects;
        var px = At(Render(Doc(30, 30, ground, soft)), 30, 15, 15);
        Assert.True(px[0] > px[1] + 50, $"the glow shows under the layer ({string.Join(",", px)})");
    }

    [Fact]
    public void Opacity_fades_the_layer_and_its_effects_together()
    {
        // A 50% opaque layer does not show its own glow through itself: inside, it is plain white at 50%.
        var ground = Rect(0, 0, 30, 30, 0, 0, 0);
        var layer = Rect(10, 10, 10, 10, 255, 255, 255);
        layer.Opacity = 0.5f;
        layer.Effects = new LayerEffects([new OuterGlowEffect { Color = Red, Size = 4 }]);
        var img = Render(Doc(30, 30, ground, layer));
        Assert.Equal([128, 128, 128, 255], At(img, 30, 10, 15));
        layer.Opacity = 1f;
        int glow = At(Render(Doc(30, 30, ground, layer)), 30, 9, 15)[0];
        Assert.InRange(At(img, 30, 9, 15)[0], glow / 2 - 1, glow / 2 + 1); // outside, the glow is faded as usual
    }

    [Fact]
    public void A_softer_glow_is_full_against_the_shape_and_ends_at_its_size()
    {
        var layer = Rect(30, 0, 30, 60, 255, 255, 255);
        layer.Effects = new LayerEffects([new OuterGlowEffect { Color = Red, Size = 6 }]);
        var img = Render(Doc(60, 60, layer));
        Assert.True(At(img, 60, 29, 30)[3] > 200);   // half a pixel from the edge
        Assert.True(At(img, 60, 25, 30)[3] > 0);     // 4.5 pixels out
        Assert.Equal(0, At(img, 60, 23, 30)[3]);     // 6.5 pixels out: beyond the size
    }

    [Fact]
    public void Tent_blur_keeps_its_weight_and_its_reach()
    {
        foreach (float radius in new[] { 0.4f, 1f, 2.5f, 7.3f })
        {
            const int w = 41;
            var impulse = new float[w * w];
            impulse[20 * w + 20] = 1f;
            var blurred = FieldOps.TentBlur(impulse, w, w, radius);
            Assert.Equal(1f, blurred.Sum(), 3);
            // A pixel counts once the tent reaches past its near edge.
            int reach = (int)MathF.Ceiling(radius - 0.5f);
            Assert.True(blurred[20 * w + 20 + reach] > 0f, $"reaches {reach} at radius {radius}");
            Assert.InRange(blurred[20 * w + 20 + reach + 1], -1e-6f, 1e-6f);
        }
    }

    [Fact]
    public void A_pixel_layers_spread_turns_faint_artwork_solid()
    {
        // A faint (25%) line: without spread its glow is faint too; with spread it glows at full strength.
        var line = Rect(20, 0, 2, 40, 255, 255, 255, alpha: 64);
        line.Effects = new LayerEffects([new OuterGlowEffect { Color = Red, Size = 8 }]);
        int faint = At(Render(Doc(40, 40, line)), 40, 18, 20)[3];
        line.Effects = new LayerEffects([new OuterGlowEffect { Color = Red, Size = 8, Spread = 0.1f }]);
        int solid = At(Render(Doc(40, 40, line)), 40, 18, 20)[3];
        Assert.True(solid > 120 && faint < 60, $"spread {solid}, none {faint}");

        // Type and vector shapes spread their exact outline instead.
        line.Tags.Add("text");
        Assert.True(At(Render(Doc(40, 40, line)), 40, 18, 20)[3] < 100);
    }

    [Fact]
    public void Type_blends_with_gamma()
    {
        var ground = Rect(0, 0, 4, 4, 0, 0, 0);
        var text = Rect(0, 0, 4, 4, 255, 255, 255);
        text.Opacity = 0.5f;
        Assert.Equal(128, At(Render(Doc(4, 4, ground, text)), 4, 1, 1)[0]);

        text.Tags.Add("text");
        // Mixed half and half in gamma 1.45: linear 0.5^1.45 = 0.366, which is 0.639 in sRGB.
        Assert.Equal(163, At(Render(Doc(4, 4, ground, text)), 4, 1, 1)[0]);
        Assert.Equal(128, At(Render(Doc(4, 4, ground, text), new RenderOptions { TextGamma = null }), 4, 1, 1)[0]);
    }

    [Fact]
    public void A_mask_already_in_the_pixels_is_not_applied_twice()
    {
        LayerMask Mask(bool applied) => new() { Bounds = new PixelRect(0, 0, 4, 4), Pixels = P(4, 4, 128), AppliedToPixels = applied };
        var layer = Rect(0, 0, 4, 4, 255, 255, 255, alpha: 128);
        layer.Mask = Mask(applied: true);
        Assert.Equal(128, At(Render(Doc(4, 4, layer)), 4, 1, 1)[3]);
        layer.Mask = Mask(applied: false);
        Assert.Equal(64, At(Render(Doc(4, 4, layer)), 4, 1, 1)[3]);
    }

    [Fact]
    public void Effects_near_the_canvas_edge_follow_the_layer_beyond_it()
    {
        // The layer runs off the right edge: its inner glow belongs at its real edge, not at the canvas's.
        var layer = Rect(10, 0, 40, 20, 0, 0, 0);
        layer.Effects = new LayerEffects([new InnerGlowEffect { Color = Red, Size = 4 }]);
        var img = Render(Doc(30, 20, layer));
        Assert.Equal([0, 0, 0, 255], At(img, 30, 29, 10));
        Assert.True(At(img, 30, 10, 10)[0] > 150); // the real left edge glows
    }

    [Fact]
    public void A_pattern_linked_with_the_layer_starts_at_its_bounds()
    {
        // Two-pixel stripes, red then blue; the layer's first column is transparent.
        var planes = new[] { new Plane(2, 1, 8, [255, 0]), new Plane(2, 1, 8, [0, 0]), new Plane(2, 1, 8, [0, 255]) };
        var pattern = new Pattern("stripes", "Stripes", new Raster(ColorMode.Rgb, planes, null));
        var layer = Rect(5, 0, 6, 2, 255, 255, 255);
        layer.Pixels!.Alpha!.Data[0] = layer.Pixels.Alpha.Data[6] = 0;
        layer.Effects = new LayerEffects([new PatternOverlayEffect { Fill = new PatternFill(PatternReference.To(pattern)) }]);
        var img = Render(Doc(12, 2, layer));
        Assert.Equal([0, 0, 255, 255], At(img, 12, 6, 0)); // the layer starts at 5 (red), so 6 is blue
        Assert.Equal([255, 0, 0, 255], At(img, 12, 7, 0));
    }
}
