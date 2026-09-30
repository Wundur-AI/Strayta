using Strayta.Core;

namespace Strayta.Rendering.Tests;

/// <summary>
/// Merging must not change the image: each command's composite is compared pixel for pixel before and after. Scenes are
/// built so that Photoshop's merge is exact too (an opaque layer at the bottom of what is merged, and hard-edged layers
/// above the result); partially transparent results are quantized to the layer's bit depth, as in Photoshop.
/// </summary>
public class LayerMergeTests
{
    private const int W = 48, H = 36;

    /// <summary>A noisy layer; <paramref name="alpha"/> picks each pixel's opacity from its position.</summary>
    private static PixelLayer Noise(string name, PixelRect bounds, int seed, Func<int, int, byte> alpha)
    {
        var rng = new Random(seed);
        int w = bounds.Width, h = bounds.Height;
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(w, h, 8)).ToArray();
        var a = Plane.Create(w, h, 8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                foreach (var p in planes) p.Data[i] = (byte)rng.Next(256);
                a.Data[i] = alpha(x, y);
            }
        return new PixelLayer { Name = name, Bounds = bounds, Pixels = new Raster(ColorMode.Rgb, planes, a) };
    }

    private static PixelLayer Background(int seed = 1) => Noise("Background", PixelRect.FromSize(W, H), seed, (_, _) => 255);

    private static byte Soft(int x, int y) => (byte)((x * 37 + y * 11) % 256);
    private static byte Hard(int x, int y) => (byte)(((x / 3 + y / 4) % 2) * 255);

    private static byte[] Composite(Document doc) => Compositor.Render(doc).ToRgba8();

    private static void AssertSameImage(byte[] before, Document doc, string what)
    {
        var after = Composite(doc);
        int diff = 0;
        for (int i = 0; i < before.Length; i++) if (before[i] != after[i]) diff++;
        Assert.True(diff == 0, $"{what}: {diff} bytes changed");
    }

    private static Document Doc(params LayerNode[] layers)
    {
        var doc = new Document(W, H, ColorMode.Rgb, 8);
        foreach (var l in layers) doc.Root.Add(l);
        return doc;
    }

    [Fact]
    public void Merge_down_keeps_the_image_and_the_lower_layers_place()
    {
        var bg = Background();
        var upper = Noise("Upper", new PixelRect(5, 4, 40, 30), 2, Soft);
        upper.BlendMode = BlendMode.Multiply;
        upper.Opacity = 0.6f;
        upper.FillOpacity = 0.8f;
        upper.Mask = new LayerMask { Bounds = new PixelRect(0, 0, W, H), Pixels = Noise("m", PixelRect.FromSize(W, H), 9, Soft).Pixels!.ColorPlanes[0] };
        upper.Effects = new LayerEffects([new DropShadowEffect { Color = RgbColor.Black, Angle = 45, Distance = 3, Size = 2 }]);
        var above = Noise("Above", new PixelRect(10, 10, 30, 30), 3, Hard);
        var doc = Doc(bg, upper, above);
        var before = Composite(doc);

        var plan = LayerMerger.MergeDown(doc, upper);
        plan.Apply();

        Assert.Equal("Merge Down", plan.Command);
        Assert.Equal(["Background", "Above"], doc.Root.Children.Select(c => c.Name));
        AssertSameImage(before, doc, "Merge Down");
    }

    [Fact]
    public void Merge_down_on_a_clipping_base_merges_the_clipped_layers()
    {
        var bg = Background();
        var baseLayer = Noise("Base", new PixelRect(4, 4, 44, 32), 4, Hard);
        var clipped = Noise("Clipped", PixelRect.FromSize(W, H), 5, Soft);
        clipped.Clipped = true;
        clipped.BlendMode = BlendMode.Screen;
        var doc = Doc(bg, baseLayer, clipped);
        var before = Composite(doc);

        var plan = LayerMerger.MergeDown(doc, baseLayer);
        plan.Apply();

        Assert.Equal("Merge Clipping Mask", plan.Command);
        Assert.Equal(["Background", "Base"], doc.Root.Children.Select(c => c.Name));
        AssertSameImage(before, doc, "Merge Clipping Mask");
    }

    [Fact]
    public void Merge_layers_draws_groups_masks_and_adjustments_together()
    {
        var bg = Background();
        var group = new LayerGroup { Name = "Group", Mask = new LayerMask { Bounds = new PixelRect(8, 6, 40, 30), DefaultColor = 0, Pixels = Plane.Create(32, 24, 8) } };
        Array.Fill(group.Mask.Pixels!.Data, (byte)255);
        var inGroup = Noise("In", new PixelRect(0, 0, 30, 30), 6, Soft);
        inGroup.BlendMode = BlendMode.Overlay;
        group.Add(inGroup);
        var invert = new AdjustmentLayer { Name = "Invert", Kind = "Invert", Adjustment = new InvertAdjustment(), Opacity = 0.5f };
        var hidden = Noise("Hidden", PixelRect.FromSize(W, H), 7, Soft);
        hidden.Visible = false;
        var doc = Doc(bg, group, hidden, invert);
        var before = Composite(doc);

        var plan = LayerMerger.MergeLayers(doc, [invert, bg, group, hidden, inGroup]);
        plan.Apply();

        var merged = Assert.Single(doc.Root.Children);
        Assert.Equal("Invert", merged.Name); // the top-most layer's name
        AssertSameImage(before, doc, "Merge Layers");
    }

    [Fact]
    public void Merge_visible_keeps_hidden_layers()
    {
        var bg = Background();
        var hidden = Noise("Hidden", PixelRect.FromSize(W, H), 8, Soft);
        hidden.Visible = false;
        var top = Noise("Top", new PixelRect(0, 0, 20, 20), 9, Soft);
        top.BlendMode = BlendMode.Difference;
        var doc = Doc(bg, hidden, top);
        var before = Composite(doc);

        var plan = LayerMerger.MergeVisible(doc, named: top);
        plan.Apply();

        Assert.Equal(["Hidden", "Top"], doc.Root.Children.Select(c => c.Name));
        AssertSameImage(before, doc, "Merge Visible");
    }

    [Fact]
    public void Flatten_makes_one_opaque_background()
    {
        var bg = Background();
        var hidden = Noise("Hidden", PixelRect.FromSize(W, H), 10, Soft);
        hidden.Visible = false;
        var top = Noise("Top", new PixelRect(3, 3, 30, 25), 11, Soft);
        top.BlendMode = BlendMode.ColorDodge;
        var doc = Doc(bg, hidden, top);
        var before = Composite(doc);

        LayerMerger.Flatten(doc).Apply();

        var flat = Assert.IsType<PixelLayer>(Assert.Single(doc.Root.Children));
        Assert.Equal("Background", flat.Name);
        Assert.Null(flat.Pixels!.Alpha);
        AssertSameImage(before, doc, "Flatten Image");
    }

    [Fact]
    public void Flatten_puts_transparency_on_white()
    {
        var doc = Doc(Noise("Half", new PixelRect(0, 0, 10, 10), 12, (_, _) => 0));
        LayerMerger.Flatten(doc).Apply();
        var flat = (PixelLayer)doc.Root.Children[0];
        Assert.All(flat.Pixels!.ColorPlanes, p => Assert.All(p.Data, v => Assert.Equal(255, v)));
    }

    [Fact]
    public void Stamp_visible_adds_the_image_on_top_without_removing_anything()
    {
        var bg = Background();
        var top = Noise("Top", new PixelRect(3, 3, 30, 25), 13, Soft);
        top.BlendMode = BlendMode.HardLight;
        var doc = Doc(bg, top);
        var before = Composite(doc);

        var plan = LayerMerger.StampVisible(doc, top, "Layer 1");
        plan.Apply();

        Assert.Equal(["Background", "Top", "Layer 1"], doc.Root.Children.Select(c => c.Name));
        AssertSameImage(before, doc, "Stamp Visible");
    }

    [Theory]
    [InlineData(BlendMode.Normal)]
    [InlineData(BlendMode.PassThrough)]
    [InlineData(BlendMode.Multiply)]
    public void Merge_group_keeps_its_name_and_mode(BlendMode mode)
    {
        var bg = Background();
        var group = new LayerGroup { Name = "Group", BlendMode = mode };
        group.Add(Noise("Fill", PixelRect.FromSize(W, H), 14, (_, _) => 255));
        var soft = Noise("Soft", new PixelRect(5, 5, 25, 25), 15, Soft);
        soft.BlendMode = BlendMode.Luminosity;
        group.Add(soft);
        var doc = Doc(bg, group);
        var before = Composite(doc);

        var plan = LayerMerger.MergeDown(doc, group); // ⌘E on a group is Merge Group
        plan.Apply();

        Assert.Equal("Merge Group", plan.Command);
        var merged = Assert.IsType<PixelLayer>(doc.Root.Children[1]);
        Assert.Equal(("Group", mode == BlendMode.PassThrough ? BlendMode.Normal : mode), (merged.Name, merged.BlendMode));
        if (mode == BlendMode.Multiply)
        {
            // The merged pixels are stored at 8 bits before they multiply the background, as in Photoshop: rounding only.
            Assert.True(before.Zip(Composite(doc)).All(p => Math.Abs(p.First - p.Second) <= 1));
            return;
        }
        AssertSameImage(before, doc, "Merge Group");
    }

    [Fact]
    public void Soft_edged_results_differ_by_at_most_rounding()
    {
        // Merging two soft layers above an opaque one stores a partially transparent layer, quantized to 8 bits.
        var bg = Background();
        var a = Noise("A", PixelRect.FromSize(W, H), 16, Soft);
        var b = Noise("B", PixelRect.FromSize(W, H), 17, Soft);
        var doc = Doc(bg, a, b);
        var before = Composite(doc);
        LayerMerger.MergeDown(doc, b).Apply();
        var after = Composite(doc);
        Assert.True(before.Zip(after).All(p => Math.Abs(p.First - p.Second) <= 1));
    }

    [Fact]
    public void Photoshop_refusals()
    {
        var bg = Background();
        var top = Noise("Top", PixelRect.FromSize(W, H), 18, Soft);
        var doc = Doc(bg, top);
        Assert.Throws<MergeRefusedException>(() => LayerMerger.MergeDown(doc, bg)); // nothing below
        bg.Visible = false;
        Assert.Contains("hidden", Assert.Throws<MergeRefusedException>(() => LayerMerger.MergeDown(doc, top)).Message);
        bg.Visible = true;
        bg.Locks = LayerLocks.Pixels;
        Assert.Contains("locked", Assert.Throws<MergeRefusedException>(() => LayerMerger.MergeDown(doc, top)).Message);
    }
}
