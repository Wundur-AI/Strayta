using System.Buffers.Binary;
using Strayta.Core;

namespace Strayta.Psd.Tests;

public class AdjustmentWritingTests
{
    private static readonly PsdReadOptions KeepAll = new() { MaxRawBlockBytes = long.MaxValue };

    /// <summary>Every supported adjustment with non-default settings.</summary>
    public static TheoryData<string> Kinds() => new(Samples.Keys);

    private static readonly Dictionary<string, Adjustment> Samples = new()
    {
        ["levels"] = new LevelsAdjustment(new LevelsChannel(10, 240, 5, 250, 1.35f),
            [new LevelsChannel(0, 200, 0, 255, 0.8f), LevelsChannel.Identity, new LevelsChannel(30, 255, 0, 230, 1f)]),
        ["curves"] = new CurvesAdjustment([new(0, 0), new(64, 90), new(255, 255)], [null, [new(0, 20), new(128, 140), new(255, 255)]]),
        ["hue"] = new HueSaturationAdjustment(-35, 40, -12, false, 0, 25, 0, false),
        ["colorize"] = new HueSaturationAdjustment(0, 0, 0, true, 210, 60, -5, false),
        ["brightness"] = new BrightnessContrastAdjustment(42, -17),
        ["invert"] = new InvertAdjustment(),
        ["threshold"] = new ThresholdAdjustment(97),
        ["posterize"] = new PosterizeAdjustment(6),
    };

    private static AdjustmentLayer ReadSingle(string key, byte[] data)
    {
        var layer = new PsdTestBuilder.Layer { Name = "adj", ExtraBlocks = { (key, data) } };
        var doc = PsdFile.Read(new MemoryStream(new PsdTestBuilder(4, 4) { Layers = { layer } }.Build())).ToDocument();
        return Assert.IsType<AdjustmentLayer>(Assert.Single(doc.Root.Children));
    }

    /// <summary>Compares settings, ignoring trailing identity records/curves the file format pads with.</summary>
    private static void AssertSameSettings(Adjustment expected, Adjustment? actual)
    {
        switch (expected, actual)
        {
            case (LevelsAdjustment e, LevelsAdjustment a):
                Assert.Equal(e.Master, a.Master);
                Assert.Equal(e.Channels, a.Channels.Take(e.Channels.Count));
                Assert.All(a.Channels.Skip(e.Channels.Count), c => Assert.True(c.IsIdentity));
                break;
            case (CurvesAdjustment e, CurvesAdjustment a):
                Assert.Equal(e.Master, a.Master!);
                for (int c = 0; c < Math.Max(e.Channels.Count, a.Channels.Count); c++)
                {
                    var ec = c < e.Channels.Count ? e.Channels[c] : null;
                    var ac = c < a.Channels.Count ? a.Channels[c] : null;
                    if (ec is null) Assert.Null(ac);
                    else Assert.Equal(ec, ac!);
                }
                break;
            default:
                Assert.Equal(expected, actual);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Encoded_blocks_decode_to_the_same_settings(string kind)
    {
        var adjustment = Samples[kind];
        var layer = ReadSingle(PsdAdjustmentWriter.KeyOf(adjustment), PsdAdjustmentWriter.Encode(adjustment));
        AssertSameSettings(adjustment, layer.Adjustment);
    }

    [Fact]
    public void Levels_are_written_the_way_photoshop_writes_them()
    {
        var data = PsdAdjustmentWriter.Encode(Samples["levels"]);

        // 29 legacy records, then 'Lvls' version 3 announcing 62 records in all, then the other 33.
        Assert.Equal(632, data.Length);
        Assert.Equal("Lvls", System.Text.Encoding.ASCII.GetString(data, 292, 4));
        Assert.Equal(3, BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(296)));
        Assert.Equal(62, BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(298)));
        Assert.Equal(100, BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(300 + 8))); // identity gamma
    }

    [Fact]
    public void Curves_carry_the_version_4_extension_with_channel_indexes()
    {
        var data = PsdAdjustmentWriter.Encode(Samples["curves"]);

        Assert.Equal(1, BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(1)));
        Assert.Equal(0b101u, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(3))); // composite + green
        int crv = data.AsSpan().IndexOf("Crv "u8);
        Assert.True(crv > 0);
        Assert.Equal(4, BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(crv + 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(crv + 6)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(crv + 10)));         // composite first
        Assert.Equal(2, BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(crv + 10 + 2 + 2 + 3 * 4))); // then green (3 points before it)
    }

    [Fact]
    public void New_hue_saturation_blocks_have_photoshops_default_color_ranges()
    {
        var data = PsdAdjustmentWriter.Encode(Samples["hue"]);
        Assert.Equal(100, data.Length);
        Assert.Equal(315, BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(16)));      // reds start
        Assert.Equal(345, BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(16 + 70 + 6))); // magentas end
        Assert.False(ReadSingle("hue2", data).Adjustment is HueSaturationAdjustment { HasColorRangeEdits: true });
    }

    private static (PsdFile File, Document Doc) Reload(Document doc)
    {
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var file = PsdFile.Read(ms, KeepAll);
        return (file, file.ToDocument());
    }

    [Fact]
    public void New_adjustment_layers_with_masks_save_and_reopen()
    {
        var doc = new Document(6, 4, ColorMode.Rgb, 8);
        Plane P(byte v) => new(6, 4, 8, Enumerable.Repeat(v, 24).ToArray());
        doc.Root.Add(new PixelLayer { Name = "Photo", Bounds = doc.Bounds, Pixels = new Raster(ColorMode.Rgb, [P(10), P(20), P(30)], null) });
        foreach (var (kind, adjustment) in Samples)
            doc.Root.Add(new AdjustmentLayer { Name = kind, Kind = kind, Adjustment = adjustment, Mask = LayerMasks.Solid(reveal: true) });
        var painted = (AdjustmentLayer)doc.Root.Children[^1];
        painted.Mask = new LayerMask { Bounds = new PixelRect(1, 1, 3, 2), Pixels = new Plane(2, 1, 8, [0, 128]), DefaultColor = 255 };
        painted.Opacity = 0.5f;
        painted.Visible = false;

        var (file, again) = Reload(doc);

        var layers = again.Root.Children.Skip(1).Cast<AdjustmentLayer>().ToList();
        Assert.Equal(Samples.Keys, layers.Select(l => l.Name));
        foreach (var (layer, expected) in layers.Zip(Samples.Values))
        {
            AssertSameSettings(expected, layer.Adjustment);
            Assert.Equal(255, layer.Mask!.DefaultColor);
        }
        Assert.Null(layers[0].Mask!.Pixels); // reveal all: no pixels stored
        var last = layers[^1];
        Assert.Equal(new PixelRect(1, 1, 3, 2), last.Mask!.Bounds);
        Assert.Equal([0, 128], last.Mask.Pixels!.Data);
        Assert.Equal((128 / 255f, false), (last.Opacity, last.Visible));
        Assert.Equal(0x18, file.Layers[1].Flags & 0x18); // pixel data marked irrelevant, like Photoshop
    }

    /// <summary>An adjustment layer as Photoshop stores it, plus an unmodeled block that must survive.</summary>
    private static Document PhotoshopDocument(params (string Key, byte[] Data)[] adjustments)
    {
        var builder = new PsdTestBuilder(4, 4);
        foreach (var (key, data) in adjustments)
            builder.Layers.Add(new PsdTestBuilder.Layer { Name = key, ExtraBlocks = { (key, data), ("zzzz", [9, 8, 7, 6]) } });
        return PsdFile.Read(new MemoryStream(builder.Build()), KeepAll).ToDocument();
    }

    [Fact]
    public void Unedited_adjustment_blocks_are_kept_byte_for_byte()
    {
        // A Levels block with non-identity data in the 'Lvls' extension, which the model does not represent.
        var levels = PsdAdjustmentWriter.Encode(Samples["levels"]);
        BinaryPrimitives.WriteInt16BigEndian(levels.AsSpan(300 + 6), 200);
        var doc = PhotoshopDocument(("levl", levels));

        var (file, _) = Reload(doc);

        Assert.Equal(levels, file.Layers[0].FindBlock("levl")!.Data);
    }

    [Fact]
    public void Edited_adjustments_write_the_new_settings_and_keep_what_is_not_modeled()
    {
        // Hue/Saturation with a color-range edit (reds +30 hue) and Levels with extension data.
        var hue = PsdAdjustmentWriter.Encode(Samples["hue"]);
        BinaryPrimitives.WriteInt16BigEndian(hue.AsSpan(16 + 8), 30);
        var levels = PsdAdjustmentWriter.Encode(Samples["levels"]);
        BinaryPrimitives.WriteInt16BigEndian(levels.AsSpan(300 + 6), 200);
        var doc = PhotoshopDocument(("hue2", hue), ("levl", levels), ("post", PsdAdjustmentWriter.Encode(new PosterizeAdjustment(3))));

        var layers = doc.Root.Children.Cast<AdjustmentLayer>().ToList();
        var newHue = (HueSaturationAdjustment)layers[0].Adjustment! with { Hue = 90, Saturation = -60 };
        var newLevels = new LevelsAdjustment(new LevelsChannel(20, 230, 0, 255, 2.2f), ((LevelsAdjustment)layers[1].Adjustment!).Channels);
        layers[0].Adjustment = newHue;
        layers[1].Adjustment = newLevels;
        layers[2].Adjustment = new PosterizeAdjustment(12);

        var (file, again) = Reload(doc);

        var reread = again.Root.Children.Cast<AdjustmentLayer>().ToList();
        Assert.Equal(newHue, reread[0].Adjustment);
        Assert.True(((HueSaturationAdjustment)reread[0].Adjustment!).HasColorRangeEdits);
        Assert.Equal(newLevels.Master, ((LevelsAdjustment)reread[1].Adjustment!).Master);
        Assert.Equal(200, BinaryPrimitives.ReadInt16BigEndian(file.Layers[1].FindBlock("levl")!.Data.AsSpan(300 + 6)));
        Assert.Equal(new PosterizeAdjustment(12), reread[2].Adjustment);
        Assert.All(file.Layers, l => Assert.Equal([9, 8, 7, 6], l.FindBlock("zzzz")!.Data));
    }

    [Fact]
    public void Settings_equality_compares_lists_by_value()
    {
        var a = new CurvesAdjustment([new(0, 0), new(255, 255)], [null]);
        var b = new CurvesAdjustment([new(0, 0), new(255, 255)], [null]);
        Assert.NotEqual(a, b); // record equality compares list references
        Assert.True(PsdAdjustmentWriter.SameSettings(a, b));
        Assert.False(PsdAdjustmentWriter.SameSettings(a, b with { Master = [new(0, 10), new(255, 255)] }));
    }
}
