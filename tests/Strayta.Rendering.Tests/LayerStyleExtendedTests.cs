using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Psd;

namespace Strayta.Rendering.Tests;

/// <summary>
/// Bevel &amp; Emboss, Satin, Pattern Overlay, gradient and pattern strokes, gradient glows, the Quality settings and
/// layer styles on groups; and every new effect rendering the same after a save and reload.
/// </summary>
public class LayerStyleExtendedTests
{
    private const int Size = 40;

    private static PixelLayer Square(int size, int at, byte r = 128, byte g = 128, byte b = 128)
    {
        var bounds = new PixelRect(at, at, at + size, at + size);
        int n = size * size;
        Plane P(byte v) => new(size, size, 8, Enumerable.Repeat(v, n).ToArray());
        return new PixelLayer { Name = "Square", Bounds = bounds, Pixels = new Raster(ColorMode.Rgb, [P(r), P(g), P(b)], P(255)) };
    }

    private static Document Doc(params LayerNode[] layers)
    {
        var doc = new Document(Size, Size, ColorMode.Rgb, 8);
        foreach (var l in layers)
        {
            l.Parent?.Remove(l); // tests render one layer in several documents
            doc.Root.Add(l);
        }
        return doc;
    }

    private static byte[] Render(Document doc) => Compositor.Render(doc).ToRgba8();

    private static byte[] At(byte[] rgba, int x, int y) => rgba[((y * Size + x) * 4)..((y * Size + x) * 4 + 4)];

    private static Pattern Stripes()
    {
        // Two columns: red, then blue.
        var planes = new[] { new Plane(2, 1, 8, [255, 0]), new Plane(2, 1, 8, [0, 0]), new Plane(2, 1, 8, [0, 255]) };
        return new Pattern("styles2-stripes", "Stripes", new Raster(ColorMode.Rgb, planes, null));
    }

    [Fact]
    public void Inner_bevel_lights_the_edge_facing_the_light_and_shades_the_other()
    {
        var layer = Square(20, 10);
        // Light from the top (90°), high enough that flat areas stay untouched.
        layer.Effects = new LayerEffects([new BevelEffect { Size = 5, Angle = 90, Altitude = 30, Technique = BevelTechnique.ChiselHard }]);
        var img = Render(Doc(layer));

        Assert.True(At(img, 20, 11)[0] > 160, $"the top edge is highlighted ({At(img, 20, 11)[0]})");
        Assert.True(At(img, 20, 28)[0] < 90, $"the bottom edge is in shadow ({At(img, 20, 28)[0]})");
        Assert.Equal([128, 128, 128, 255], At(img, 20, 20)); // the flat top is not lit
        Assert.Equal(0, At(img, 20, 8)[3]); // an inner bevel stays inside
    }

    [Fact]
    public void Down_swaps_highlight_and_shadow_and_outer_bevel_draws_outside()
    {
        var layer = Square(20, 10);
        layer.Effects = new LayerEffects([new BevelEffect { Size = 5, Angle = 90, Up = false, Technique = BevelTechnique.ChiselHard }]);
        var img = Render(Doc(layer));
        Assert.True(At(img, 20, 11)[0] < 90 && At(img, 20, 28)[0] > 160, $"down: the top edge is dark, the bottom light ({At(img, 20, 11)[0]}, {At(img, 20, 28)[0]})");

        var backdrop = Square(Size, 0, 128, 128, 128);
        var outer = Square(20, 10, 200, 0, 0);
        outer.Effects = new LayerEffects([new BevelEffect { Style = BevelStyle.OuterBevel, Size = 5, Angle = 90, Technique = BevelTechnique.ChiselHard }]);
        img = Render(Doc(backdrop, outer));
        Assert.Equal([200, 0, 0, 255], At(img, 20, 11)); // the layer itself is untouched
        // The outer bevel rises from the backdrop to the layer: above the layer that slope faces the light; below it, away.
        Assert.True(At(img, 20, 7)[1] > 150, $"above the layer the outer bevel faces the light ({At(img, 20, 7)[1]})");
        Assert.True(At(img, 20, 32)[1] < 100, $"below it, the slope faces away ({At(img, 20, 32)[1]})");
    }

    [Fact]
    public void Every_bevel_style_and_technique_draws_something()
    {
        foreach (var style in Enum.GetValues<BevelStyle>())
            foreach (var technique in Enum.GetValues<BevelTechnique>())
            {
                var backdrop = Square(Size, 0, 128, 128, 128);
                var layer = Square(20, 10);
                var fx = new List<LayerEffect> { new BevelEffect { Style = style, Technique = technique, Size = 6 } };
                if (style == BevelStyle.StrokeEmboss) fx.Add(new StrokeEffect { Size = 3, Color = new RgbColor(0.5f, 0.5f, 0.5f) });
                layer.Effects = new LayerEffects(fx);
                var img = Render(Doc(backdrop, layer));
                int changed = 0;
                for (int i = 0; i < img.Length; i += 4) changed += img[i] != 128 ? 1 : 0;
                Assert.True(changed > 40, $"{style}/{technique} changed {changed} pixels");
            }
    }

    [Fact]
    public void Stroke_emboss_needs_a_stroke()
    {
        var layer = Square(20, 10);
        layer.Effects = new LayerEffects([new BevelEffect { Style = BevelStyle.StrokeEmboss, Size = 6 }]);
        var plain = Square(20, 10);
        Assert.Equal(Render(Doc(plain)), Render(Doc(layer)));
    }

    [Fact]
    public void Bevel_texture_roughens_the_flat_surface()
    {
        var layer = Square(20, 10);
        layer.Effects = new LayerEffects([new BevelEffect { Size = 2, UseTexture = true, Texture = new PatternFill(PatternReference.To(Stripes())) { Scale = 3 }, TextureDepth = 3 }]);
        var img = Render(Doc(layer));
        // Light and shade alternate with the stripes across the flat middle.
        var row = Enumerable.Range(14, 12).Select(x => At(img, x, 20)[0]).Distinct().ToList();
        Assert.True(row.Count >= 3, $"values across the texture: {string.Join(",", row)}");
    }

    [Fact]
    public void Satin_shades_inside_only_and_invert_swaps_it()
    {
        var layer = Square(20, 10, 255, 255, 255);
        layer.Effects = new LayerEffects([new SatinEffect { Color = RgbColor.Black, BlendMode = BlendMode.Normal, Opacity = 1, Size = 4, Distance = 4, Invert = false }]);
        var img = Render(Doc(layer));
        Assert.Equal(0, At(img, 5, 5)[3]);
        Assert.Equal([255, 255, 255, 255], At(img, 20, 20)); // the copies agree in the middle: no satin there
        Assert.True(At(img, 11, 20)[0] < 200, "they differ near the edges");

        layer.Effects = new LayerEffects([new SatinEffect { Color = RgbColor.Black, BlendMode = BlendMode.Normal, Opacity = 1, Size = 4, Distance = 4, Invert = true }]);
        img = Render(Doc(layer));
        Assert.Equal([0, 0, 0, 255], At(img, 20, 20));
    }

    [Fact]
    public void Pattern_overlay_tiles_from_the_layer_or_the_canvas()
    {
        var layer = Square(20, 11);
        var pattern = PatternReference.To(Stripes());
        layer.Effects = new LayerEffects([new PatternOverlayEffect { Fill = new PatternFill(pattern) { LinkWithLayer = true } }]);
        var img = Render(Doc(layer));
        Assert.Equal([255, 0, 0, 255], At(img, 11, 20)); // the tile starts at the layer's corner
        Assert.Equal([0, 0, 255, 255], At(img, 12, 20));

        layer.Effects = new LayerEffects([new PatternOverlayEffect { Fill = new PatternFill(pattern) { LinkWithLayer = false } }]);
        img = Render(Doc(layer));
        Assert.Equal([0, 0, 255, 255], At(img, 11, 20)); // or at the canvas origin
        Assert.Equal([255, 0, 0, 255], At(img, 12, 20));
        Assert.Equal(0, At(img, 5, 5)[3]);

        // A pattern the file does not contain draws nothing (and is reported).
        layer.Effects = new LayerEffects([new PatternOverlayEffect { Fill = new PatternFill(new PatternReference("missing", "Missing")) }]);
        var result = Compositor.Render(Doc(layer));
        Assert.Equal([128, 128, 128, 255], At(result.ToRgba8(), 20, 20));
        Assert.Contains(result.Warnings, w => w.Contains("Missing"));
    }

    [Fact]
    public void Gradient_and_pattern_strokes_fill_only_the_stroke()
    {
        var gradient = GradientModel.TwoColor("Red, Blue", new RgbColor(1, 0, 0), new RgbColor(0, 0, 1));
        var layer = Square(20, 10);
        layer.Effects = new LayerEffects([new StrokeEffect
        {
            Size = 3, FillType = StrokeFillType.Gradient, GradientFill = new GradientFill(gradient) { Angle = 0 },
        }]);
        var img = Render(Doc(layer));
        var left = At(img, 8, 20);
        var right = At(img, 31, 20);
        Assert.True(left[0] > 200 && left[2] < 60, $"reddish on the left ({string.Join(",", left)})");
        Assert.True(right[2] > 200 && right[0] < 60, $"bluish on the right ({string.Join(",", right)})");
        Assert.Equal([128, 128, 128, 255], At(img, 20, 20));

        layer.Effects = new LayerEffects([new StrokeEffect { Size = 3, FillType = StrokeFillType.Pattern, PatternFill = new PatternFill(PatternReference.To(Stripes())) { LinkWithLayer = false } }]);
        img = Render(Doc(layer));
        Assert.Equal([0, 0, 255, 255], At(img, 7, 20));
        Assert.Equal([255, 0, 0, 255], At(img, 8, 20));
    }

    [Fact]
    public void Glow_technique_contour_and_gradient()
    {
        var soft = Square(10, 15);
        soft.Effects = new LayerEffects([new OuterGlowEffect { Color = new RgbColor(1, 1, 1), BlendMode = BlendMode.Normal, Size = 8 }]);
        var precise = Square(10, 15);
        precise.Effects = new LayerEffects([new OuterGlowEffect { Color = new RgbColor(1, 1, 1), BlendMode = BlendMode.Normal, Size = 8, Technique = GlowTechnique.Precise }]);
        var a = Render(Doc(soft));
        var b = Render(Doc(precise));
        Assert.True(At(b, 20, 13)[3] > At(a, 20, 13)[3], "a precise glow stays solid near the edge");

        // A Softer glow at the default 50% range is at full strength against the shape; a wider range spreads the
        // contour over more of the falloff, so the glow is fainter, and a narrower one fuller.
        Assert.True(At(a, 14, 20)[3] > 180, "strong right against the shape");
        var wide = Square(10, 15);
        wide.Effects = new LayerEffects([new OuterGlowEffect { Color = new RgbColor(1, 1, 1), BlendMode = BlendMode.Normal, Size = 8, Range = 1 }]);
        var narrow = Square(10, 15);
        narrow.Effects = new LayerEffects([new OuterGlowEffect { Color = new RgbColor(1, 1, 1), BlendMode = BlendMode.Normal, Size = 8, Range = 0.25f }]);
        Assert.True(At(Render(Doc(wide)), 12, 20)[3] < At(a, 12, 20)[3], "range 100% is fainter");
        Assert.True(At(Render(Doc(narrow)), 10, 20)[3] > At(a, 10, 20)[3], "range 25% is fuller");
        var cone = Square(10, 15);
        cone.Effects = new LayerEffects([new OuterGlowEffect { Color = new RgbColor(1, 1, 1), BlendMode = BlendMode.Normal, Size = 8, Contour = Contour.Presets[1] }]);
        Assert.NotEqual(a, Render(Doc(cone)));

        var gradient = GradientModel.TwoColor("Red, Blue", new RgbColor(1, 0, 0), new RgbColor(0, 0, 1));
        var colored = Square(10, 15);
        colored.Effects = new LayerEffects([new OuterGlowEffect { Gradient = gradient, BlendMode = BlendMode.Normal, Size = 8, Technique = GlowTechnique.Precise }]);
        var c = Render(Doc(colored));
        Assert.True(At(c, 20, 14)[0] > At(c, 20, 14)[2], "red next to the layer");
        Assert.True(At(c, 20, 9)[2] > At(c, 20, 9)[0], "blue further out");
    }

    [Fact]
    public void Shadow_noise_varies_pixels_but_keeps_the_average()
    {
        var plain = Square(20, 10);
        plain.Effects = new LayerEffects([new DropShadowEffect { Color = RgbColor.Black, BlendMode = BlendMode.Normal, Opacity = 1, Size = 6, Distance = 0 }]);
        var noisy = Square(20, 10);
        noisy.Effects = new LayerEffects([new DropShadowEffect { Color = RgbColor.Black, BlendMode = BlendMode.Normal, Opacity = 1, Size = 6, Distance = 0, Noise = 0.5f }]);
        var a = Render(Doc(plain));
        var b = Render(Doc(noisy));
        Assert.NotEqual(a, b);
        double Mean(byte[] img) => Enumerable.Range(0, Size).Average(x => img[(8 * Size + x) * 4 + 3]);
        Assert.InRange(Mean(b), Mean(a) * 0.85, Mean(a) * 1.15);
    }

    [Fact]
    public void Group_styles_follow_the_groups_content()
    {
        var group = new LayerGroup { Name = "Group" };
        group.Add(Square(8, 8, 255, 255, 255));
        group.Add(Square(8, 24, 255, 255, 255));
        group.Effects = new LayerEffects([new ColorOverlayEffect { Color = new RgbColor(1, 0, 0) }, new StrokeEffect { Size = 2, Color = new RgbColor(0, 0, 1) }]);
        var result = Compositor.Render(Doc(group));
        var img = result.ToRgba8();

        Assert.Equal([255, 0, 0, 255], At(img, 11, 11)); // both squares are overlaid
        Assert.Equal([255, 0, 0, 255], At(img, 27, 27));
        Assert.Equal([0, 0, 255, 255], At(img, 7, 11));  // and stroked around their union
        Assert.Equal([0, 0, 255, 255], At(img, 32, 27));
        Assert.Equal(0, At(img, 20, 3)[3]);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("not rendered"));

        // A pass-through group with effects is drawn isolated (and says so).
        group.BlendMode = BlendMode.PassThrough;
        result = Compositor.Render(Doc(group));
        Assert.Equal([255, 0, 0, 255], At(result.ToRgba8(), 11, 11));
        Assert.Contains(result.Warnings, w => w.Contains("isolated"));
    }

    [Fact]
    public void Groups_with_effects_inside_groups_are_not_clipped()
    {
        var inner = new LayerGroup { Name = "Inner", BlendMode = BlendMode.Normal };
        inner.Add(Square(10, 15));
        inner.Effects = new LayerEffects([new DropShadowEffect { Color = RgbColor.Black, BlendMode = BlendMode.Normal, Opacity = 1, Size = 0, Distance = 6, Angle = 90 }]);
        var outer = new LayerGroup { Name = "Outer", BlendMode = BlendMode.Normal };
        outer.Add(inner);
        var img = Render(Doc(outer));
        Assert.Equal([0, 0, 0, 255], At(img, 20, 29)); // the shadow extends below the content's own bounds
    }

    [Fact]
    public void Every_new_effect_renders_the_same_after_saving()
    {
        var pattern = Stripes();
        var gradient = GradientModel.TwoColor("Red, Blue", new RgbColor(1, 0, 0), new RgbColor(0, 0, 1));
        var effects = new LayerEffect[]
        {
            new BevelEffect { Style = BevelStyle.Emboss, Technique = BevelTechnique.ChiselSoft, Size = 5, Soften = 1, Angle = 45, UseGlobalLight = false, Altitude = 40,
                GlossContour = Contour.Presets[6], UseContour = true, Contour = Contour.Presets[7], ContourRange = 0.3f,
                UseTexture = true, Texture = new PatternFill(PatternReference.To(pattern)) { Scale = 2 }, TextureDepth = -2 },
            new SatinEffect { Color = new RgbColor(0.2f, 0.1f, 0), Contour = Contour.Presets[5] },
            new PatternOverlayEffect { Opacity = 0.5f, Fill = new PatternFill(PatternReference.To(pattern)) { Scale = 3, PhaseX = 1 } },
            new StrokeEffect { Size = 2, FillType = StrokeFillType.Gradient, GradientFill = new GradientFill(gradient) { Style = GradientStyle.ShapeBurst } },
            new OuterGlowEffect { Gradient = gradient, Size = 6, Technique = GlowTechnique.Precise, Contour = Contour.Presets[2], Range = 0.7f, Jitter = 0.2f, Noise = 0.1f },
            new InnerGlowEffect { Color = new RgbColor(1, 1, 0), Size = 4, Contour = Contour.Presets[4], AntiAliased = true },
            new DropShadowEffect { Color = RgbColor.Black, Size = 4, Distance = 3, Contour = Contour.Presets[1], Noise = 0.2f },
        };
        var layer = Square(20, 10);
        layer.Effects = new LayerEffects(effects);
        var doc = Doc(layer);
        doc.Patterns.Add(pattern);
        var before = Render(doc);

        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var again = PsdFile.Read(ms).ToDocument();
        Assert.Equal(doc.Root.Children[0].Effects, again.Root.Children[0].Effects);
        Assert.Equal(before, Render(again));
    }

    [Fact]
    public void Preview_scaling_shrinks_every_size()
    {
        var bevel = new BevelEffect { Size = 8, Soften = 2, Texture = new PatternFill(PatternReference.To(Stripes())) { Scale = 2 } };
        var scaled = (BevelEffect)new LayerEffects([bevel]).Scaled(0.25f).Items[0];
        Assert.Equal((2f, 0.5f, 0.5f), (scaled.Size, scaled.Soften, scaled.Texture!.Scale));
    }
}
