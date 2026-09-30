using Strayta.Core;

namespace Strayta.Psd.Tests;

public class ArtboardTests
{
    private static Raster Solid(int w, int h, byte r, byte g, byte b)
    {
        var planes = new[] { r, g, b }.Select(v =>
        {
            var p = Plane.Create(w, h, 8);
            Array.Fill(p.Data, v);
            return p;
        }).ToArray();
        return new Raster(ColorMode.Rgb, planes, null);
    }

    private static Document TwoArtboards()
    {
        var doc = new Document(600, 400, ColorMode.Rgb, 8);
        var a = new LayerGroup { Name = "Phone", Artboard = new Artboard { Rect = new PixelRect(10, 20, 210, 380), PresetName = "iPhone SE" } };
        a.Add(new PixelLayer { Name = "red", Bounds = new PixelRect(50, 50, 100, 100), Pixels = Solid(50, 50, 255, 0, 0) });
        var b = new LayerGroup
        {
            Name = "Web",
            Artboard = new Artboard { Rect = new PixelRect(250, 20, 590, 300), Background = ArtboardBackground.Custom, Color = (10, 200, 30) },
        };
        b.Add(new PixelLayer { Name = "blue", Bounds = new PixelRect(300, 50, 400, 100), Pixels = Solid(100, 50, 0, 0, 255) });
        doc.Root.Add(a);
        doc.Root.Add(b);
        return doc;
    }

    private static (byte[] Bytes, PsdFile File, Document Doc) Save(Document doc)
    {
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        var bytes = ms.ToArray();
        var file = PsdFile.Read(new MemoryStream(bytes), new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
        return (bytes, file, file.ToDocument());
    }

    [Fact]
    public void Artboards_made_in_Strayta_are_saved_and_read_back()
    {
        var doc = TwoArtboards();
        var (_, file, again) = Save(doc);

        var groups = again.Root.Children.OfType<LayerGroup>().ToList();
        Assert.Equal(2, groups.Count);
        Assert.Equal(doc.Root.Children.OfType<LayerGroup>().Select(g => g.Artboard), groups.Select(g => g.Artboard));
        // The artboard block sits on the group's folder record, and the document gets Photoshop's 'artd' settings.
        var folder = file.Layers.Single(l => l.Name == "Phone" && l.SectionType != PsdSectionType.BoundingDivider);
        Assert.NotNull(folder.FindBlock("artb"));
        Assert.Contains(file.GlobalBlocks, b => b.Key == "artd");
    }

    [Fact]
    public void An_unchanged_artboard_file_saves_byte_for_byte()
    {
        var (first, _, again) = Save(TwoArtboards());
        var (second, _, _) = Save(again);
        // The version resource is rewritten identically, the composite is the stored one: the files match exactly.
        Assert.Equal(first, second);
    }

    [Fact]
    public void Stored_artboard_blocks_keep_their_bytes_and_their_unknown_items()
    {
        // A block as Photoshop writes it, with fractional edges and guide indices plus an item Strayta does not model.
        var stored = new DescriptorWriter();
        stored.Descriptor("artboard", d => d
            .Object("artboardRect", "classFloatRect", 4, r => r.Double("Top ", 0).Double("Left", 0).Double("Btom", 300.4).Double("Rght", 200))
            .ListOfLongs("guideIndeces", 3, 7)
            .Text("artboardPresetName", "Custom")
            .Object("Clr ", "RGBC", 3, c => c.Double("Rd  ", 255).Double("Grn ", 255).Double("Bl  ", 255))
            .Long("artboardBackgroundType", 1)
            .Bool("futureThing", true), 6);
        byte[] block = [0, 0, 0, 16, .. stored.ToArray()];

        var builder = new PsdTestBuilder(300, 400);
        builder.Layers.Add(new PsdTestBuilder.Layer { Name = "</Layer group>", SectionType = 3 });
        builder.Layers.Add(new PsdTestBuilder.Layer { Name = "Artboard 1", SectionType = 1, SectionBlend = "pass", ExtraBlocks = [("artb", block)] });
        var file = PsdFile.Read(new MemoryStream(builder.Build()), new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
        var doc = file.ToDocument();
        var group = Assert.IsType<LayerGroup>(Assert.Single(doc.Root.Children));
        Assert.Equal(new PixelRect(0, 0, 200, 300), group.Artboard!.Rect);
        Assert.Equal("Custom", group.Artboard.PresetName);

        var (_, saved, _) = Save(doc);
        Assert.Equal(block, saved.Layers.Single(l => l.SectionType == PsdSectionType.OpenFolder).FindBlock("artb")!.Data);

        // Edited: the modeled items change, guide indices and the unknown item stay.
        group.Artboard = group.Artboard with { Rect = new PixelRect(5, 0, 205, 300), Background = ArtboardBackground.Black };
        var (_, edited, reread) = Save(doc);
        var data = edited.Layers.Single(l => l.SectionType == PsdSectionType.OpenFolder).FindBlock("artb")!.Data!;
        var descriptor = Descriptors.DescriptorReader.ReadVersioned(data);
        Assert.Equal(2, descriptor.List("guideIndeces")!.Count);
        Assert.True(descriptor.Bool("futureThing"));
        Assert.Equal(group.Artboard, ((LayerGroup)reread.Root.Children[0]).Artboard);
        // The document settings block is added once; a file that has one keeps it and gets no second.
        Assert.Single(edited.GlobalBlocks, b => b.Key == "artd");
        Assert.Single(Save(reread).File.GlobalBlocks, b => b.Key == "artd");
    }

    [Fact]
    public void A_group_that_stops_being_an_artboard_loses_its_block()
    {
        var doc = TwoArtboards();
        var (_, _, again) = Save(doc);
        ((LayerGroup)again.Root.Children[0]).Artboard = null;
        var (_, file, reread) = Save(again);
        Assert.Null(((LayerGroup)reread.Root.Children[0]).Artboard);
        Assert.NotNull(((LayerGroup)reread.Root.Children[1]).Artboard);
        Assert.Single(file.Layers, l => l.FindBlock("artb") is not null);
    }
}
