using Strayta.Core;

namespace Strayta.Psd.Tests;

public class PsdWriterTests
{
    private static byte[] Ramp(int n, int mul = 7) => Enumerable.Range(0, n).Select(i => (byte)(i * mul)).ToArray();

    private static PsdTestBuilder.Layer Pixel(string name, int l, int t, int r, int b)
    {
        int n = (r - l) * (b - t);
        return new PsdTestBuilder.Layer
        {
            Name = name, Left = l, Top = t, Right = r, Bottom = b, Compression = 1,
            Channels = { [-1] = Ramp(n, 11), [0] = Ramp(n), [1] = Ramp(n, 3), [2] = Ramp(n, 5) },
        };
    }

    private static Document SampleDocument() => PsdFile.Read(new MemoryStream(new PsdTestBuilder(8, 6)
    {
        Layers =
        {
            new PsdTestBuilder.Layer { Name = "</Layer group>", SectionType = 3 },
            Pixel("Inside", 1, 1, 5, 4),
            new PsdTestBuilder.Layer { Name = "Group", SectionType = 1, SectionBlend = "pass" },
            WithBlock(Pixel("Top ✓", 0, 0, 8, 6), "zzzz", [1, 2, 3]),
        },
    }.Build()), new PsdReadOptions { MaxRawBlockBytes = long.MaxValue }).ToDocument();

    private static PsdTestBuilder.Layer WithBlock(PsdTestBuilder.Layer l, string key, byte[] data)
    {
        l.ExtraBlocks.Add((key, data));
        return l;
    }

    private static (PsdFile File, Document Doc) Reload(Document doc, PsdWriteOptions? options = null)
    {
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms, options);
        ms.Position = 0;
        var file = PsdFile.Read(ms, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
        return (file, file.ToDocument());
    }

    [Fact]
    public void Unmodified_document_round_trips_structure_pixels_and_unknown_blocks()
    {
        var doc = SampleDocument();
        var (file, again) = Reload(doc);

        var group = Assert.IsType<LayerGroup>(again.Root.Children[0]);
        Assert.Equal(BlendMode.PassThrough, group.BlendMode);
        var inside = Assert.IsType<PixelLayer>(Assert.Single(group.Children));
        Assert.Equal(new PixelRect(1, 1, 5, 4), inside.Bounds);
        Assert.Equal(Ramp(12, 3), inside.Pixels!.ColorPlanes[1].Data);

        var top = again.Root.Children[1];
        Assert.Equal("Top ✓", top.Name);
        Assert.Equal([1, 2, 3], file.Layers[^1].FindBlock("zzzz")!.Data);
    }

    [Fact]
    public void Saved_files_record_strayta_as_the_writer()
    {
        var (file, _) = Reload(SampleDocument());
        Assert.Equal("Strayta", file.WriterName);
        Assert.False(file.CompositeIsFromPhotoshop);
        Assert.True(file.HasRealMergedData);
    }

    [Fact]
    public void Edits_are_saved()
    {
        var doc = SampleDocument();
        var group = (LayerGroup)doc.Root.Children[0];
        var top = (PixelLayer)doc.Root.Children[1];

        top.Name = "Renamed";
        top.Opacity = 0.5f;
        top.FillOpacity = 0.25f;
        top.BlendMode = BlendMode.Multiply;
        top.Visible = false;
        top.Bounds = new PixelRect(2, 1, 10, 7);  // moved right and down
        doc.Root.Remove(top);
        group.Insert(0, top);                      // now inside the group, below "Inside"
        group.Expanded = false;
        group.BlendMode = BlendMode.Screen;

        var (_, again) = Reload(doc);

        var g = Assert.IsType<LayerGroup>(Assert.Single(again.Root.Children));
        Assert.False(g.Expanded);
        Assert.Equal(BlendMode.Screen, g.BlendMode);
        var moved = Assert.IsType<PixelLayer>(g.Children[0]);
        Assert.Equal(("Renamed", 128 / 255f, 64 / 255f, BlendMode.Multiply, false),
            (moved.Name, moved.Opacity, moved.FillOpacity, moved.BlendMode, moved.Visible));
        Assert.Equal(new PixelRect(2, 1, 10, 7), moved.Bounds);
        Assert.Equal("Inside", g.Children[1].Name);
    }

    [Fact]
    public void New_documents_without_a_source_file_can_be_saved()
    {
        var doc = new Document(3, 2, ColorMode.Rgb, 8);
        Plane P(byte v) => new(3, 2, 8, Enumerable.Repeat(v, 6).ToArray());
        doc.Root.Add(new PixelLayer { Name = "Solid", Bounds = new PixelRect(0, 0, 3, 2), Pixels = new Raster(ColorMode.Rgb, [P(10), P(20), P(30)], P(255)) });
        var composite = new Raster(ColorMode.Rgb, [P(10), P(20), P(30)], P(128));

        var (file, again) = Reload(doc, new PsdWriteOptions { Composite = composite });

        Assert.True(file.CompositeHasTransparency);
        Assert.Equal("Solid", Assert.Single(again.Root.Children).Name);
        Assert.Equal(128, again.Composite!.Alpha!.Data[0]);
        Assert.InRange(again.Composite.ColorPlanes[0].Data[0], 9, 11); // matted over white and back
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void High_bit_depth_layers_round_trip(int depth)
    {
        var doc = new Document(2, 2, ColorMode.Rgb, depth);
        Plane P(float v)
        {
            var p = Plane.Create(2, 2, depth);
            if (depth == 16) p.AsUInt16().Fill((ushort)(v * 65535)); else p.AsSingle().Fill(v);
            return p;
        }
        doc.Root.Add(new PixelLayer { Name = "Deep", Bounds = new PixelRect(0, 0, 2, 2), Pixels = new Raster(ColorMode.Rgb, [P(0.1f), P(0.5f), P(0.9f)], P(1f)) });

        var (file, again) = Reload(doc);

        Assert.Contains(file.GlobalBlocks, b => b.Key == (depth == 16 ? "Lr16" : "Lr32"));
        var px = Assert.IsType<PixelLayer>(Assert.Single(again.Root.Children)).Pixels!;
        Assert.Equal(depth, px.BitDepth);
        Assert.Equal(0.5f, px.ColorPlanes[1].GetNormalized(3), 3);
    }

    [Fact]
    public void Psb_sources_are_saved_as_psb()
    {
        var bytes = new PsdTestBuilder(4, 4, psb: true) { Layers = { Pixel("L", 0, 0, 4, 4) } }.Build();
        var doc = PsdFile.Read(new MemoryStream(bytes)).ToDocument();

        var (file, again) = Reload(doc);

        Assert.True(file.Header.IsPsb);
        Assert.Equal(Ramp(16), ((PixelLayer)again.Root.Children[0]).Pixels!.ColorPlanes[0].Data);
    }

    [Fact]
    public void Failed_save_leaves_the_existing_file_untouched()
    {
        string path = Path.Combine(Path.GetTempPath(), $"strayta-{Guid.NewGuid():N}.psd");
        File.WriteAllText(path, "original");
        try
        {
            var cmyk = new Document(1, 1, ColorMode.Cmyk, 8);
            Assert.Throws<NotSupportedException>(() => PsdWriter.Save(cmyk, path));
            Assert.Equal("original", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".saving"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
