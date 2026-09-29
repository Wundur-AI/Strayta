using System.Buffers.Binary;
using Strayta.Core;

namespace Strayta.Psd.Tests;

/// <summary>Guides (image resource 1032) are read into the document, written back, and follow canvas changes.</summary>
public class GuideTests
{
    private static readonly PsdReadOptions Options = new() { MaxRawBlockBytes = long.MaxValue };

    private static Document NewDocument(params Guide[] guides)
    {
        var doc = new Document(200, 100, ColorMode.Rgb, 8) { Guides = guides };
        var pixels = new Raster(ColorMode.Rgb, [Plane.Create(200, 100, 8), Plane.Create(200, 100, 8), Plane.Create(200, 100, 8)], null);
        doc.Root.Add(new PixelLayer { Name = "Background", Bounds = doc.Bounds, Pixels = pixels });
        return doc;
    }

    private static (PsdFile File, Document Doc) Reload(Document doc)
    {
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var file = PsdFile.Read(ms, Options);
        return (file, file.ToDocument());
    }

    [Fact]
    public void Resource_encodes_Photoshop_layout()
    {
        var data = PsdGuides.Write([new Guide(GuideOrientation.Vertical, 50.5), new Guide(GuideOrientation.Horizontal, 20)]);
        Assert.Equal(16 + 2 * 5, data.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(data));
        Assert.Equal(576, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4))); // Photoshop's default grid cycle
        Assert.Equal(576, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(8)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(12)));
        Assert.Equal(50.5 * 32, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)));
        Assert.Equal(0, data[20]);
        Assert.Equal(20 * 32, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(21)));
        Assert.Equal(1, data[25]);
        Assert.Equal([new Guide(GuideOrientation.Vertical, 50.5), new Guide(GuideOrientation.Horizontal, 20)], PsdGuides.Read(data));
    }

    [Fact]
    public void Guides_made_in_a_new_document_are_saved_and_read_back()
    {
        var doc = NewDocument(new Guide(GuideOrientation.Vertical, 100), new Guide(GuideOrientation.Horizontal, 33.25));
        var (file, again) = Reload(doc);
        Assert.NotNull(file.FindResource(PsdGuides.ResourceId));
        Assert.Equal(doc.Guides, again.Guides);

        var none = Reload(NewDocument()).File;
        Assert.Null(none.FindResource(PsdGuides.ResourceId)); // nothing to store: no block, as Photoshop
    }

    [Fact]
    public void Unchanged_guides_keep_their_bytes_and_changed_ones_keep_the_header()
    {
        var stored = PsdGuides.Write([new Guide(GuideOrientation.Horizontal, 10)]);
        BinaryPrimitives.WriteInt32BigEndian(stored.AsSpan(4), 1234); // an unusual grid cycle must survive
        var doc = NewDocument();
        doc.SourceData = new PsdFile
        {
            Header = new PsdHeader(1, 3, 200, 100, 8, ColorMode.Rgb),
            Resources = [new ImageResource("8BIM", PsdGuides.ResourceId, "", stored)],
        };
        doc.Guides = PsdGuides.Read(stored);
        var (file, _) = Reload(doc);
        Assert.Equal(stored, file.FindResource(PsdGuides.ResourceId)!.Data);

        doc.Guides = [new Guide(GuideOrientation.Horizontal, 10), new Guide(GuideOrientation.Vertical, 7.5)];
        var (changed, again) = Reload(doc);
        var data = changed.FindResource(PsdGuides.ResourceId)!.Data;
        Assert.Equal(1234, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4)));
        Assert.Equal(doc.Guides, again.Guides);

        doc.Guides = []; // Clear Guides: the block stays, with no guides
        Assert.Empty(Reload(doc).Doc.Guides);
    }

    [Fact]
    public void Typed_remap_matches_the_stored_block_remap()
    {
        var guides = new[] { new Guide(GuideOrientation.Vertical, 50), new Guide(GuideOrientation.Horizontal, 20.5) };
        foreach (var map in new[] { CanvasMap.Translation(-10, -5), new CanvasMap(0, -1, 1, 0, 100, 0), new CanvasMap(2, 0, 0, 2, 0, 0) })
        {
            var typed = PsdGuides.Remap(guides, 180, 90, map);
            var bytes = PsdGuides.Read(PsdCanvas.RemapGuides(PsdGuides.Write(guides), 180, 90, map));
            Assert.Equal(bytes, typed);
        }
        Assert.Equal(new Guide(GuideOrientation.Vertical, 40), PsdGuides.Remap(guides[0], 100, 100, CanvasMap.Translation(-10, -5)));
    }

    [Theory]
    [MemberData(nameof(CorpusTests.Files), MemberType = typeof(CorpusTests))]
    public void Corpus_guides_round_trip(string relativePath)
    {
        string? dir = Environment.GetEnvironmentVariable("STRAYTA_CORPUS");
        if (relativePath.Length == 0 || dir is null) Assert.Skip("Set STRAYTA_CORPUS to a folder of PSD files to run corpus tests.");
        var options = new PsdReadOptions { MaxRawBlockBytes = long.MaxValue, SkipComposite = true };
        var original = PsdFile.Open(Path.Combine(dir, relativePath), options);
        var doc = original.ToDocument();
        if (doc.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale) || doc.BitDepth is not (8 or 16 or 32)) return;
        var stored = original.FindResource(PsdGuides.ResourceId)?.Data;
        Assert.Equal(PsdGuides.Read(stored), doc.Guides);

        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var again = PsdFile.Read(ms, options);
        Assert.Equal(stored, again.FindResource(PsdGuides.ResourceId)?.Data); // untouched guides: byte for byte

        // A guide added in Strayta is stored after the file's own.
        doc.Guides = [.. doc.Guides, new Guide(GuideOrientation.Horizontal, doc.Height / 3.0)];
        ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        Assert.Equal(doc.Guides.Select(g => g with { Position = PsdGuides.Quantize(g.Position) }), PsdFile.Read(ms, options).ToDocument().Guides);
    }
}
