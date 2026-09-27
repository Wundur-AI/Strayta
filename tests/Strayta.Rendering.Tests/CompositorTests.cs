using Strayta.Core;

namespace Strayta.Rendering.Tests;

public class CompositorTests
{
    private static PixelLayer Solid(string name, PixelRect bounds, byte r, byte g, byte b, byte a = 255)
    {
        int n = bounds.Width * bounds.Height;
        Plane P(byte v) => new(bounds.Width, bounds.Height, 8, Enumerable.Repeat(v, n).ToArray());
        return new PixelLayer { Name = name, Bounds = bounds, Pixels = new Raster(ColorMode.Rgb, [P(r), P(g), P(b)], P(a)) };
    }

    private static Document Doc(int w, int h, params LayerNode[] layers)
    {
        var doc = new Document(w, h, ColorMode.Rgb, 8);
        foreach (var l in layers) doc.Root.Add(l);
        return doc;
    }

    private static byte[] Pixel(Document doc, int x, int y, RenderOptions? options = null)
    {
        var rgba = Compositor.Render(doc, options).ToRgba8();
        int i = (y * doc.Width + x) * 4;
        return rgba[i..(i + 4)];
    }

    private static readonly PixelRect Full = new(0, 0, 2, 2);

    [Fact]
    public void Normal_layer_with_opacity_mixes_with_backdrop()
    {
        var top = Solid("red", Full, 255, 0, 0);
        top.Opacity = 0.5f;
        var doc = Doc(2, 2, Solid("blue", Full, 0, 0, 255), top);

        Assert.Equal([128, 0, 128, 255], Pixel(doc, 0, 0));
    }

    [Fact]
    public void Fill_opacity_multiplies_with_opacity()
    {
        var top = Solid("white", Full, 255, 255, 255);
        top.Opacity = 0.5f;
        top.FillOpacity = 0.5f;
        var doc = Doc(2, 2, Solid("black", Full, 0, 0, 0), top);

        Assert.Equal([64, 64, 64, 255], Pixel(doc, 0, 0));
    }

    [Fact]
    public void Transparent_areas_stay_transparent()
    {
        var doc = Doc(4, 4, Solid("small", new PixelRect(0, 0, 1, 1), 10, 20, 30));
        Assert.Equal([0, 0, 0, 0], Pixel(doc, 3, 3));
        Assert.Equal([10, 20, 30, 255], Pixel(doc, 0, 0));
    }

    [Fact]
    public void Multiply_darkens_backdrop()
    {
        var top = Solid("gray", Full, 128, 128, 128);
        top.BlendMode = BlendMode.Multiply;
        var doc = Doc(2, 2, Solid("orange", Full, 255, 128, 0), top);

        Assert.Equal([128, 64, 0, 255], Pixel(doc, 0, 0));
    }

    [Fact]
    public void Blend_mode_has_no_effect_over_transparency()
    {
        var top = Solid("gray", Full, 128, 128, 128);
        top.BlendMode = BlendMode.Multiply;
        Assert.Equal([128, 128, 128, 255], Pixel(Doc(2, 2, top), 0, 0));
    }

    [Fact]
    public void Hidden_layers_and_render_overrides()
    {
        var bottom = Solid("blue", Full, 0, 0, 255);
        var top = Solid("red", Full, 255, 0, 0);
        top.Visible = false;
        var doc = Doc(2, 2, bottom, top);

        Assert.Equal([0, 0, 255, 255], Pixel(doc, 0, 0));
        Assert.Equal([255, 0, 0, 255], Pixel(doc, 0, 0, new RenderOptions { Shown = new HashSet<LayerNode> { top } }));
        Assert.Equal([0, 0, 0, 0], Pixel(doc, 0, 0, new RenderOptions { Hidden = new HashSet<LayerNode> { bottom } }));
    }

    [Fact]
    public void Layer_mask_hides_where_black_and_uses_default_outside_bounds()
    {
        var top = Solid("red", Full, 255, 0, 0);
        top.Mask = new LayerMask
        {
            Bounds = new PixelRect(0, 0, 1, 1),
            Pixels = new Plane(1, 1, 8, [0]),
            DefaultColor = 255,
        };
        var doc = Doc(2, 2, Solid("blue", Full, 0, 0, 255), top);

        Assert.Equal([0, 0, 255, 255], Pixel(doc, 0, 0)); // masked out
        Assert.Equal([255, 0, 0, 255], Pixel(doc, 1, 1)); // outside mask bounds -> default white
    }

    [Fact]
    public void Clipped_layer_only_shows_inside_base_layer()
    {
        var baseLayer = Solid("base", new PixelRect(0, 0, 1, 2), 0, 255, 0);
        var clipped = Solid("clipped", Full, 255, 0, 0);
        clipped.Clipped = true;
        var doc = Doc(2, 2, baseLayer, clipped);

        Assert.Equal([255, 0, 0, 255], Pixel(doc, 0, 0));
        Assert.Equal([0, 0, 0, 0], Pixel(doc, 1, 0));
    }

    [Fact]
    public void Clipped_layers_are_hidden_with_their_base()
    {
        var baseLayer = Solid("base", Full, 0, 255, 0);
        baseLayer.Visible = false;
        var clipped = Solid("clipped", Full, 255, 0, 0);
        clipped.Clipped = true;

        Assert.Equal([0, 0, 0, 0], Pixel(Doc(2, 2, baseLayer, clipped), 0, 0));
    }

    [Fact]
    public void Pass_through_group_lets_children_blend_with_layers_below()
    {
        var multiply = Solid("gray", Full, 128, 128, 128);
        multiply.BlendMode = BlendMode.Multiply;
        var group = new LayerGroup { BlendMode = BlendMode.PassThrough };
        group.Add(multiply);

        Assert.Equal([128, 64, 0, 255], Pixel(Doc(2, 2, Solid("orange", Full, 255, 128, 0), group), 0, 0));
    }

    [Fact]
    public void Isolated_group_blends_children_among_themselves_first()
    {
        var multiply = Solid("gray", Full, 128, 128, 128);
        multiply.BlendMode = BlendMode.Multiply;
        var group = new LayerGroup { BlendMode = BlendMode.Normal };
        group.Add(multiply);

        // Inside the isolated group the multiply layer has nothing beneath it, so it lands as plain gray.
        Assert.Equal([128, 128, 128, 255], Pixel(Doc(2, 2, Solid("orange", Full, 255, 128, 0), group), 0, 0));
    }

    [Fact]
    public void Pass_through_group_opacity_fades_its_effect()
    {
        var group = new LayerGroup { BlendMode = BlendMode.PassThrough, Opacity = 0.5f };
        group.Add(Solid("white", Full, 255, 255, 255));

        Assert.Equal([128, 128, 128, 255], Pixel(Doc(2, 2, Solid("black", Full, 0, 0, 0), group), 0, 0));
    }

    [Fact]
    public void Warns_about_unsupported_content()
    {
        var layer = Solid("fx", Full, 1, 2, 3);
        layer.Effects = new LayerEffects([new UnsupportedEffect("Bevel & Emboss")]);
        var result = Compositor.Render(Doc(2, 2, layer));
        Assert.Contains(result.Warnings, w => w.Contains("Bevel & Emboss"));
    }
}

public class BlendFunctionTests
{
    [Theory]
    [InlineData(BlendMode.Screen, 0.5f, 0.5f, 0.75f)]
    [InlineData(BlendMode.Overlay, 0.25f, 1f, 0.5f)]
    [InlineData(BlendMode.HardLight, 1f, 0.25f, 0.5f)]
    [InlineData(BlendMode.ColorDodge, 0.5f, 0.5f, 1f)]
    [InlineData(BlendMode.ColorBurn, 0.5f, 0.5f, 0f)]
    [InlineData(BlendMode.LinearBurn, 0.75f, 0.5f, 0.25f)]
    [InlineData(BlendMode.Difference, 0.2f, 0.7f, 0.5f)]
    [InlineData(BlendMode.Exclusion, 0.5f, 0.5f, 0.5f)]
    [InlineData(BlendMode.SoftLight, 0.5f, 0.5f, 0.5f)]
    [InlineData(BlendMode.HardMix, 0.6f, 0.5f, 1f)]
    [InlineData(BlendMode.Divide, 0.25f, 0.5f, 0.5f)]
    public void Separable_modes(BlendMode mode, float backdrop, float source, float expected) =>
        Assert.Equal(expected, BlendFunctions.Separable(mode, backdrop, source), 3);

    [Fact]
    public void Luminosity_keeps_backdrop_color_and_takes_source_brightness()
    {
        var (r, g, b) = BlendFunctions.NonSeparable(BlendMode.Luminosity, 1f, 0f, 0f, 0.5f, 0.5f, 0.5f);
        Assert.Equal(0.5f, 0.3f * r + 0.59f * g + 0.11f * b, 3);
        Assert.True(r > g && g == b);
    }
}

public class FidelityReportTests
{
    [Fact]
    public void Identical_images_match_fully()
    {
        byte[] img = [10, 20, 30, 255, 0, 0, 0, 0];
        var report = FidelityReport.Compare(img, img, 2, 1);
        Assert.Equal(100, report.MatchPercent);
        Assert.Equal(0, report.MaxError);
    }

    [Fact]
    public void Color_under_full_transparency_is_ignored()
    {
        var report = FidelityReport.Compare([255, 0, 0, 0], [0, 255, 0, 0], 1, 1);
        Assert.Equal(100, report.MatchPercent);
    }
}

public class RenderCacheTests
{
    private static PixelLayer Layer(string name, int x, byte r, byte g, byte b, BlendMode mode = BlendMode.Normal)
    {
        var bounds = new PixelRect(x, 0, x + 6, 4);
        Plane P(byte v) => new(6, 4, 8, Enumerable.Repeat(v, 24).ToArray());
        return new PixelLayer { Name = name, Bounds = bounds, BlendMode = mode, Pixels = new Raster(ColorMode.Rgb, [P(r), P(g), P(b)], P(200)) };
    }

    /// <summary>A stack with pass-through groups, isolated groups and clipping so steps get expanded.</summary>
    private static (Document Doc, List<LayerNode> All) Build()
    {
        var doc = new Document(10, 4, ColorMode.Rgb, 8);
        doc.Root.Add(Layer("bg", 0, 250, 250, 250));
        var pass = new LayerGroup { Name = "pass" };
        pass.Add(Layer("p1", 1, 200, 20, 20, BlendMode.Multiply));
        pass.Add(Layer("p2", 2, 20, 200, 20, BlendMode.Screen));
        var clipped = Layer("clipped", 0, 0, 0, 255, BlendMode.Overlay);
        clipped.Clipped = true;
        pass.Add(clipped);
        doc.Root.Add(pass);
        var iso = new LayerGroup { Name = "iso", BlendMode = BlendMode.Difference };
        iso.Add(Layer("i1", 3, 90, 90, 200));
        doc.Root.Add(iso);
        doc.Root.Add(Layer("top", 4, 10, 120, 240, BlendMode.SoftLight));
        return (doc, doc.Root.Descendants().ToList());
    }

    [Theory]
    [InlineData(1L << 30)]  // room for a checkpoint at every step
    [InlineData(640L * 3)]  // 10x4 canvas = 640 bytes: only two checkpoints, forcing eviction
    public void Cached_renders_match_fresh_renders_through_many_toggles(long budget)
    {
        var (doc, all) = Build();
        var cache = new RenderCache(budget);
        var hidden = new HashSet<LayerNode>();
        var rng = new Random(42);

        for (int round = 0; round < 60; round++)
        {
            var node = all[rng.Next(all.Count)];
            if (!hidden.Remove(node)) hidden.Add(node);

            var cached = Compositor.Render(doc, new RenderOptions { Hidden = hidden }, cache).ToRgba8();
            var fresh = Compositor.Render(doc, new RenderOptions { Hidden = hidden }).ToRgba8();
            Assert.Equal(fresh, cached);
        }
    }

    [Fact]
    public void Cached_renders_track_property_and_order_edits()
    {
        var (doc, all) = Build();
        var cache = new RenderCache();
        var rng = new Random(7);
        var layers = all.OfType<PixelLayer>().ToList();
        var modes = new[] { BlendMode.Normal, BlendMode.Multiply, BlendMode.Screen, BlendMode.Overlay, BlendMode.Difference };

        for (int round = 0; round < 60; round++)
        {
            var layer = layers[rng.Next(layers.Count)];
            switch (rng.Next(4))
            {
                case 0: layer.Opacity = (float)rng.NextDouble(); break;
                case 1: layer.BlendMode = modes[rng.Next(modes.Length)]; break;
                case 2: layer.Bounds = layer.Bounds with { Left = layer.Bounds.Left + 1, Right = layer.Bounds.Right + 1 }; break;
                case 3:
                    var parent = layer.Parent!;
                    parent.Remove(layer);
                    parent.Insert(rng.Next(parent.Children.Count + 1), layer);
                    break;
            }

            var cached = Compositor.Render(doc, cache: cache).ToRgba8();
            var fresh = Compositor.Render(doc).ToRgba8();
            Assert.Equal(fresh, cached);
        }
    }

    [Fact]
    public void Cache_smaller_than_one_canvas_falls_back_to_full_renders()
    {
        var (doc, _) = Build();
        var tiny = new RenderCache(memoryBudgetBytes: 16);
        var a = Compositor.Render(doc, cache: tiny).ToRgba8();
        var b = Compositor.Render(doc).ToRgba8();
        Assert.Equal(b, a);
    }
}

public class AdjustmentRenderingTests
{
    private static readonly PixelRect Full = new(0, 0, 2, 2);

    private static PixelLayer Solid(PixelRect bounds, byte r, byte g, byte b, byte a = 255)
    {
        int n = bounds.Width * bounds.Height;
        Plane P(byte v) => new(bounds.Width, bounds.Height, 8, Enumerable.Repeat(v, n).ToArray());
        return new PixelLayer { Bounds = bounds, Pixels = new Raster(ColorMode.Rgb, [P(r), P(g), P(b)], P(a)) };
    }

    private static byte[] Pixel(Document doc, int x, int y)
    {
        var rgba = Compositor.Render(doc).ToRgba8();
        int i = (y * doc.Width + x) * 4;
        return rgba[i..(i + 4)];
    }

    private static Document Doc(params LayerNode[] layers)
    {
        var doc = new Document(2, 2, ColorMode.Rgb, 8);
        foreach (var l in layers) doc.Root.Add(l);
        return doc;
    }

    private static AdjustmentLayer Invert() => new() { Adjustment = new InvertAdjustment(), Kind = "Invert" };

    [Fact]
    public void Levels_math_matches_photoshop_definition()
    {
        var l = new LevelsChannel(0, 95, 0, 255, 1.67f);
        Assert.Equal(1f, LutTransform.Levels(l, 95 / 255f), 3);
        Assert.Equal(MathF.Pow(0.5f, 1 / 1.67f), LutTransform.Levels(l, 47.5f / 255f), 3);
        Assert.Equal(0.5f, LutTransform.Levels(LevelsChannel.Identity, 0.5f), 4);
        Assert.Equal(10 / 255f, LutTransform.Levels(new LevelsChannel(0, 255, 10, 240, 1f), 0f), 4);
    }

    [Fact]
    public void Curve_spline_passes_through_its_points_and_is_identity_for_a_diagonal()
    {
        var spline = LutTransform.Spline([new(0, 0), new(128, 200), new(255, 255)]);
        Assert.Equal(200 / 255f, spline(128 / 255f), 3);
        var identity = LutTransform.Spline([new(0, 0), new(255, 255)]);
        Assert.Equal(0.3f, identity(0.3f), 4);
    }

    [Fact]
    public void Adjustment_changes_layers_below()
    {
        Assert.Equal([255, 155, 55, 255], Pixel(Doc(Solid(Full, 0, 100, 200), Invert()), 0, 0));
    }

    [Fact]
    public void Adjustment_leaves_transparent_areas_transparent()
    {
        Assert.Equal([0, 0, 0, 0], Pixel(Doc(Solid(new PixelRect(0, 0, 1, 1), 0, 0, 0), Invert()), 1, 1));
    }

    [Fact]
    public void Adjustment_respects_opacity_and_mask()
    {
        var inv = Invert();
        inv.Opacity = 0.5f;
        inv.Mask = new LayerMask { Bounds = new PixelRect(0, 0, 1, 1), Pixels = new Plane(1, 1, 8, [0]), DefaultColor = 255 };
        var doc = Doc(Solid(Full, 0, 0, 0), inv);

        Assert.Equal([0, 0, 0, 255], Pixel(doc, 0, 0));       // masked out
        Assert.Equal([128, 128, 128, 255], Pixel(doc, 1, 1)); // half-strength invert of black
    }

    [Fact]
    public void Clipped_adjustment_only_affects_its_base_layer()
    {
        var baseLayer = Solid(new PixelRect(0, 0, 1, 2), 0, 0, 0);
        var inv = Invert();
        inv.Clipped = true;
        var doc = Doc(Solid(Full, 0, 0, 0), baseLayer, inv);

        Assert.Equal([255, 255, 255, 255], Pixel(doc, 0, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(doc, 1, 0));
    }

    [Fact]
    public void Adjustment_in_pass_through_group_reaches_layers_below_the_group()
    {
        var group = new LayerGroup { BlendMode = BlendMode.PassThrough, Opacity = 0.5f };
        group.Add(Invert());
        Assert.Equal([128, 128, 128, 255], Pixel(Doc(Solid(Full, 0, 0, 0), group), 0, 0));
    }

    [Fact]
    public void Adjustment_in_isolated_group_only_affects_the_group()
    {
        var group = new LayerGroup { BlendMode = BlendMode.Normal };
        group.Add(Solid(new PixelRect(0, 0, 1, 2), 0, 0, 0));
        group.Add(Invert());
        var doc = Doc(Solid(Full, 0, 0, 0), group);

        Assert.Equal([255, 255, 255, 255], Pixel(doc, 0, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(doc, 1, 0));
    }

    [Fact]
    public void Hue_rotation_turns_red_into_green()
    {
        var hue = new AdjustmentLayer { Adjustment = new HueSaturationAdjustment(120, 0, 0, false, 0, 0, 0, false) };
        Assert.Equal([0, 255, 0, 255], Pixel(Doc(Solid(Full, 255, 0, 0), hue), 0, 0));
    }

    [Fact]
    public void Unsupported_adjustments_are_skipped_with_a_warning()
    {
        var doc = Doc(Solid(Full, 10, 20, 30), new AdjustmentLayer { Kind = "Color Balance", Name = "cb" });
        var result = Compositor.Render(doc);
        Assert.Contains(result.Warnings, w => w.Contains("Color Balance"));
        Assert.Equal([10, 20, 30, 255], Pixel(doc, 0, 0));
    }
}

public class FieldOpsTests
{
    [Fact]
    public void Distance_transform_is_euclidean()
    {
        var field = new float[5 * 5];
        field[0] = 1f; // single inside pixel at (0,0)
        var d = FieldOps.DistanceTo(field, 5, 5, v => v >= 0.5f);
        Assert.Equal(0f, d[0]);
        Assert.Equal(4f, d[4], 3);
        Assert.Equal(5f, d[4 * 5 + 3], 3);
    }

    [Fact]
    public void Blur_preserves_total_coverage_away_from_edges()
    {
        var field = new float[21 * 21];
        field[10 * 21 + 10] = 1f;
        var blurred = FieldOps.Blur(field, 21, 21, 6f);
        Assert.Equal(1f, blurred.Sum(), 3);
        Assert.True(blurred[10 * 21 + 10] < 0.2f);
    }
}

public class EffectRenderingTests
{
    private static PixelLayer Square(int size, int at, byte r, byte g, byte b)
    {
        var bounds = new PixelRect(at, at, at + size, at + size);
        int n = size * size;
        Plane P(byte v) => new(size, size, 8, Enumerable.Repeat(v, n).ToArray());
        return new PixelLayer { Bounds = bounds, Pixels = new Raster(ColorMode.Rgb, [P(r), P(g), P(b)], P(255)) };
    }

    private static byte[] Render(int size, params LayerNode[] layers)
    {
        var doc = new Document(size, size, ColorMode.Rgb, 8);
        foreach (var l in layers) doc.Root.Add(l);
        return Compositor.Render(doc).ToRgba8();
    }

    private static byte[] At(byte[] rgba, int size, int x, int y) => rgba[((y * size + x) * 4)..((y * size + x) * 4 + 4)];

    [Fact]
    public void Outside_stroke_surrounds_the_shape()
    {
        var layer = Square(4, 8, 255, 255, 255);
        layer.Effects = new LayerEffects([new StrokeEffect { Color = new RgbColor(1, 0, 0), Size = 2, Position = StrokePosition.Outside }]);
        var img = Render(20, layer);

        Assert.Equal([255, 255, 255, 255], At(img, 20, 9, 9));  // inside unchanged
        Assert.Equal([255, 0, 0, 255], At(img, 20, 7, 9));      // 1 px out
        Assert.Equal([255, 0, 0, 255], At(img, 20, 6, 9));      // 2 px out
        Assert.Equal([0, 0, 0, 0], At(img, 20, 4, 9));          // beyond the stroke
    }

    [Fact]
    public void Drop_shadow_falls_away_from_the_light()
    {
        var layer = Square(4, 8, 255, 255, 255);
        layer.Effects = new LayerEffects([new DropShadowEffect { Color = RgbColor.Black, Angle = 90, Distance = 4, Size = 0 }]);
        var img = Render(20, layer);

        Assert.Equal(255, At(img, 20, 9, 13)[3]); // light from the top: shadow 4 px below
        Assert.Equal(0, At(img, 20, 9, 6)[3]);    // nothing above
    }

    [Fact]
    public void Color_overlay_ignores_fill_opacity()
    {
        var layer = Square(4, 0, 255, 255, 255);
        layer.FillOpacity = 0f;
        layer.Effects = new LayerEffects([new ColorOverlayEffect { Color = new RgbColor(0, 0, 1) }]);
        Assert.Equal([0, 0, 255, 255], At(Render(4, layer), 4, 1, 1));
    }

    [Fact]
    public void Special_eight_modes_scale_the_source_by_fill_instead_of_fading()
    {
        var bottom = Square(2, 0, 200, 200, 200);
        var top = Square(2, 0, 255, 255, 255);
        top.BlendMode = BlendMode.LinearDodge;
        top.FillOpacity = 0.2f;

        // Photoshop adds 20% of white (+51, clamped); fading the clamped result would give 211.
        Assert.Equal([251, 251, 251, 255], At(Render(2, bottom, top), 2, 0, 0));
    }
}

public class CpuRendererTests
{
    private static Document Doc(byte value)
    {
        var doc = new Document(2, 2, ColorMode.Rgb, 8);
        Plane P(byte v) => new(2, 2, 8, [v, v, v, v]);
        doc.Root.Add(new PixelLayer { Bounds = new PixelRect(0, 0, 2, 2), Pixels = new Raster(ColorMode.Rgb, [P(value), P(value), P(value)], null) });
        return doc;
    }

    [Fact]
    public void Matches_the_reference_compositor()
    {
        var doc = Doc(77);
        using var renderer = Renderers.CreateDefault();
        Assert.Equal(Compositor.Render(doc).ToRgba8(), renderer.Render(doc).ToRgba8());
    }

    [Fact]
    public void Switching_documents_does_not_reuse_cached_pixels()
    {
        using var renderer = new CpuRenderer();
        renderer.Render(Doc(10));
        Assert.Equal(200, renderer.Render(Doc(200)).ToRgba8()[0]);
    }

    [Fact]
    public void Invalidate_picks_up_edited_layer_content()
    {
        var doc = Doc(10);
        using var renderer = new CpuRenderer();
        renderer.Render(doc);

        var layer = (PixelLayer)doc.Root.Children[0];
        layer.Pixels!.ColorPlanes[0].Data[0] = 250;
        renderer.Invalidate();

        Assert.Equal(250, renderer.Render(doc).ToRgba8()[0]);
    }
}

public class CoverageTests
{
    [Fact]
    public void Full_canvas_mask_with_white_default_limits_to_its_nonzero_pixels()
    {
        var pixels = new Plane(10, 10, 8, new byte[100]);
        pixels.Data[5 * 10 + 3] = 200;
        var mask = new LayerMask { Bounds = new PixelRect(0, 0, 10, 10), Pixels = pixels, DefaultColor = 255 };

        Assert.Equal(new PixelRect(3, 5, 4, 6), Coverage.MaskReach(mask, new PixelRect(0, 0, 10, 10)));
        // If the mask does not cover the whole area, its white default could reveal the rest.
        Assert.Null(Coverage.MaskReach(mask, new PixelRect(0, 0, 20, 20)));

        // A black band along the bottom with a white default only reveals the area above it.
        var band = new LayerMask { Bounds = new PixelRect(0, 6, 10, 10), Pixels = new Plane(10, 4, 8, new byte[40]), DefaultColor = 255 };
        Assert.Equal(new PixelRect(0, 0, 10, 6), Coverage.MaskReach(band, new PixelRect(0, 0, 10, 10)));
    }

    [Fact]
    public void Skipping_masked_out_areas_does_not_change_the_image()
    {
        var doc = new Document(12, 12, ColorMode.Rgb, 8);
        Plane P(byte v) => new(12, 12, 8, Enumerable.Repeat(v, 144).ToArray());
        doc.Root.Add(new PixelLayer { Bounds = doc.Bounds, Pixels = new Raster(ColorMode.Rgb, [P(0), P(0), P(200)], null) });
        var maskPixels = new Plane(12, 12, 8, new byte[144]);
        for (int y = 4; y < 7; y++) for (int x = 2; x < 9; x++) maskPixels.Data[y * 12 + x] = 255;
        var fill = new PixelLayer
        {
            Bounds = doc.Bounds,
            Pixels = new Raster(ColorMode.Rgb, [P(255), P(0), P(0)], P(255)),
            Mask = new LayerMask { Bounds = doc.Bounds, Pixels = maskPixels, DefaultColor = 255 },
        };
        doc.Root.Add(fill);

        var img = Compositor.Render(doc).ToRgba8();
        int at = (5 * 12 + 4) * 4;
        Assert.Equal([255, 0, 0, 255], img[at..(at + 4)]);
        Assert.Equal([0, 0, 200, 255], img[0..4]);
    }
}

public class PreviewTests
{
    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(0.75, 2)]
    [InlineData(0.5, 2)]
    [InlineData(0.3, 4)]
    [InlineData(0.27, 4)]
    [InlineData(0.01, 16)]
    public void Factor_keeps_preview_close_to_screen_resolution(double zoom, int factor) =>
        Assert.Equal(factor, PreviewDocument.FactorForZoom(zoom));

    [Fact]
    public void Preview_is_a_downscaled_render_that_follows_edits()
    {
        var doc = new Document(64, 64, ColorMode.Rgb, 8);
        Plane P(int w, int h, byte v) => new(w, h, 8, Enumerable.Repeat(v, w * h).ToArray());
        doc.Root.Add(new PixelLayer { Bounds = doc.Bounds, Pixels = new Raster(ColorMode.Rgb, [P(64, 64, 0), P(64, 64, 0), P(64, 64, 255)], null) });
        var red = new PixelLayer { Bounds = new PixelRect(0, 0, 16, 16), Pixels = new Raster(ColorMode.Rgb, [P(16, 16, 255), P(16, 16, 0), P(16, 16, 0)], P(16, 16, 255)) };
        doc.Root.Add(red);

        var preview = new PreviewDocument(doc, 4);
        Assert.Equal((16, 16), (preview.Proxy.Width, preview.Proxy.Height));
        using var renderer = new CpuRenderer();

        var img = renderer.Render(preview.Sync()).ToRgba8();
        Assert.Equal([255, 0, 0, 255], img[0..4]);

        red.Bounds = new PixelRect(32, 32, 48, 48); // move
        img = renderer.Render(preview.Sync()).ToRgba8();
        Assert.Equal([0, 0, 255, 255], img[0..4]);
        int at = (9 * 16 + 9) * 4;
        Assert.Equal([255, 0, 0, 255], img[at..(at + 4)]);

        doc.Root.Remove(red);                        // structure change
        img = renderer.Render(preview.Sync()).ToRgba8();
        Assert.Equal([0, 0, 255, 255], img[at..(at + 4)]);
    }

    [Fact]
    public void Renders_cancelled_midway_through_a_layer_leave_the_cache_consistent()
    {
        var doc = new Document(256, 256, ColorMode.Rgb, 8);
        Plane P(byte v) => new(256, 256, 8, Enumerable.Repeat(v, 256 * 256).ToArray());
        for (int i = 0; i < 8; i++)
            doc.Root.Add(new PixelLayer { Bounds = doc.Bounds, Opacity = 0.5f, BlendMode = BlendMode.Multiply, Pixels = new Raster(ColorMode.Rgb, [P((byte)(i * 30)), P(90), P(200)], null) });
        var cache = new RenderCache();
        Compositor.Render(doc, cache: cache);

        for (int attempt = 0; attempt < 20; attempt++)
        {
            doc.Root.Children[attempt % 8].Opacity = 0.1f + attempt * 0.04f;
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromTicks(attempt * 2000));
            try { Compositor.Render(doc, new RenderOptions { Cancellation = cts.Token }, cache); }
            catch (OperationCanceledException) { }
            Assert.Equal(Compositor.Render(doc).ToRgba8(), Compositor.Render(doc, cache: cache).ToRgba8());
        }
    }

    [Fact]
    public void Cancelled_renders_leave_the_cache_consistent()
    {
        var doc = new Document(8, 8, ColorMode.Rgb, 8);
        Plane P(byte v) => new(8, 8, 8, Enumerable.Repeat(v, 64).ToArray());
        for (int i = 0; i < 6; i++)
            doc.Root.Add(new PixelLayer { Bounds = doc.Bounds, Opacity = 0.5f, Pixels = new Raster(ColorMode.Rgb, [P((byte)(i * 40)), P(10), P(20)], null) });
        var cache = new RenderCache();
        Compositor.Render(doc, cache: cache);

        doc.Root.Children[1].Opacity = 0.25f;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Compositor.Render(doc, new RenderOptions { Cancellation = cts.Token }, cache));

        Assert.Equal(Compositor.Render(doc).ToRgba8(), Compositor.Render(doc, cache: cache).ToRgba8());
    }
}

public class StrokeRenderingTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 4)]
    public void Live_stroke_matches_the_baked_result(bool erase, int factor)
    {
        var doc = new Document(256, 256, ColorMode.Rgb, 8);
        Plane P(byte v) => new(128, 128, 8, Enumerable.Repeat(v, 128 * 128).ToArray());
        var layer = new PixelLayer { Bounds = new PixelRect(64, 64, 192, 192), Pixels = new Raster(ColorMode.Rgb, [P(0), P(200), P(0)], P(255)) };
        doc.Root.Add(layer);

        // Starts outside the layer so brush strokes also grow it.
        var stroke = new Strayta.Core.Painting.PaintStroke(layer, new Strayta.Core.Painting.BrushSettings(40, 0.5f, 0.8f),
            new RgbColor(1, 0, 0), erase, doc.Bounds);
        stroke.StrokeTo(16, 120);
        stroke.StrokeTo(160, 136);

        byte[] live, baked;
        if (factor == 1)
        {
            live = Compositor.Render(doc, new RenderOptions { ActiveStroke = new StrokeOverlay(stroke, layer) }).ToRgba8();
        }
        else
        {
            var preview = new PreviewDocument(doc, factor);
            var proxy = preview.Sync();
            live = Compositor.Render(proxy, new RenderOptions { ActiveStroke = preview.MapStroke(stroke) }).ToRgba8();
        }

        var (pixels, bounds) = Strayta.Core.Painting.StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        layer.Pixels = pixels;
        layer.Bounds = bounds;
        baked = factor == 1 ? Compositor.Render(doc).ToRgba8() : Compositor.Render(new PreviewDocument(doc, factor).Sync()).ToRgba8();

        // Compare premultiplied: the baked layer stores 8-bit alpha, so nearly invisible edge pixels round
        // differently from the float overlay without looking any different.
        var report = FidelityReport.Compare(live, baked, doc.Width / factor, doc.Height / factor);
        if (factor == 1) Assert.True(report.MaxError <= 2, $"live stroke differs from baked by up to {report.MaxError}");
        // Previews sample the stroke at each preview pixel's center instead of averaging the block, so edges
        // differ slightly; the image as a whole must stay close.
        else Assert.True(report.MeanError < 3, $"preview stroke mean error {report.MeanError:F2}");
    }
}
