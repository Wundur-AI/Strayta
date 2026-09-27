using Strayta.Core;

namespace Strayta.Psd.Tests;

public class PsdReaderTests
{
    private static byte[] Fill(int count, byte value) => Enumerable.Repeat(value, count).ToArray();
    private static byte[] Ramp(int count) => Enumerable.Range(0, count).Select(i => (byte)(i * 7)).ToArray();

    private static PsdTestBuilder.Layer PixelLayer(string name, int l, int t, int r, int b, ushort compression)
    {
        int n = (r - l) * (b - t);
        return new PsdTestBuilder.Layer
        {
            Name = name, Left = l, Top = t, Right = r, Bottom = b, Compression = compression,
            Channels = { [-1] = Fill(n, 255), [0] = Ramp(n), [1] = Fill(n, 40), [2] = Fill(n, 200) },
        };
    }

    private static PsdFile Read(PsdTestBuilder builder) => PsdFile.Read(new MemoryStream(builder.Build()));

    [Fact]
    public void Rejects_files_without_signature()
    {
        var ex = Assert.Throws<PsdFormatException>(() => PsdFile.Read(new MemoryStream(new byte[64])));
        Assert.Contains("8BPS", ex.Message);
    }

    [Fact]
    public void Rejects_truncated_files()
    {
        var bytes = new PsdTestBuilder(4, 4) { Layers = { PixelLayer("A", 0, 0, 4, 4, 0) } }.Build();
        Assert.Throws<PsdFormatException>(() => PsdFile.Read(new MemoryStream(bytes[..60])));
    }

    [Theory]
    [InlineData((ushort)0, false)]
    [InlineData((ushort)1, false)]
    [InlineData((ushort)2, false)]
    [InlineData((ushort)0, true)]
    [InlineData((ushort)1, true)]
    public void Decodes_layer_channels_for_each_compression(ushort compression, bool psb)
    {
        var builder = new PsdTestBuilder(10, 8, psb) { Layers = { PixelLayer("Pixels", 2, 1, 7, 5, compression) } };

        var doc = Read(builder).ToDocument();

        var layer = Assert.IsType<PixelLayer>(Assert.Single(doc.Root.Children));
        Assert.Equal(new PixelRect(2, 1, 7, 5), layer.Bounds);
        var px = Assert.IsType<Raster>(layer.Pixels);
        Assert.Equal(Ramp(20), px.ColorPlanes[0].Data);
        Assert.All(px.ColorPlanes[1].Data, v => Assert.Equal(40, v));
        Assert.All(px.Alpha!.Data, v => Assert.Equal(255, v));
    }

    [Fact]
    public void Maps_layer_properties()
    {
        var layer = PixelLayer("Ünïcode ✓", 0, 0, 2, 2, 0);
        layer.Blend = "mul ";
        layer.Opacity = 128;
        layer.Flags = 0x02 | 0x01; // hidden, transparency locked
        layer.Clipped = true;
        layer.FillOpacity = 51;

        var doc = Read(new PsdTestBuilder(2, 2) { Layers = { layer } }).ToDocument();

        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Root.Children));
        Assert.Equal("Ünïcode ✓", l.Name);
        Assert.Equal(BlendMode.Multiply, l.BlendMode);
        Assert.Equal(128 / 255f, l.Opacity);
        Assert.Equal(51 / 255f, l.FillOpacity);
        Assert.False(l.Visible);
        Assert.True(l.TransparencyLocked);
        Assert.True(l.Clipped);
    }

    [Fact]
    public void Builds_nested_groups_from_section_dividers()
    {
        // Stored bottom-to-top: divider, child, folder(open) ; then a top-level layer above the group.
        var builder = new PsdTestBuilder(4, 4)
        {
            Layers =
            {
                new PsdTestBuilder.Layer { Name = "</Layer group>", SectionType = 3 },
                PixelLayer("Inside", 0, 0, 2, 2, 1),
                new PsdTestBuilder.Layer { Name = "Group", SectionType = 2, SectionBlend = "scrn" },
                PixelLayer("Top", 0, 0, 4, 4, 1),
            },
        };

        var doc = Read(builder).ToDocument();

        Assert.Equal(2, doc.Root.Children.Count);
        var group = Assert.IsType<LayerGroup>(doc.Root.Children[0]);
        Assert.Equal("Group", group.Name);
        Assert.False(group.Expanded);
        Assert.Equal(BlendMode.Screen, group.BlendMode);
        Assert.Equal("Inside", Assert.Single(group.Children).Name);
        Assert.Same(group, group.Children[0].Parent);
        Assert.Equal("Top", doc.Root.Children[1].Name);
    }

    [Fact]
    public void Reads_layer_mask()
    {
        var layer = PixelLayer("Masked", 0, 0, 4, 4, 1);
        layer.Mask = (Top: 1, Left: 1, Bottom: 3, Right: 4, Default: 0, Flags: 0x02);
        layer.Channels[-2] = [0, 128, 255, 10, 20, 30];

        var doc = Read(new PsdTestBuilder(4, 4) { Layers = { layer } }).ToDocument();

        var mask = Assert.IsType<LayerMask>(Assert.IsType<PixelLayer>(doc.Root.Children[0]).Mask);
        Assert.Equal(new PixelRect(1, 1, 4, 3), mask.Bounds);
        Assert.True(mask.Disabled);
        Assert.Equal([0, 128, 255, 10, 20, 30], mask.Pixels!.Data);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    public void Reads_composite_and_removes_white_matte(ushort compression)
    {
        // A 50%-opaque pure red pixel stored over white becomes (255, 128, 128).
        var builder = new PsdTestBuilder(2, 1)
        {
            NegativeLayerCount = true,
            Layers = { PixelLayer("L", 0, 0, 2, 1, 0) },
            CompositeCompression = compression,
            Composite = [[255, 255], [128, 255], [128, 255], [128, 255]],
        };

        var file = Read(builder);
        var comp = Assert.IsType<Raster>(file.ToDocument().Composite);

        Assert.True(file.CompositeHasTransparency);
        Assert.Equal([255, 255], comp.ColorPlanes[0].Data);
        Assert.Equal(2, comp.ColorPlanes[1].Data[0]); // (1/255) / (128/255) * 255 = 1.99 -> 2
        Assert.Equal(255, comp.ColorPlanes[1].Data[1]); // fully opaque pixels are untouched
        Assert.Equal([128, 255], comp.Alpha!.Data);
    }

    [Fact]
    public void Without_negative_layer_count_extra_channels_are_not_transparency()
    {
        var builder = new PsdTestBuilder(1, 1)
        {
            Layers = { PixelLayer("L", 0, 0, 1, 1, 0) },
            Composite = [[10], [20], [30], [99]],
        };

        var comp = Read(builder).ToDocument().Composite!;

        Assert.Null(comp.Alpha);
        Assert.Equal(20, comp.ColorPlanes[1].Data[0]);
    }
}
