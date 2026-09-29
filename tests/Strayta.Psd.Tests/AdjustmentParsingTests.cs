using System.Buffers.Binary;
using Strayta.Core;

namespace Strayta.Psd.Tests;

public class AdjustmentParsingTests
{
    private static byte[] Shorts(params int[] values)
    {
        var b = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(i * 2), (short)values[i]);
        return b;
    }

    private static AdjustmentLayer ReadSingle(string key, byte[] data)
    {
        var layer = new PsdTestBuilder.Layer { Name = "adj", ExtraBlocks = { (key, data) } };
        var doc = PsdFile.Read(new MemoryStream(new PsdTestBuilder(4, 4) { Layers = { layer } }.Build())).ToDocument();
        return Assert.IsType<AdjustmentLayer>(Assert.Single(doc.Root.Children));
    }

    [Fact]
    public void Levels_reads_master_and_channel_records()
    {
        // Version 2, master (0..95 -> 0..255, gamma 1.67), red channel output 10..240, then padding records.
        var data = Shorts([2, 0, 95, 0, 255, 167, 0, 255, 10, 240, 100, .. Enumerable.Repeat(0, 27 * 5)]);

        var layer = ReadSingle("levl", data);

        var levels = Assert.IsType<LevelsAdjustment>(layer.Adjustment);
        Assert.Equal("Levels", layer.Kind);
        Assert.Equal(new LevelsChannel(0, 95, 0, 255, 1.67f), levels.Master);
        Assert.Equal(new LevelsChannel(0, 255, 10, 240, 1f), levels.Channels[0]);
        Assert.Contains("adjustment", layer.Tags);
    }

    [Fact]
    public void Curves_reads_points_per_channel()
    {
        // Filler, version 1, bitmap: composite (bit 0) and green (bit 2). Points are (output, input).
        var data = new byte[] { 0 }
            .Concat(Shorts(1)).Concat(new byte[] { 0, 0, 0, 0b101 })
            .Concat(Shorts(2, 0, 0, 255, 255))            // composite: identity
            .Concat(Shorts(3, 0, 0, 200, 128, 255, 255))  // green: lifts mid-tones
            .ToArray();

        var curves = Assert.IsType<CurvesAdjustment>(ReadSingle("curv", data).Adjustment);

        Assert.Equal([new CurvePoint(0, 0), new CurvePoint(255, 255)], curves.Master!);
        Assert.Null(curves.Channels[0]);
        Assert.Equal(new CurvePoint(128, 200), curves.Channels[1]![1]);
    }

    [Fact]
    public void Hue_saturation_reads_master_and_colorize()
    {
        var data = new byte[] { 0, 2, 1, 0 }
            .Concat(Shorts(30, 40, -10))     // colorize H/S/L
            .Concat(Shorts(120, -50, 20))    // master H/S/L
            .Concat(Shorts([.. Enumerable.Repeat(0, 6 * 7)]))
            .ToArray();

        var hs = Assert.IsType<HueSaturationAdjustment>(ReadSingle("hue2", data).Adjustment);

        Assert.True(hs.Colorize);
        Assert.Equal((30, 40, -10), (hs.ColorizeHue, hs.ColorizeSaturation, hs.ColorizeLightness));
        Assert.Equal((120, -50, 20), (hs.Hue, hs.Saturation, hs.Lightness));
        Assert.False(hs.HasColorRangeEdits);
    }

    [Fact]
    public void Simple_adjustments()
    {
        Assert.IsType<InvertAdjustment>(ReadSingle("nvrt", []).Adjustment);
        Assert.Equal(new ThresholdAdjustment(128), ReadSingle("thrs", Shorts(128)).Adjustment);
        Assert.Equal(new PosterizeAdjustment(4), ReadSingle("post", Shorts(4)).Adjustment);
        Assert.Equal(new BrightnessContrastAdjustment(20, -30), ReadSingle("brit", Shorts(20, -30, 127, 0)).Adjustment);
    }

    [Fact]
    public void Unsupported_adjustments_become_layers_without_an_adjustment()
    {
        var layer = ReadSingle("hue ", new byte[40]); // the pre-Photoshop 5 Hue/Saturation layout
        Assert.Equal("Hue/Saturation (legacy)", layer.Kind);
        Assert.Null(layer.Adjustment);
    }

    [Fact]
    public void Truncated_adjustment_data_is_reported_as_unsupported()
    {
        var layer = ReadSingle("curv", [0, 0, 1]);
        Assert.Null(layer.Adjustment);
    }
}
