using Strayta.Core;
using Strayta.Psd.Descriptors;
using PsdDescriptorWriter = Strayta.Psd.Descriptors.DescriptorWriter;

namespace Strayta.Psd.Tests;

/// <summary>
/// Writing layer styles: the descriptor encoder, every effect kind through write → read, unedited layers kept byte for
/// byte, edited ones patched so settings the model does not hold survive, and (with $STRAYTA_CORPUS) every
/// Photoshop-authored effect block re-encoded to an identical descriptor tree.
/// </summary>
public class EffectsWritingTests
{
    private static readonly Gradient Rainbow = new(
        [new(0, 0.5f, new RgbColor(1, 0, 0)), new(0.25f, 0.3f, new RgbColor(1, 1, 0)), new(1, 0.5f, new RgbColor(0, 0, 1))],
        [new(0, 0.5f, 1), new(1, 0.7f, 0.25f)]) { Name = "Rainbowish" };

    /// <summary>One of every kind the model can write, with settings away from Photoshop's defaults.</summary>
    public static TheoryData<string> Kinds() => new(Samples.Keys);

    private static readonly Dictionary<string, LayerEffect> Samples = new()
    {
        ["DrSh"] = new DropShadowEffect
        {
            BlendMode = BlendMode.LinearBurn, Opacity = 0.42f, Color = new RgbColor(0.2f, 0.4f, 0.6f), Angle = 37,
            UseGlobalLight = false, Distance = 9, Spread = 0.15f, Size = 21, Knockout = false,
        },
        ["IrSh"] = new InnerShadowEffect
        {
            BlendMode = BlendMode.Multiply, Opacity = 0.6f, Color = new RgbColor(0, 0, 0.5f), Angle = -45, UseGlobalLight = false,
            Distance = 4, Choke = 0.3f, Size = 12,
        },
        ["OrGl"] = new OuterGlowEffect { BlendMode = BlendMode.Screen, Opacity = 0.8f, Color = new RgbColor(1, 1, 0), Spread = 0.2f, Size = 30 },
        ["IrGl"] = new InnerGlowEffect { BlendMode = BlendMode.LinearDodge, Opacity = 0.5f, Color = new RgbColor(0, 1, 1), Choke = 0.1f, Size = 8, FromCenter = true },
        ["SoFi"] = new ColorOverlayEffect { BlendMode = BlendMode.Overlay, Opacity = 0.33f, Color = new RgbColor(0.5f, 0.25f, 1) },
        ["GrFl"] = new GradientOverlayEffect
        {
            BlendMode = BlendMode.SoftLight, Opacity = 0.9f, Gradient = Rainbow, Style = GradientStyle.Reflected, Angle = 30, Scale = 1.5f,
            Reverse = true, AlignWithLayer = false, OffsetX = 0.1f, OffsetY = -0.2f,
        },
        ["FrFX"] = new StrokeEffect { BlendMode = BlendMode.Difference, Opacity = 0.7f, Color = new RgbColor(1, 0, 1), Size = 6, Position = StrokePosition.Center },
    };

    private static PsdTestBuilder.Layer Square(string name, params (string Key, byte[] Data)[] blocks)
    {
        var layer = new PsdTestBuilder.Layer
        {
            Name = name, Left = 2, Top = 2, Right = 6, Bottom = 6,
            Channels = { [-1] = Enumerable.Repeat((byte)255, 16).ToArray(), [0] = new byte[16], [1] = new byte[16], [2] = new byte[16] },
        };
        layer.ExtraBlocks.AddRange(blocks);
        return layer;
    }

    private static (PsdFile File, Document Doc) Reload(Document doc)
    {
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var file = PsdFile.Read(ms, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
        return (file, file.ToDocument());
    }

    private static Document Open(PsdTestBuilder builder) =>
        PsdFile.Read(new MemoryStream(builder.Build()), new PsdReadOptions { MaxRawBlockBytes = long.MaxValue }).ToDocument();

    // ---- Descriptor encoding ----------------------------------------------------------------------

    [Fact]
    public void Descriptor_writer_is_the_inverse_of_the_reader()
    {
        var d = new Descriptor
        {
            Name = "", ClassId = "null",
            Items =
            [
                new("Scl ", new UnitFloatValue("#Prc", 100)),
                new("masterFXSwitch", new BoolValue(true)),
                new("Nm  ", new TextValue("Linear ✓")),
                new("Md  ", new EnumValue("BlnM", "linearBurn")),
                new("Cnt ", new IntegerValue(-3)),
                new("big ", new LargeIntegerValue(1L << 40)),
                new("Rd  ", new DoubleValue(129.002)),
                new("Clss", new ClassValue("", "Lyr ")),
                new("list", new ListValue([new ObjectValue(new Descriptor { Name = "", ClassId = "CrPt", Items = [new("Hrzn", new DoubleValue(0))] })])),
                new("pts ", new UnitFloatsValue("#Pxl", new[] { 1.5, 2.5 })),
                new("raw ", new RawValue("tdta", [1, 2, 3])),
            ],
        };
        var bytes = PsdDescriptorWriter.WriteVersioned(d);
        var again = DescriptorReader.ReadVersioned(bytes);

        Assert.Equal(d.ToString(), again.ToString());
        Assert.Equal(bytes, PsdDescriptorWriter.WriteVersioned(again));
        // Strings are stored as Photoshop stores them, with a terminating null counted in the length.
        Assert.Equal([0, 0, 0, 1, 0, 0], bytes[4..10]);
    }

    // ---- Every effect kind ------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_effect_kind_survives_write_and_read(string kind)
    {
        var effect = Samples[kind];
        var effects = new LayerEffects([effect]);
        var doc = Open(new PsdTestBuilder(8, 8) { Layers = { Square("fx") } });
        doc.Root.Children[0].Effects = effects;

        var (file, again) = Reload(doc);

        var read = again.Root.Children[0].Effects;
        Assert.Equal(effects, read);
        Assert.Equal(kind, PsdEffectsWriter.TypeOf(read!.Items[0]));

        // The block has the header Photoshop writes and the keys it expects for this kind.
        var block = file.Layers[0].FindBlock("lfx2")!.Data!;
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 16], block[..8]);
        var top = DescriptorReader.ReadVersioned(block, 4);
        Assert.Equal(["Scl ", "masterFXSwitch", kind], top.Items.Select(i => i.Key));
        var fx = top.Object(kind)!;
        Assert.Equal(kind, fx.ClassId);
        Assert.True(fx.Bool("enab"));
        Assert.True(fx.Bool("present"));
        Assert.True(fx.Bool("showInDialog"));
        Assert.Equal("BlnM", Assert.IsType<EnumValue>(fx["Md  "]).Type);
        Assert.Equal("#Prc", Assert.IsType<UnitFloatValue>(fx["Opct"]).Unit);
        if (kind is "DrSh" or "IrSh" or "OrGl" or "IrGl")
        {
            Assert.Equal("#Pxl", Assert.IsType<UnitFloatValue>(fx["blur"]).Unit);
            Assert.Equal("#Pxl", Assert.IsType<UnitFloatValue>(fx["Ckmt"]).Unit); // a percentage, in Photoshop's '#Pxl'
            Assert.Equal("Linear", fx.Object("TrnS")!.Text("Nm  "));
            Assert.Equal("RGBC", fx.Object("Clr ")!.ClassId);
        }
        if (kind is "DrSh" or "IrSh")
            Assert.Equal("#Ang", Assert.IsType<UnitFloatValue>(fx["lagl"]).Unit);
        if (kind == "GrFl")
        {
            var grad = fx.Object("Grad")!;
            Assert.Equal(("Grdn", "Rainbowish", 3, 2), (grad.ClassId, grad.Text("Nm  "), grad.List("Clrs")!.Count, grad.List("Trns")!.Count));
            Assert.IsType<IntegerValue>(((ObjectValue)grad.List("Clrs")![1]).Value["Lctn"]);
            Assert.Equal(1024, ((ObjectValue)grad.List("Clrs")![1]).Value.Number("Lctn"));
            Assert.Equal("Rflc", fx.Enum("Type"));
        }
        if (kind == "FrFX")
            Assert.Equal(("CtrF", "SClr", 6.0), (fx.Enum("Styl"), fx.Enum("PntT"), fx.Number("Sz  ")));
    }

    [Fact]
    public void Several_effects_of_one_kind_use_the_multi_key_and_global_light_is_written()
    {
        var doc = Open(new PsdTestBuilder(8, 8) { Layers = { Square("fx") } });
        doc.GlobalLightAngle = 75;
        doc.GlobalLightAltitude = 40;
        var a = new DropShadowEffect { Color = new RgbColor(1, 0, 0), Angle = 75, UseGlobalLight = true, Distance = 3, Size = 2 };
        var b = new DropShadowEffect { Color = new RgbColor(0, 0, 1), Angle = 75, UseGlobalLight = true, Distance = 6, Size = 4, Enabled = false };
        doc.Root.Children[0].Effects = new LayerEffects([a, b, Samples["FrFX"]]) { Enabled = false };

        var (file, again) = Reload(doc);

        var top = DescriptorReader.ReadVersioned(file.Layers[0].FindBlock("lfx2")!.Data!, 4);
        Assert.Equal(["Scl ", "masterFXSwitch", "dropShadowMulti", "FrFX"], top.Items.Select(i => i.Key));
        Assert.False(top.Bool("masterFXSwitch"));
        Assert.Equal(doc.Root.Children[0].Effects, again.Root.Children[0].Effects);
        Assert.Equal((75f, 40f), (again.GlobalLightAngle, again.GlobalLightAltitude));
        Assert.Equal([0, 0, 0, 75], file.FindResource(1037)!.Data);
        Assert.Equal([0, 0, 0, 40], file.FindResource(1049)!.Data);
    }

    // ---- Keeping and patching original blocks -----------------------------------------------------

    /// <summary>A Photoshop-style block with settings the model does not hold (noise, a custom contour, anti-aliasing).</summary>
    private static byte[] PhotoshopBlock(double shadowSize) => DescriptorWriter.EffectsBlock(new DescriptorWriter().Descriptor("null", w => w
        .Unit("Scl ", "#Prc", 100)
        .Bool("masterFXSwitch", true)
        .Object("DrSh", "DrSh", 14, s => s
            .Bool("enab", true).Bool("present", true).Bool("showInDialog", true)
            .Enum("Md  ", "BlnM", "Mltp").Rgb("Clr ", 10, 20, 30).Unit("Opct", "#Prc", 35)
            .Bool("uglg", true).Unit("lagl", "#Ang", 120).Unit("Dstn", "#Pxl", 5).Unit("Ckmt", "#Pxl", 0)
            .Unit("blur", "#Pxl", shadowSize).Unit("Nose", "#Prc", 12).Bool("AntA", true)
            .Object("TrnS", "ShpC", 1, c => c.Text("Nm  ", "Cone")))
        .Object("OrGl", "OrGl", 4, s => s.Bool("enab", false).Bool("present", false).Bool("showInDialog", true).Rgb("Clr ", 255, 255, 0))
        .Long("numModifyingFX", 0), count: 5).ToArray());

    private static readonly byte[] LegacyEffects = [0, 0, 0, 0, 1, 2, 3];

    [Fact]
    public void Unedited_layers_keep_their_effect_blocks_byte_for_byte()
    {
        var doc = Open(new PsdTestBuilder(8, 8)
        {
            Layers = { Square("a", ("lfx2", PhotoshopBlock(5)), ("lrFX", LegacyEffects)), Square("b", ("lfx2", PhotoshopBlock(7)), ("lrFX", LegacyEffects)) },
        });
        var original = doc.Root.Children.Select(n => ((PsdLayerRecord)n.SourceData!).FindBlock("lfx2")!.Data).ToList();

        // Hiding and showing an effect again, or replacing the style with an equal one, is not an edit.
        var a = doc.Root.Children[0];
        a.Effects = a.Effects! with { Items = [.. a.Effects.Items] };
        var shadow = (DropShadowEffect)doc.Root.Children[1].Effects!.Items[0];
        doc.Root.Children[1].Effects = new LayerEffects([shadow with { Size = 11 }]) { SourceData = doc.Root.Children[1].Effects!.SourceData };

        var (file, again) = Reload(doc);

        Assert.Equal(original[0], file.Layers[0].FindBlock("lfx2")!.Data);
        Assert.Equal(LegacyEffects, file.Layers[0].FindBlock("lrFX")!.Data);
        Assert.NotEqual(original[1], file.Layers[1].FindBlock("lfx2")!.Data);
        Assert.Null(file.Layers[1].FindBlock("lrFX")); // a stale legacy block would disagree with the edit
        Assert.Equal(11f, ((DropShadowEffect)again.Root.Children[1].Effects!.Items[0]).Size);
    }

    [Fact]
    public void Editing_a_modeled_setting_keeps_everything_else_in_the_effect()
    {
        var doc = Open(new PsdTestBuilder(8, 8) { Layers = { Square("a", ("lfx2", PhotoshopBlock(5))) } });
        var layer = doc.Root.Children[0];
        var shadow = (DropShadowEffect)Assert.Single(layer.Effects!.Items);
        layer.Effects = layer.Effects with { Items = [shadow with { Size = 9, Color = new RgbColor(1, 0, 0) }] };

        var (file, _) = Reload(doc);

        var top = DescriptorReader.ReadVersioned(file.Layers[0].FindBlock("lfx2")!.Data!, 4);
        Assert.Equal(["Scl ", "masterFXSwitch", "DrSh", "OrGl", "numModifyingFX"], top.Items.Select(i => i.Key));
        var fx = top.Object("DrSh")!;
        Assert.Equal(9, fx.Number("blur"));
        Assert.Equal(255, fx.Object("Clr ")!.Number("Rd  "));
        Assert.Equal((12.0, true, "Cone"), (fx.Number("Nose")!.Value, fx.Bool("AntA")!.Value, fx.Object("TrnS")!.Text("Nm  ")));
        Assert.Equal(120, fx.Number("lagl")); // uses the global light, so its own angle stays as it was
        Assert.False(top.Object("OrGl")!.Bool("present"));
    }

    [Fact]
    public void Removed_effects_stay_remembered_and_clearing_the_style_removes_the_blocks()
    {
        var doc = Open(new PsdTestBuilder(8, 8)
        {
            Layers = { Square("a", ("lfx2", PhotoshopBlock(5)), ("lrFX", LegacyEffects)), Square("b", ("lfx2", PhotoshopBlock(5)), ("lrFX", LegacyEffects)) },
        });
        var a = doc.Root.Children[0];
        a.Effects = a.Effects! with { Items = [Samples["SoFi"]] };
        doc.Root.Children[1].Effects = null;

        var (file, again) = Reload(doc);

        var top = DescriptorReader.ReadVersioned(file.Layers[0].FindBlock("lfx2")!.Data!, 4);
        Assert.Equal(["Scl ", "masterFXSwitch", "DrSh", "OrGl", "numModifyingFX", "SoFi"], top.Items.Select(i => i.Key));
        Assert.Equal((false, false), (top.Object("DrSh")!.Bool("enab"), top.Object("DrSh")!.Bool("present")));
        Assert.Equal(5, top.Object("DrSh")!.Number("blur")); // the dialog still remembers its settings
        Assert.Equal(new LayerEffects([Samples["SoFi"]]), again.Root.Children[0].Effects);

        Assert.Null(file.Layers[1].FindBlock("lfx2"));
        Assert.Null(file.Layers[1].FindBlock("lrFX"));
        Assert.Null(again.Root.Children[1].Effects);
    }

    [Fact]
    public void Changing_the_global_light_rewrites_the_resource_only_then()
    {
        var doc = Open(new PsdTestBuilder(8, 8) { Layers = { Square("a", ("lfx2", PhotoshopBlock(5))) } });
        var (unchanged, _) = Reload(doc);
        Assert.Equal([0, 0, 0, 120], unchanged.FindResource(1037)!.Data);

        doc.GlobalLightAngle = 45;
        var shadow = (DropShadowEffect)doc.Root.Children[0].Effects!.Items[0];
        doc.Root.Children[0].Effects = doc.Root.Children[0].Effects! with { Items = [shadow with { Angle = 45 }] };
        var (file, again) = Reload(doc);

        Assert.Equal([0, 0, 0, 45], file.FindResource(1037)!.Data);
        var read = (DropShadowEffect)again.Root.Children[0].Effects!.Items[0];
        Assert.Equal((45f, true), (read.Angle, read.UseGlobalLight));
    }

    // ---- Photoshop-authored files ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CorpusTests.Files), MemberType = typeof(CorpusTests))]
    public void Photoshop_effects_re_encode_to_the_same_descriptor_tree(string relativePath)
    {
        string? corpus = Environment.GetEnvironmentVariable("STRAYTA_CORPUS");
        if (relativePath.Length == 0 || corpus is null) Assert.Skip("Set STRAYTA_CORPUS to a folder of PSD files to run corpus tests.");

        var file = PsdFile.Open(Path.Combine(corpus, relativePath), new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
        float angle = PsdEffects.GlobalAngleOf(file);
        foreach (var record in file.Layers)
        {
            if (record.FindBlock("lfx2")?.Data is not { } data) continue;
            Descriptor original;
            try { original = DescriptorReader.ReadVersioned(data, 4); }
            catch (PsdFormatException) { continue; }

            // The encoder reproduces Photoshop's bytes exactly (Photoshop pads the block with zeros after the descriptor).
            var encoded = PsdDescriptorWriter.WriteVersioned(original)[4..];
            Assert.Equal(encoded, data[8..(8 + encoded.Length)]);
            Assert.All(data[(8 + encoded.Length)..], b => Assert.Equal(0, b));

            // Re-encoding the model patches every modeled key with the value it already had.
            if (record.FindBlock("lmfx") is not null || PsdEffects.Read(record, angle) is not { } effects) continue;
            var rewritten = PsdEffectsWriter.Describe(effects);
            Assert.Equal(original.ToString(), rewritten.ToString());

            // And reading the rewritten block gives the same effects.
            var again = new PsdLayerRecord { Blocks = [new TaggedBlock("8BIM", "lfx2", 0, 0, PsdEffectsWriter.Encode(effects))] };
            Assert.Equal(effects, PsdEffects.Read(again, angle));
        }
    }
}
