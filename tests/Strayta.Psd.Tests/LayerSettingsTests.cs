using Strayta.Core;

namespace Strayta.Psd.Tests;

/// <summary>Locks ('lspf'), color labels ('lclr') and linked layers (resource 1026): read, kept byte for byte, and written after edits.</summary>
public class LayerSettingsTests
{
    private static PsdTestBuilder.Layer Pixel(string name, byte flags = 0, params (string Key, byte[] Data)[] blocks)
    {
        var l = new PsdTestBuilder.Layer
        {
            Name = name, Left = 0, Top = 0, Right = 2, Bottom = 2, Flags = flags,
            Channels = { [-1] = [255, 255, 255, 255], [0] = [1, 2, 3, 4], [1] = [5, 6, 7, 8], [2] = [9, 10, 11, 12] },
        };
        l.ExtraBlocks.AddRange(blocks);
        return l;
    }

    private static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static (PsdFile File, Document Doc) Open(byte[] bytes)
    {
        var file = PsdFile.Read(new MemoryStream(bytes), new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
        return (file, file.ToDocument());
    }

    private static (PsdFile File, Document Doc) Reload(Document doc)
    {
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        return Open(ms.ToArray());
    }

    private static PsdTestBuilder Sample() => new(4, 4)
    {
        Layers =
        {
            Pixel("Plain"),
            // Unknown bit 0x10 and a non-zero tail in lclr must survive untouched.
            Pixel("Locked", 0x01, ("lspf", U32(0x16)), ("lclr", [0, 4, 0, 0, 0, 7, 0, 0])),
            Pixel("All", 0, ("lspf", U32(0x8000_0000)), ("lclr", [0, 6, 0, 0, 0, 0, 0, 0])),
        },
        Resources = { (1026, [0, 0, 0, 3, 0, 3]) },
    };

    [Fact]
    public void Locks_colors_and_links_are_read()
    {
        var (_, doc) = Open(Sample().Build());
        var (plain, locked, all) = (doc.Root.Children[0], doc.Root.Children[1], doc.Root.Children[2]);
        Assert.Equal(LayerLocks.None, plain.Locks);
        Assert.Equal(LayerLocks.Transparency | LayerLocks.Pixels | LayerLocks.Position | (LayerLocks)0x10, locked.Locks);
        Assert.Equal(LayerColor.Green, locked.Color);
        Assert.Equal(LayerColor.Violet, all.Color);
        Assert.True(all.IsLocked(LayerLocks.Position) && all.IsLocked(LayerLocks.Pixels) && ((PixelLayer)all).TransparencyLocked);
        Assert.Equal([0, 3, 3], doc.Root.Children.Select(c => c.LinkGroup));
    }

    [Fact]
    public void Unchanged_settings_keep_their_bytes()
    {
        var (original, doc) = Open(Sample().Build());
        var (again, _) = Reload(doc);
        for (int i = 0; i < original.Layers.Count; i++)
        {
            Assert.Equal(original.Layers[i].Flags, again.Layers[i].Flags);
            foreach (var key in new[] { "lspf", "lclr" })
                Assert.Equal(original.Layers[i].FindBlock(key)?.Data, again.Layers[i].FindBlock(key)?.Data);
        }
        Assert.Equal(original.FindResource(1026)!.Data, again.FindResource(1026)!.Data);
    }

    [Fact]
    public void Edited_settings_are_written_and_read_back()
    {
        var (_, doc) = Open(Sample().Build());
        var (plain, locked, all) = (doc.Root.Children[0], doc.Root.Children[1], doc.Root.Children[2]);
        plain.Locks = LayerLocks.Position;
        plain.Color = LayerColor.Red;
        ((PixelLayer)locked).TransparencyLocked = false;
        all.Locks = LayerLocks.None;
        all.Color = LayerColor.None;
        all.LinkGroup = 0;
        plain.LinkGroup = 5;
        doc.Root.Add(new PixelLayer { Name = "New", Locks = LayerLocks.All, Color = LayerColor.Blue, LinkGroup = 5 });

        var (file, again) = Reload(doc);
        Assert.Equal(LayerLocks.Position, again.Root.Children[0].Locks);
        Assert.Equal(LayerColor.Red, again.Root.Children[0].Color);
        Assert.Equal(LayerLocks.Pixels | LayerLocks.Position | (LayerLocks)0x10, again.Root.Children[1].Locks);
        Assert.Equal(0, file.Layers[1].Flags & 1);
        Assert.Equal(LayerLocks.None, again.Root.Children[2].Locks);
        Assert.Equal(LayerColor.None, again.Root.Children[2].Color);
        Assert.Equal(LayerLocks.All, again.Root.Children[3].Locks);
        Assert.Equal(0, file.Layers[3].Flags & 1); // Lock All lives in 'lspf' alone
        Assert.Equal(new byte[] { 0x80, 0, 0, 0 }, file.Layers[3].FindBlock("lspf")!.Data);
        Assert.Equal(LayerColor.Blue, again.Root.Children[3].Color);
        Assert.Equal([5, 3, 0, 5], again.Root.Children.Select(c => c.LinkGroup));
    }

    [Fact]
    public void Links_in_groups_are_stored_per_record()
    {
        var doc = new Document(4, 4, ColorMode.Rgb, 8);
        var group = new LayerGroup { Name = "G", LinkGroup = 2 };
        group.Add(new PixelLayer { Name = "In", LinkGroup = 2 });
        doc.Root.Add(new PixelLayer { Name = "Below" });
        doc.Root.Add(group);
        var (file, again) = Reload(doc);
        Assert.Equal(file.Layers.Count * 2, file.FindResource(1026)!.Data.Length);
        var g = Assert.IsType<LayerGroup>(again.Root.Children[1]);
        Assert.Equal(2, g.LinkGroup);
        Assert.Equal(2, g.Children[0].LinkGroup);
        Assert.Equal(0, again.Root.Children[0].LinkGroup);
    }

    [Fact]
    public void Documents_without_links_get_no_link_resource()
    {
        var doc = new Document(4, 4, ColorMode.Rgb, 8);
        doc.Root.Add(new PixelLayer { Name = "A" });
        var (file, _) = Reload(doc);
        Assert.Null(file.FindResource(1026));
    }
}
