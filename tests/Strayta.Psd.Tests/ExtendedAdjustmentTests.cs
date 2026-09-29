using System.Buffers.Binary;
using System.Text;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd.Tests;

/// <summary>Exposure, Vibrance, Color Balance, Black &amp; White, Photo Filter, Channel Mixer, Selective Color, Gradient Map and Color Lookup blocks.</summary>
public class ExtendedAdjustmentTests
{
    private static readonly PsdReadOptions KeepAll = new() { MaxRawBlockBytes = long.MaxValue };

    private static Document Build(params (string Key, byte[] Data)[] adjustments)
    {
        var builder = new PsdTestBuilder(4, 4);
        foreach (var (key, data) in adjustments)
            builder.Layers.Add(new PsdTestBuilder.Layer { Name = key, ExtraBlocks = { (key, data) } });
        return PsdFile.Read(new MemoryStream(builder.Build()), KeepAll).ToDocument();
    }

    private static (PsdFile File, Document Doc) Reload(Document doc)
    {
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var file = PsdFile.Read(ms, KeepAll);
        return (file, file.ToDocument());
    }

    private static Adjustment? Single(string key, byte[] data) => Assert.IsType<AdjustmentLayer>(Assert.Single(Build((key, data)).Root.Children)).Adjustment;

    [Fact]
    public void Exposure_reads_version_and_three_floats()
    {
        var d = new byte[16];
        BinaryPrimitives.WriteInt16BigEndian(d, 1);
        BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(2), -2.5f);
        BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(6), 0.125f);
        BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(10), 1.5f);
        Assert.Equal(new ExposureAdjustment(-2.5f, 0.125f, 1.5f), Single("expA", d));
    }

    [Fact]
    public void Vibrance_descriptor_missing_keys_are_zero_and_unknown_keys_survive_editing()
    {
        var desc = new Descriptor
        {
            ClassId = "null",
            Items = [new("vibrance", new IntegerValue(25)), new("someFutureKey", new TextValue("keep me"))],
        };
        var data = Strayta.Psd.Descriptors.DescriptorWriter.WriteVersioned(desc);
        var doc = Build(("vibA", data));
        var layer = (AdjustmentLayer)doc.Root.Children[0];
        Assert.Equal(new VibranceAdjustment(25, 0), layer.Adjustment);

        layer.Adjustment = new VibranceAdjustment(-40, 60);
        var (file, again) = Reload(doc);
        Assert.Equal(new VibranceAdjustment(-40, 60), ((AdjustmentLayer)again.Root.Children[0]).Adjustment);
        var written = DescriptorReader.ReadVersioned(file.Layers[0].FindBlock("vibA")!.Data!);
        Assert.Equal("keep me", written.Text("someFutureKey"));
        Assert.Equal("vibrance", written.Items[0].Key); // existing keys keep their place
    }

    [Fact]
    public void Unedited_blocks_of_every_new_kind_are_kept_byte_for_byte()
    {
        var blocks = new (string, byte[])[]
        {
            ("expA", PsdAdjustmentWriter.Encode(new ExposureAdjustment(1, 0, 1)).Concat(new byte[] { 0, 0 }).ToArray()),
            ("blnc", PsdAdjustmentWriter.Encode(new ColorBalanceAdjustment(new(1, 2, 3), new(4, 5, 6), new(7, 8, 9), true))),
            ("selc", PsdAdjustmentWriter.Encode(SelectiveColorAdjustment.Default.With(SelectiveColorRange.Blues, new(1, 2, 3, 4)))),
            ("mixr", PsdAdjustmentWriter.Encode(ChannelMixerAdjustment.Default).Concat(new byte[] { 1, 2, 3, 4, 5, 6 }).ToArray()),
            ("blwh", PsdAdjustmentWriter.Encode(BlackWhiteAdjustment.Default)),
        };
        var (file, _) = Reload(Build(blocks));
        for (int i = 0; i < blocks.Length; i++)
            Assert.Equal(blocks[i].Item2, file.Layers[i].FindBlock(blocks[i].Item1)!.Data);
    }

    [Fact]
    public void Edited_channel_mixer_keeps_trailing_data()
    {
        var data = PsdAdjustmentWriter.Encode(ChannelMixerAdjustment.Default).Concat(new byte[] { 1, 2, 3, 4, 5, 6 }).ToArray();
        var doc = Build(("mixr", data));
        var layer = (AdjustmentLayer)doc.Root.Children[0];
        var edited = ChannelMixerAdjustment.Default with { Red = new(50, 50, 0, 10) };
        layer.Adjustment = edited;
        var (file, again) = Reload(doc);
        Assert.Equal(edited, ((AdjustmentLayer)again.Root.Children[0]).Adjustment);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, file.Layers[0].FindBlock("mixr")!.Data![^6..]);
    }

    [Fact]
    public void Photo_filter_reads_version_2_color_structures()
    {
        var d = new byte[18];
        BinaryPrimitives.WriteInt16BigEndian(d, 2);
        BinaryPrimitives.WriteInt16BigEndian(d.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(4), 65535);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(8), 32768);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12), 42);
        d[16] = 1;
        var p = Assert.IsType<PhotoFilterAdjustment>(Single("phfl", d));
        Assert.Equal(42, p.Density);
        Assert.True(p.PreserveLuminosity);
        Assert.Equal(1f, p.Color.R);
        Assert.Equal(32768 / 65535f, p.Color.B, 4);
    }

    [Fact]
    public void Photo_filter_version_3_xyz_white_reads_as_white()
    {
        // D50 white in 16.16 fixed point.
        var d = new byte[19];
        BinaryPrimitives.WriteInt16BigEndian(d, 3);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(2), (int)(0.9642 * 65536));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(6), 65536);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(10), (int)(0.8249 * 65536));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(14), 25);
        var p = Assert.IsType<PhotoFilterAdjustment>(Single("phfl", d));
        Assert.Equal(25, p.Density);
        Assert.InRange(p.Color.R, 0.98f, 1f);
        Assert.InRange(p.Color.G, 0.98f, 1f);
        Assert.InRange(p.Color.B, 0.98f, 1f);
    }

    [Fact]
    public void Gradient_map_reads_18_byte_stops_as_in_the_specification()
    {
        var ms = new MemoryStream();
        void W16(int v) { var b = new byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, (short)v); ms.Write(b); }
        void W32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        W16(1); ms.WriteByte(0); ms.WriteByte(1);
        W32(3); ms.Write(Encoding.BigEndianUnicode.GetBytes("ab\0"));
        W16(2);
        W32(0); W32(50); W16(0); W16(0); W16(0); W16(0); W16(0);
        W32(4096); W32(50); W16(0); W16(-1); W16(-1); W16(-1); W16(0);
        W16(2);
        W32(0); W32(50); W16(255);
        W32(4096); W32(50); W16(255);
        W16(2); W16(4096); W16(32); W16(0); W32(0); W16(0); W16(0); W32(2048); W16(3);
        for (int i = 0; i < 8; i++) W16(0);
        W16(0);
        var g = Assert.IsType<GradientMapAdjustment>(Single("grdm", ms.ToArray()));
        Assert.True(g.Dither);
        Assert.Equal("ab", g.Gradient.Name);
        Assert.Equal(2, g.Gradient.Colors.Count);
        Assert.Equal(new RgbColor(1, 1, 1), g.Gradient.Colors[1].Color);
        Assert.Equal(GradientMethod.Classic, g.Method);
    }

    [Fact]
    public void Gradient_map_version_3_carries_the_method()
    {
        var a = new GradientMapAdjustment(GradientModel.TwoColor("x", RgbColor.Black, new RgbColor(1, 1, 1)), false, false, GradientMethod.Linear);
        var data = PsdAdjustmentWriter.Encode(a);
        Assert.Equal(3, BinaryPrimitives.ReadInt16BigEndian(data));
        Assert.Equal("Lnr ", Encoding.ASCII.GetString(data, 4, 4));
        Assert.Equal(a, Single("grdm", data));
        Assert.Equal(1, BinaryPrimitives.ReadInt16BigEndian(PsdAdjustmentWriter.Encode(a with { Method = GradientMethod.Classic })));
    }

    [Fact]
    public void Color_lookup_stores_the_lut_file_and_drops_a_stale_profile_when_it_changes()
    {
        var desc = new Descriptor
        {
            ClassId = "null",
            Items =
            [
                new("lookupType", new EnumValue("colorLookupType", "3DLUT")),
                new("Nm  ", new TextValue("Old.cube")),
                new("Dthr", new BoolValue(true)),
                new("profile", new RawValue("tdta", [1, 2, 3])),
                new("LUTFormat", new EnumValue("LUTFormatType", "LUTFormatCUBE")),
                new("LUT3DFileData", new RawValue("tdta", Encoding.ASCII.GetBytes("LUT_3D_SIZE 2\n"))),
                new("LUT3DFileName", new TextValue("Old.cube")),
            ],
        };
        var body = Strayta.Psd.Descriptors.DescriptorWriter.WriteVersioned(desc);
        var data = new byte[2 + body.Length];
        data[1] = 1;
        body.CopyTo(data, 2);

        var doc = Build(("clrL", data));
        var layer = (AdjustmentLayer)doc.Root.Children[0];
        var read = Assert.IsType<ColorLookupAdjustment>(layer.Adjustment);
        Assert.Equal(("Old.cube", "CUBE", true), (read.Name, read.Format, read.Dither));

        var (unchanged, _) = Reload(doc);
        Assert.Equal(data, unchanged.Layers[0].FindBlock("clrL")!.Data);

        layer.Adjustment = read with { Name = "New.cube", Data = Encoding.ASCII.GetBytes("LUT_3D_SIZE 2\n# new\n") };
        var (file, again) = Reload(doc);
        Assert.Equal(layer.Adjustment, ((AdjustmentLayer)again.Root.Children[0]).Adjustment);
        var written = DescriptorReader.ReadVersioned(file.Layers[0].FindBlock("clrL")!.Data!, 2);
        Assert.False(written.Has("profile"));
    }
}
