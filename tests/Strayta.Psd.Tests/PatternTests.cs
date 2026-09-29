using System.Buffers.Binary;
using System.Text;
using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Psd.Tests;

public class PatternTests
{
    // ---- Hand-built blocks, written from the specification independently of PsdPatterns ----------------

    private static void U32(Stream s, uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
    private static void U16(Stream s, ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }

    /// <summary>One RGB pattern with raw 8-bit channels (and optionally a sheet mask), as a length-prefixed, 4-aligned entry.</summary>
    private static byte[] HandBuiltPattern(string name, string id, int w, int h, byte[][] channels, byte[]? alpha, bool alphaInUserMaskSlot = false)
    {
        var p = new MemoryStream();
        U32(p, 1); // version
        U32(p, 3); // RGB
        U16(p, (ushort)h);
        U16(p, (ushort)w);
        U32(p, (uint)name.Length);
        p.Write(Encoding.BigEndianUnicode.GetBytes(name));
        p.WriteByte((byte)id.Length);
        p.Write(Encoding.ASCII.GetBytes(id));

        var list = new MemoryStream();
        foreach (int v in new[] { 0, 0, h, w }) U32(list, (uint)v);
        U32(list, 3);
        void Array(byte[]? data)
        {
            if (data is null)
            {
                U32(list, 0);
                return;
            }
            U32(list, 1);
            U32(list, (uint)(23 + data.Length));
            U32(list, 8);
            foreach (int v in new[] { 0, 0, h, w }) U32(list, (uint)v);
            U16(list, 8);
            list.WriteByte(0); // raw
            list.Write(data);
        }
        foreach (var c in channels) Array(c);
        Array(alphaInUserMaskSlot ? alpha : null);
        Array(alphaInUserMaskSlot ? null : alpha);
        U32(p, 3);
        U32(p, (uint)list.Length);
        list.Position = 0;
        list.CopyTo(p);

        var entry = new MemoryStream();
        U32(entry, (uint)p.Length);
        p.Position = 0;
        p.CopyTo(entry);
        while (entry.Length % 4 != 0) entry.WriteByte(0);
        return entry.ToArray();
    }

    [Fact]
    public void Reads_hand_built_patterns_with_and_without_transparency()
    {
        byte[] r = [255, 0, 10, 20, 30, 40], g = [0, 255, 11, 21, 31, 41], b = [0, 0, 12, 22, 32, 42], a = [255, 128, 0, 255, 255, 255];
        var block = HandBuiltPattern("Stripes", "11111111-2222-3333-4444-555555555555", 3, 2, [r, g, b], null)
            .Concat(HandBuiltPattern("Odd", "abc", 3, 2, [r, g, b], a))
            .Concat(HandBuiltPattern("User slot", "def", 3, 2, [r, g, b], a, alphaInUserMaskSlot: true)).ToArray();
        var patterns = PsdPatterns.Decode(block);

        Assert.Equal(3, patterns.Count);
        var first = patterns[0];
        Assert.Equal(("Stripes", "11111111-2222-3333-4444-555555555555", 3, 2), (first.Name, first.Id, first.Width, first.Height));
        Assert.Null(first.Pixels.Alpha);
        Assert.Equal(r, first.Pixels.ColorPlanes[0].Data);
        Assert.Equal(b, first.Pixels.ColorPlanes[2].Data);
        Assert.Equal(a, patterns[1].Pixels.Alpha!.Data);
        Assert.Equal(a, patterns[2].Pixels.Alpha!.Data);
    }

    [Fact]
    public void A_damaged_pattern_is_skipped_and_the_rest_still_read()
    {
        var good = HandBuiltPattern("Good", "g", 1, 1, [[1], [2], [3]], null);
        var bad = new byte[] { 0, 0, 0, 8, 0, 0, 0, 1, 0, 0, 0, 3 }; // declares 8 bytes: version and mode only
        var patterns = PsdPatterns.Decode(bad.Concat(good).ToArray());
        Assert.Equal("Good", Assert.Single(patterns).Name);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Encoded_patterns_decode_to_the_same_pixels(int depth)
    {
        var original = PatternLibrary.BuiltIn.First(p => p.Pixels.Alpha is not null);
        var decoded = Assert.Single(PsdPatterns.Decode(PsdPatterns.Encode([original], depth)));
        Assert.Equal((original.Id, original.Name, original.Width, original.Height), (decoded.Id, decoded.Name, decoded.Width, decoded.Height));
        Assert.Equal(depth, decoded.Pixels.BitDepth);
        for (int i = 0; i < original.Width * original.Height; i++)
        {
            Assert.Equal(original.Pixels.ColorPlanes[1].GetNormalized(i), decoded.Pixels.ColorPlanes[1].GetNormalized(i), 3);
            Assert.Equal(original.Pixels.Alpha!.GetNormalized(i), decoded.Pixels.Alpha!.GetNormalized(i), 3);
        }
    }

    // ---- References and writing ---------------------------------------------------------------------

    /// <summary>A PtFl (pattern fill) block pointing at a pattern by name and ID.</summary>
    private static byte[] PatternFillBlock(string name, string id)
    {
        var w = new DescriptorWriter();
        w.Descriptor("null", d => d.Object("Ptrn", "Ptrn", 2, p => p.Text("Nm  ", name).Text("Idnt", id)), 1);
        var body = w.ToArray();
        var o = new MemoryStream();
        U32(o, 16);
        o.Write(body);
        return o.ToArray();
    }

    private static Document FileWithPatternFillLayer(string id)
    {
        var b = new PsdTestBuilder(8, 8);
        var layer = new PsdTestBuilder.Layer { Name = "Pattern Fill 1", Right = 8, Bottom = 8 };
        foreach (short c in new short[] { -1, 0, 1, 2 }) layer.Channels[c] = Enumerable.Repeat((byte)200, 64).ToArray();
        layer.ExtraBlocks.Add(("PtFl", PatternFillBlock("Mine", id)));
        b.Layers.Add(layer);
        b.Composite = [new byte[64], new byte[64], new byte[64]];
        return PsdFile.Read(new MemoryStream(b.Build()), new PsdReadOptions { MaxRawBlockBytes = long.MaxValue }).ToDocument();
    }

    private static PsdFile Save(Document doc)
    {
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        return PsdFile.Read(ms, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
    }

    [Fact]
    public void Finds_pattern_references_in_layer_data()
    {
        var doc = FileWithPatternFillLayer("my-id");
        var reference = Assert.Single(PsdPatterns.References((PsdFile)doc.SourceData!));
        Assert.Equal(new PsdPatternReference("Pattern Fill 1", "PtFl", "Mine", "my-id"), reference);
    }

    [Fact]
    public void A_referenced_pattern_is_written_and_read_back_and_unreferenced_ones_are_not()
    {
        var used = PatternLibrary.BuiltIn[0];
        var doc = FileWithPatternFillLayer(used.Id);
        doc.Patterns.Add(used);
        doc.Patterns.Add(PatternLibrary.BuiltIn[1]); // defined but used by no layer

        var saved = Save(doc);
        var block = Assert.Single(saved.GlobalBlocks, g => g.Key == "Patt");
        Assert.Equal(0, block.Data!.Length % 4);
        var reopened = saved.ToDocument();
        var pattern = Assert.Single(reopened.Patterns);
        Assert.Equal(used.Id, pattern.Id);
        Assert.Equal(used.Pixels.ColorPlanes[0].Data, pattern.Pixels.ColorPlanes[0].Data);

        // Saving again keeps the block byte for byte and adds nothing.
        var again = Save(reopened);
        Assert.Equal(block.Data, Assert.Single(again.GlobalBlocks, g => g.Key == "Patt").Data);
    }

    [Fact]
    public void New_patterns_are_appended_after_the_existing_block_bytes()
    {
        var first = PatternLibrary.BuiltIn[0];
        var doc = FileWithPatternFillLayer(first.Id);
        doc.Patterns.Add(first);
        var saved = Save(doc).ToDocument();
        var original = ((PsdFile)saved.SourceData!).GlobalBlocks.Single(g => g.Key == "Patt").Data!;

        // Point the layer at a second pattern too (as an edited layer style would), then save.
        var second = PatternLibrary.BuiltIn[2];
        var record = (PsdLayerRecord)saved.Root.Children[0].SourceData!;
        saved.Root.Children[0].SourceData = record.WithBlocks([.. record.Blocks, new TaggedBlock("8BIM", "vstk", 0, 0, PatternFillBlock("Second", second.Id))], record.Mask);
        saved.Patterns.Add(second);
        var resaved = Save(saved);
        var data = resaved.GlobalBlocks.Single(g => g.Key == "Patt").Data!;
        Assert.Equal(original, data[..original.Length]);
        Assert.Equal([first.Id, second.Id], PsdPatterns.Decode(data).Select(p => p.Id));
    }

    [Fact]
    public void Sixteen_bit_documents_store_patterns_in_Pat2()
    {
        Assert.Equal("Patt", PsdPatterns.KeyFor(8));
        Assert.Equal("Pat2", PsdPatterns.KeyFor(16));
        Assert.Equal("Pat3", PsdPatterns.KeyFor(32));
    }
}
