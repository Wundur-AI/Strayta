using Strayta.Core;
using Strayta.Psd;

namespace Strayta.Rendering.Tests;

/// <summary>
/// Editable layer styles: inner shadow and inner glow, hidden effects and the master switch, and a saved style
/// rendering exactly as it did before saving.
/// </summary>
public class LayerStyleRenderingTests
{
    private static PixelLayer Square(int size, int at, byte r, byte g, byte b)
    {
        var bounds = new PixelRect(at, at, at + size, at + size);
        int n = size * size;
        Plane P(byte v) => new(size, size, 8, Enumerable.Repeat(v, n).ToArray());
        return new PixelLayer { Name = "Square", Bounds = bounds, Pixels = new Raster(ColorMode.Rgb, [P(r), P(g), P(b)], P(255)) };
    }

    private static Document Doc(int size, params LayerNode[] layers)
    {
        var doc = new Document(size, size, ColorMode.Rgb, 8);
        foreach (var l in layers)
        {
            l.Parent?.Remove(l); // the tests render one layer in several documents
            doc.Root.Add(l);
        }
        return doc;
    }

    private static byte[] At(byte[] rgba, int size, int x, int y) => rgba[((y * size + x) * 4)..((y * size + x) * 4 + 4)];

    [Fact]
    public void Inner_shadow_darkens_the_edge_facing_the_light_only()
    {
        var layer = Square(12, 4, 255, 255, 255);
        layer.Effects = new LayerEffects([new InnerShadowEffect { Color = RgbColor.Black, Angle = 90, Distance = 3, Size = 0, BlendMode = BlendMode.Normal }]);
        var img = Compositor.Render(Doc(20, layer)).ToRgba8();

        Assert.Equal([0, 0, 0, 255], At(img, 20, 10, 5));        // light from the top: the top 3 rows are in shadow
        Assert.Equal([255, 255, 255, 255], At(img, 20, 10, 12));  // the middle is not
        Assert.Equal([255, 255, 255, 255], At(img, 20, 10, 15));  // nor the bottom edge
        Assert.Equal([0, 0, 0, 0], At(img, 20, 10, 2));           // and nothing outside the layer
    }

    [Fact]
    public void Inner_glow_lights_the_edges_or_the_center()
    {
        var edge = Square(16, 2, 0, 0, 0);
        edge.Effects = new LayerEffects([new InnerGlowEffect { Color = new RgbColor(1, 1, 1), Size = 6, BlendMode = BlendMode.Normal }]);
        var img = Compositor.Render(Doc(20, edge)).ToRgba8();
        Assert.True(At(img, 20, 2, 10)[0] > 100, "the edge glows");
        Assert.True(At(img, 20, 10, 10)[0] < 10, "the center does not");

        edge.Effects = new LayerEffects([new InnerGlowEffect { Color = new RgbColor(1, 1, 1), Size = 6, FromCenter = true, BlendMode = BlendMode.Normal }]);
        img = Compositor.Render(Doc(20, edge)).ToRgba8();
        Assert.True(At(img, 20, 2, 10)[0] < 150, "a center glow fades towards the edge");
        Assert.True(At(img, 20, 10, 10)[0] > 245, "and fills the middle");
    }

    [Fact]
    public void Hidden_effects_and_the_master_switch_hide_what_they_draw()
    {
        var layer = Square(4, 4, 255, 255, 255);
        var overlay = new ColorOverlayEffect { Color = new RgbColor(0, 0, 1) };
        layer.Effects = new LayerEffects([overlay with { Enabled = false }]);
        Assert.Equal([255, 255, 255, 255], At(Compositor.Render(Doc(12, layer)).ToRgba8(), 12, 5, 5));

        layer.Effects = new LayerEffects([overlay]) { Enabled = false };
        Assert.Equal([255, 255, 255, 255], At(Compositor.Render(Doc(12, layer)).ToRgba8(), 12, 5, 5));
        Assert.Empty(Compositor.Render(Doc(12, layer)).Warnings);

        layer.Effects = new LayerEffects([overlay]);
        Assert.Equal([0, 0, 255, 255], At(Compositor.Render(Doc(12, layer)).ToRgba8(), 12, 5, 5));
    }

    [Fact]
    public void A_saved_style_renders_exactly_as_before_saving()
    {
        var shadowed = Square(20, 10, 200, 60, 30);
        shadowed.Effects = new LayerEffects([
            new DropShadowEffect { Color = new RgbColor(0, 0, 0.2f), Opacity = 0.6f, BlendMode = BlendMode.Multiply, Angle = 135, UseGlobalLight = false, Distance = 5, Spread = 0.1f, Size = 7 },
            new OuterGlowEffect { Color = new RgbColor(1, 1, 0.5f), Opacity = 0.7f, BlendMode = BlendMode.Screen, Size = 6, Spread = 0.2f },
            new InnerShadowEffect { Color = RgbColor.Black, Opacity = 0.5f, BlendMode = BlendMode.Multiply, Angle = 45, UseGlobalLight = false, Distance = 3, Size = 4 },
            new InnerGlowEffect { Color = new RgbColor(1, 1, 1), Opacity = 0.4f, BlendMode = BlendMode.Screen, Size = 5, Choke = 0.2f },
            new GradientOverlayEffect
            {
                Opacity = 0.5f, BlendMode = BlendMode.Overlay, Style = GradientStyle.Radial, Angle = 60, Scale = 0.8f,
                Gradient = new Gradient([new(0, 0.5f, new RgbColor(1, 0, 0)), new(1, 0.4f, new RgbColor(0, 0, 1))], [new(0, 0.5f, 1), new(1, 0.5f, 0.5f)]),
            },
            new ColorOverlayEffect { Color = new RgbColor(0, 1, 0), Opacity = 0.25f, BlendMode = BlendMode.Color },
            new StrokeEffect { Color = new RgbColor(1, 0, 1), Size = 2, Position = StrokePosition.Center, Opacity = 0.9f },
        ]);
        var hidden = Square(6, 2, 10, 20, 30);
        hidden.Effects = new LayerEffects([new StrokeEffect { Color = new RgbColor(1, 0, 0), Size = 3, Enabled = false }]);
        var doc = Doc(48, hidden, shadowed);
        var before = Compositor.Render(doc).ToRgba8();

        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var again = PsdFile.Read(ms).ToDocument();

        Assert.Equal(doc.Root.Children.Select(n => n.Effects), again.Root.Children.Select(n => n.Effects));
        Assert.Equal(before, Compositor.Render(again).ToRgba8());
    }
}
