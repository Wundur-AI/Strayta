using System.Buffers.Binary;
using System.Text;

namespace Strayta.Psd.Tests;

/// <summary>Brush files (.abr) built by hand from the format description (no third-party files).</summary>
public class AbrReaderTests
{
    private sealed class Writer
    {
        private readonly MemoryStream _o = new();
        public byte[] ToArray() => _o.ToArray();
        public int Length => (int)_o.Length;
        public Writer U8(int v) { _o.WriteByte((byte)v); return this; }
        public Writer U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); _o.Write(b); return this; }
        public Writer U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _o.Write(b); return this; }
        public Writer I32(int v) => U32((uint)v);
        public Writer Bytes(byte[] b) { _o.Write(b); return this; }
        public Writer Ascii(string s) => Bytes(Encoding.ASCII.GetBytes(s));
        public Writer Unicode(string s) { U32((uint)s.Length + 1); Bytes(Encoding.BigEndianUnicode.GetBytes(s + "\0")); return this; }
        public Writer Zeros(int n) => Bytes(new byte[n]);
    }

    /// <summary>A w × h tip: a gradient from 0 at the left to 255 at the right.</summary>
    private static byte[] Tip(int w, int h)
    {
        var t = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) t[y * w + x] = (byte)(x * 255 / Math.Max(1, w - 1));
        return t;
    }

    /// <summary>PackBits rows (literal runs only, which every decoder accepts), with their 2-byte counts first.</summary>
    private static byte[] Rle(byte[] image, int w, int h)
    {
        var rows = new List<byte[]>();
        for (int y = 0; y < h; y++)
        {
            var row = new List<byte>();
            for (int x = 0; x < w; x += 128)
            {
                int n = Math.Min(128, w - x);
                row.Add((byte)(n - 1));
                row.AddRange(image.AsSpan(y * w + x, n).ToArray());
            }
            rows.Add([.. row]);
        }
        var o = new Writer();
        foreach (var r in rows) o.U16(r.Length);
        foreach (var r in rows) o.Bytes(r);
        return o.ToArray();
    }

    private static Writer Bounds(Writer w, int width, int height, int depth, int compression) =>
        w.I32(0).I32(0).I32(height).I32(width).U16(depth).U8(compression);

    [Fact]
    public void Version_1_reads_computed_and_raw_sampled_brushes()
    {
        var computed = new Writer().U32(0).U16(30).U16(45).U16(60).U16(unchecked((ushort)-30)).U16(70);
        var sampled = Bounds(new Writer().U32(0).U16(0).U8(1).Zeros(8), 5, 3, 8, 0).Bytes(Tip(5, 3));
        var file = new Writer().U16(1).U16(2)
            .U16(1).U32((uint)computed.Length).Bytes(computed.ToArray())
            .U16(2).U32((uint)sampled.Length).Bytes(sampled.ToArray());

        var brushes = AbrReader.Read(file.ToArray(), "Old.abr");
        Assert.Equal(2, brushes.Count);
        var round = brushes[0];
        Assert.Null(round.Tip);
        Assert.Equal((45f, 0.7f, 30f, -30f, 0.6f), (round.Diameter, round.Hardness, round.SpacingPercent, round.Angle, round.Roundness));
        Assert.Equal("Old 1", round.Name);
        var tip = brushes[1].Tip!;
        Assert.Equal((5, 3), (tip.Width, tip.Height));
        Assert.Equal(Tip(5, 3), tip.Alpha);
        Assert.Equal(25f, brushes[1].SpacingPercent); // 0 means the default
        Assert.Equal(5f, brushes[1].Diameter);
    }

    [Fact]
    public void Version_2_reads_names_and_packbits_tips_and_skips_damaged_brushes()
    {
        var image = Tip(200, 4); // wider than one PackBits run
        var sampled = Bounds(new Writer().U32(0).U16(10).Unicode("Chalk 23").U8(1).Zeros(8), 200, 4, 8, 1).Bytes(Rle(image, 200, 4));
        var broken = Bounds(new Writer().U32(0).U16(10).Unicode("Broken").U8(1).Zeros(8), 50, 50, 8, 0).Bytes(new byte[10]); // too little data
        var file = new Writer().U16(2).U16(3)
            .U16(2).U32((uint)broken.Length).Bytes(broken.ToArray())
            .U16(2).U32((uint)sampled.Length).Bytes(sampled.ToArray())
            .U16(7).U32(4).Zeros(4); // an unknown brush type

        var brushes = AbrReader.Read(file.ToArray());
        var chalk = Assert.Single(brushes);
        Assert.Equal("Chalk 23", chalk.Name);
        Assert.Equal(10f, chalk.SpacingPercent);
        Assert.Equal(image, chalk.Tip!.Alpha);
    }

    private static byte[] Sample(string id, int sub, int w, int h, int depth, bool rle)
    {
        var image = Tip(w, h);
        byte[] data;
        if (depth == 16)
        {
            data = new byte[w * h * 2];
            for (int i = 0; i < w * h; i++) (data[i * 2], data[i * 2 + 1]) = (image[i], 0x7F);
        }
        else data = rle ? Rle(image, w, h) : image;
        var item = new Writer().U8(id.Length).Ascii(id).Zeros(sub == 1 ? 10 : 264);
        Bounds(item, w, h, depth, rle ? 1 : 0).Bytes(data);
        var o = new Writer().U32((uint)item.Length).Bytes(item.ToArray());
        return o.Zeros((4 - item.Length % 4) % 4).ToArray();
    }

    private static byte[] Section(string key, byte[] data) =>
        new Writer().Ascii("8BIM").Ascii(key).U32((uint)data.Length).Bytes(data).ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Version_6_reads_tips_and_presets_from_the_descriptor(int sub)
    {
        const string first = "$0a1b2c3d-0000-0000-0000-000000000001", second = "$0a1b2c3d-0000-0000-0000-000000000002";
        var samp = new Writer().Bytes(Sample(first, sub, 7, 5, 8, rle: true)).Bytes(Sample(second, sub, 3, 3, 16, rle: false)).ToArray();

        var d = new DescriptorWriter();
        d.Descriptor("null", w => w.ListOfObjects("Brsh", "brushPreset",
            (2, p => p.Text("Nm  ", "Spatter 39").Object("Brsh", "sampledBrush", 5, b => b
                .Unit("Dmtr", "#Pxl", 39).Unit("Angl", "#Ang", 30).Unit("Rndn", "#Prc", 80).Unit("Spcn", "#Prc", 40).Text("sampledData", first))),
            (2, p => p.Text("Nm  ", "Hard Round 12").Object("Brsh", "computedBrush", 5, b => b
                .Unit("Dmtr", "#Pxl", 12).Unit("Hrdn", "#Prc", 100).Unit("Angl", "#Ang", 0).Unit("Rndn", "#Prc", 100).Unit("Spcn", "#Prc", 25))),
            (2, p => p.Text("Nm  ", "Missing tip").Object("Brsh", "sampledBrush", 1, b => b.Text("sampledData", "$nope")))), 1);
        var desc = new Writer().U32(16).Bytes(d.ToArray()).ToArray();

        var file = new Writer().U16(6).U16(sub)
            .Bytes(Section("samp", samp))
            .Bytes(Section("patt", []))
            .Bytes(Section("desc", desc));

        var brushes = AbrReader.Read(file.ToArray(), "Set.abr");
        Assert.Equal(["Spatter 39", "Hard Round 12", "Sampled Brush 2"], brushes.Select(b => b.Name));
        var spatter = brushes[0];
        Assert.Equal((39f, 30f, 0.8f, 40f), (spatter.Diameter, spatter.Angle, spatter.Roundness, spatter.SpacingPercent));
        Assert.Equal(Tip(7, 5), spatter.Tip!.Alpha);
        Assert.Null(brushes[1].Tip);
        Assert.Equal((12f, 1f), (brushes[1].Diameter, brushes[1].Hardness));
        Assert.Equal(Tip(3, 3), brushes[2].Tip!.Alpha); // the 16-bit tip keeps its high bytes
    }

    [Fact]
    public void Version_6_without_a_descriptor_lists_its_tips()
    {
        var file = new Writer().U16(10).U16(2).Bytes(Section("samp", Sample("$x", 2, 4, 4, 8, rle: false)));
        var brush = Assert.Single(AbrReader.Read(file.ToArray()));
        Assert.Equal(4f, brush.Diameter);
        Assert.NotNull(brush.Tip);
    }

    [Fact]
    public void Unknown_versions_are_refused()
    {
        Assert.Throws<PsdFormatException>(() => AbrReader.Read(new Writer().U16(3).U16(0).ToArray()));
        Assert.Throws<PsdFormatException>(() => AbrReader.Read([1]));
    }
}
