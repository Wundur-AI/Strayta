using Strayta.Core;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd.Tests;

/// <summary>
/// Smart objects in the file: linked-files entries read and written exactly, placed-layer blocks built and edited,
/// warps and smart filters read, and unedited smart objects kept byte for byte through a save.
/// </summary>
public class SmartObjectTests
{
    private static readonly (double X, double Y)[] Corners = [(10, 20), (110, 20), (110, 70), (10, 70)];

    private static byte[] Payload(int n, int seed) => Enumerable.Range(0, n).Select(i => (byte)(i * 31 + seed)).ToArray();

    [Fact]
    public void Linked_files_blocks_read_back_and_re_encode_identically()
    {
        var embedded = PsdSmartObjects.NewEmbedded("id-1", "a.png", "png ", Payload(37, 1));
        var v8 = PsdSmartObjects.NewEmbedded("id-2", "Design.psb", "8BPB", Payload(12, 2)) with
        {
            Version = 8, ChildDocumentId = "xmp.did:1", ContentDescriptor = PsdLinkedFile.NewContentDescriptor(null),
        };
        var external = new PsdLinkedFile
        {
            Kind = "liFE", Version = 7, UniqueId = "id-3", Name = "far.png", FileType = "png ",
            LinkDescriptor = new Descriptor { ClassId = "ExternalFileLink", Items = [new("fullPath", new TextValue("file:///tmp/far.png"))] },
            FileDate = (2024, 5, 6, 7, 8, 9.5), ExternalSize = 1234,
        };
        var block = PsdLinkedFiles.Write([embedded, v8, external]);
        Assert.True(PsdLinkedFiles.IsFullyReadable(block));
        var read = PsdLinkedFiles.Read(block);
        Assert.Equal(["id-1", "id-2", "id-3"], read.Select(e => e.UniqueId));
        Assert.Equal(embedded.Data, read[0].Data);
        Assert.Equal(v8.ContentId, read[1].ContentId);
        Assert.Equal("/tmp/far.png", read[2].ExternalPath);
        Assert.Equal((2024, 5, 6, 7, 8, 9.5), read[2].FileDate);
        Assert.Equal(1234, read[2].ExternalSize);
        // Encoding the parsed fields gives the same bytes as the raw entries.
        Assert.Equal(block, PsdLinkedFiles.Write(read.Select(e => e with { Raw = null })));
        Assert.Equal(block, PsdLinkedFiles.Write(read));
    }

    [Fact]
    public void New_content_keeps_the_id_updates_the_size_and_a_newer_entry_gets_a_new_content_id()
    {
        var v8 = PsdSmartObjects.NewEmbedded("id-2", "Design.psb", "8BPB", Payload(12, 2)) with { Version = 8, ContentDescriptor = PsdLinkedFile.NewContentDescriptor(null) };
        var entry = PsdLinkedFiles.Read(PsdLinkedFiles.Write([v8]))[0];
        var changed = PsdLinkedFiles.Read(PsdLinkedFiles.Write([entry.WithData(Payload(99, 3))]))[0];
        Assert.Equal("id-2", changed.UniqueId);
        Assert.Equal(99, changed.Data!.Length);
        Assert.NotEqual(entry.ContentId, changed.ContentId);
        Assert.NotNull(changed.ContentId);
    }

    private static PsdFile FileWith(params PsdLinkedFile[] entries)
    {
        var block = PsdLinkedFiles.Write(entries);
        return new PsdFile
        {
            Header = new PsdHeader(1, 3, 200, 100, 8, ColorMode.Rgb),
            GlobalBlocks = [new TaggedBlock("8BIM", "lnk2", 0, block.Length, block), new TaggedBlock("8BIM", "Patt", 0, 4, [1, 2, 3, 4])],
        };
    }

    [Fact]
    public void Replacing_one_entry_keeps_the_others_and_other_blocks_byte_for_byte()
    {
        var file = FileWith(PsdSmartObjects.NewEmbedded("a", "a.png", "png ", Payload(10, 1)), PsdSmartObjects.NewEmbedded("b", "b.png", "png ", Payload(20, 2)));
        var before = PsdLinkedFiles.Read(file.GlobalBlocks[0].Data!);
        var updated = PsdSmartObjects.WithLinkedFile(file, PsdSmartObjects.FindLinkedFile(file, "b")!.WithData(Payload(5, 9), "b.psb", "8BPB"));
        var after = PsdLinkedFiles.Read(updated.GlobalBlocks[0].Data!);
        Assert.Equal(before[0].Raw, after[0].Raw);
        Assert.Equal(("b.psb", "8BPB", 5), (after[1].Name, after[1].FileType, after[1].Data!.Length));
        Assert.Same(file.GlobalBlocks[1], updated.GlobalBlocks[1]);

        var added = PsdSmartObjects.WithLinkedFile(updated, PsdSmartObjects.NewEmbedded("c", "c.psb", "8BPB", Payload(3, 3)));
        Assert.Equal(["a", "b", "c"], PsdLinkedFiles.Read(added.GlobalBlocks[0].Data!).Select(e => e.UniqueId));
        var fresh = PsdSmartObjects.WithLinkedFile(new PsdFile { Header = file.Header }, PsdSmartObjects.NewEmbedded("c", "c.psb", "8BPB", Payload(3, 3)));
        Assert.Equal("lnk2", fresh.GlobalBlocks.Single().Key);
    }

    private static PsdLayerRecord NewRecord(double w = 100, double h = 50)
    {
        var placed = PsdSmartObjects.NewPlaced("id-1", "p-1", w, h, Corners, 72);
        return PsdSmartObjects.WithPlaced(new PsdLayerRecord { Blocks = [new TaggedBlock("8BIM", "PlLd", 0, 0, [])] }, placed);
    }

    [Fact]
    public void A_new_placed_layer_reads_back_with_its_legacy_block()
    {
        var record = NewRecord();
        var so = PsdLiveContent.ReadSmartObject(record)!;
        Assert.Equal(("id-1", 100.0, 50.0, false), (so.UniqueId, so.Width, so.Height, so.Warped));
        Assert.Equal(Corners, so.Corners);
        var legacy = record.FindBlock("PlLd")!.Data!;
        Assert.Equal("plcL"u8.ToArray(), legacy[..4]);
        // The legacy block's corners move with a canvas change like Photoshop's own.
        var moved = PsdLiveContent.TransformPlacedLegacy(legacy, CanvasMap.Translation(5, 0));
        Assert.NotEqual(legacy, moved);
        var warp = DescriptorReader.ReadVersioned(legacy, 8 + 1 + 4 + 16 + 64 + 4);
        Assert.Equal("warpNone", warp.Enum("warpStyle"));
    }

    [Fact]
    public void New_ids_and_content_size_keep_the_placement()
    {
        var record = NewRecord();
        var copy = PsdSmartObjects.WithIds(record, "id-9", "p-9");
        Assert.Equal("id-9", PsdLiveContent.ReadSmartObject(copy)!.UniqueId);
        Assert.Equal("id-9"u8.ToArray(), copy.FindBlock("PlLd")!.Data![9..13]);

        // Twice the content: same scale (1 document pixel per content pixel), centered on the old place.
        var bigger = PsdLiveContent.ReadSmartObject(PsdSmartObjects.WithContentSize(record, 200, 100))!;
        Assert.Equal((200.0, 100.0), (bigger.Width, bigger.Height));
        Assert.Equal((-40.0, -5.0), (Math.Round(bigger.Corners[0].X, 6), Math.Round(bigger.Corners[0].Y, 6)));
        Assert.Equal((160.0, 95.0), (Math.Round(bigger.Corners[2].X, 6), Math.Round(bigger.Corners[2].Y, 6)));
        Assert.Same(record, PsdSmartObjects.WithContentSize(record, 100, 50));
    }

    [Fact]
    public void Warps_are_read_from_styles_custom_meshes_split_meshes_and_cylinders()
    {
        var custom = new WarpSpec
        {
            Style = "warpCustom", Bounds = (0, 0, 100, 50), Mesh = Enumerable.Range(0, 16).Select(i => ((i % 4) * 33.0, (i / 4) * 16.0 + (i % 4 == 1 ? 5 : 0))).ToArray(),
        };
        var read = PsdLiveContent.ReadWarp(PsdSmartObjects.WarpDescriptor(custom))!;
        Assert.Equal(("warpCustom", 16), (read.Style, read.Mesh!.Count));
        Assert.Equal(custom.Mesh, read.Mesh);

        var split = custom with { Rows = 4, Columns = 7, Mesh = Enumerable.Range(0, 28).Select(i => ((double)i, (double)i)).ToArray(), SlicesX = [0, 40, 100], SlicesY = [0, 50] };
        var readSplit = PsdLiveContent.ReadWarp(PsdSmartObjects.WarpDescriptor(split))!;
        Assert.Equal((4, 7), (readSplit.Rows, readSplit.Columns));
        Assert.Equal([0.0, 40, 100], readSplit.SlicesX);

        var arc = PsdLiveContent.ReadWarp(PsdSmartObjects.WarpDescriptor(new WarpSpec { Style = "warpArc", Value = 50, Bounds = (0, 0, 10, 10), Vertical = true }))!;
        Assert.Equal(("warpArc", 50.0, true), (arc.Style, arc.Value, arc.Vertical));
        Assert.False(arc.IsNone);

        // A placed layer whose "quiltWarp" is set uses it over its plain "warp".
        var placed = PsdSmartObjects.NewPlaced("id", "p", 100, 50, Corners, 72).With("quiltWarp", new ObjectValue(PsdSmartObjects.WarpDescriptor(split)));
        var so = PsdLiveContent.ReadSmartObject(PsdSmartObjects.WithPlaced(new PsdLayerRecord(), placed))!;
        Assert.True(so.Warped);
        Assert.Equal(7, so.Warp!.Columns);
    }

    [Fact]
    public void Smart_filters_are_written_and_read_back_in_order()
    {
        var gauss = PsdSmartObjects.NewFilter("Gaussian Blur", new Descriptor { ClassId = "GsnB", Items = [new("Rds ", new UnitFloatValue("#Pxl", 4))] });
        var noise = PsdSmartObjects.NewFilter("Add Noise", new Descriptor { ClassId = "AdNs", Items = [new("Nose", new UnitFloatValue("#Prc", 12))] }) with
        {
            Enabled = false, BlendMode = BlendMode.Screen, Opacity = 0.4,
        };
        var odd = new PsdSmartFilter("Liquify", "LqFy", true, BlendMode.Normal, 1, new Descriptor { ClassId = "LqFy" }, PsdSmartObjects.NewFilter("Liquify", new Descriptor { ClassId = "LqFy" }).Source);
        var record = NewRecord();
        var placed = PsdSmartObjects.WithFilters(PsdSmartObjects.ReadPlaced(record)!, PsdSmartObjects.NewStack() with { Filters = [gauss, noise, odd] });
        var saved = PsdSmartObjects.WithPlaced(record, placed);
        var stack = PsdSmartObjects.ReadFilters(PsdSmartObjects.ReadPlaced(saved)!)!;
        Assert.Equal(["GsnB", "AdNs", "LqFy"], stack.Filters.Select(f => f.FilterClass));
        Assert.Equal(4, stack.Filters[0].Settings!.Number("Rds "));
        Assert.Equal((false, BlendMode.Screen, 0.4), (stack.Filters[1].Enabled, stack.Filters[1].BlendMode, stack.Filters[1].Opacity));
        Assert.True(PsdLiveContent.ReadSmartObject(saved)!.HasFilters);
        Assert.Equal(1198747202, stack.Filters[0].Source.Number("filterID"));
        // "filterFX" sits before "Sz  ", as Photoshop writes it.
        var keys = PsdSmartObjects.ReadPlaced(saved)!.Items.Select(kv => kv.Key).ToList();
        Assert.True(keys.IndexOf("filterFX") < keys.IndexOf("Sz  "));
        Assert.False(PsdLiveContent.ReadSmartObject(PsdSmartObjects.WithPlaced(saved, PsdSmartObjects.WithFilters(PsdSmartObjects.ReadPlaced(saved)!, null)))!.HasFilters);
    }

    [Fact]
    public void An_unedited_smart_object_and_its_content_survive_a_save_byte_for_byte_and_an_edited_one_saves_its_new_content()
    {
        var file = FileWith(PsdSmartObjects.NewEmbedded("id-1", "a.png", "png ", Payload(40, 1)));
        var doc = new Document(200, 100, ColorMode.Rgb, 8) { SourceData = file };
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(100, 50, 8)).ToArray();
        var layer = new PixelLayer { Name = "SO", Bounds = new PixelRect(10, 20, 110, 70), Pixels = new Raster(ColorMode.Rgb, planes, null), SourceData = NewRecord() };
        layer.Tags.Add("smart-object");
        doc.Root.Add(layer);
        var first = new MemoryStream();
        PsdWriter.Write(doc, first);
        first.Position = 0;
        var reread = PsdFile.Read(first, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
        var redoc = reread.ToDocument();
        var second = new MemoryStream();
        PsdWriter.Write(redoc, second);
        second.Position = 0;
        var again = PsdFile.Read(second, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
        Assert.Equal(reread.GlobalBlocks.Single(b => b.Key == "lnk2").Data, again.GlobalBlocks.Single(b => b.Key == "lnk2").Data);
        foreach (var key in new[] { "SoLd", "PlLd" })
            Assert.Equal(reread.Layers[0].FindBlock(key)!.Data, again.Layers[0].FindBlock(key)!.Data);

        redoc.SourceData = PsdSmartObjects.WithLinkedFile(reread, PsdSmartObjects.FindLinkedFile(reread, "id-1")!.WithData(Payload(7, 5)));
        var third = new MemoryStream();
        PsdWriter.Write(redoc, third);
        third.Position = 0;
        Assert.Equal(Payload(7, 5), PsdLiveContent.FindEmbeddedFile(PsdFile.Read(third), "id-1")!.Data);
    }

    [Fact]
    public void Psb_output_is_forced_for_smart_object_contents()
    {
        var doc = new Document(4, 4, ColorMode.Rgb, 8);
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms, new PsdWriteOptions { Psb = true });
        ms.Position = 0;
        Assert.True(PsdFile.Read(ms).Header.IsPsb);
    }

    // ---- Real files ------------------------------------------------------------------------------

    private static readonly string? CorpusDir = Environment.GetEnvironmentVariable("STRAYTA_CORPUS");

    [Fact]
    public void Real_linked_files_blocks_re_encode_identically()
    {
        if (CorpusDir is null || !Directory.Exists(CorpusDir)) Assert.Skip("Set STRAYTA_CORPUS to run this test.");
        int seen = 0;
        foreach (var f in Directory.EnumerateFiles(CorpusDir, "*.ps?", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("._")))
        {
            var file = PsdFile.Open(f, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue, SkipLayerPixels = true, SkipComposite = true });
            foreach (var block in file.GlobalBlocks.Where(b => b.Key is "lnk2" or "lnk3" or "lnkD" && b.Data is { Length: > 0 }))
            {
                Assert.True(PsdLinkedFiles.IsFullyReadable(block.Data!), Path.GetFileName(f));
                var entries = PsdLinkedFiles.Read(block.Data!);
                Assert.Equal(block.Data, PsdLinkedFiles.Write(entries.Select(e => e with { Raw = null })));
                seen += entries.Count;
            }
            foreach (var r in file.Layers.Where(r => r.FindBlock("SoLd") is not null))
                Assert.NotNull(PsdLiveContent.ReadSmartObject(r));
        }
        if (seen == 0) Assert.Skip("No smart objects in the corpus.");
    }
}
