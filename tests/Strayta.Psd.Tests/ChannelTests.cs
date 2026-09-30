using System.Buffers.Binary;
using Strayta.Core;
using Strayta.Core.Selection;

namespace Strayta.Psd.Tests;

/// <summary>Saved selections (alpha channels) and spot channels: resources 1006/1045/1053/1077 and the extra image channels.</summary>
public class ChannelTests
{
    private static Plane Filled(int w, int h, byte v) => new(w, h, 8, Enumerable.Repeat(v, w * h).ToArray());

    private static Plane Gradient(int w, int h)
    {
        var p = Plane.Create(w, h, 8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                p.Data[y * w + x] = (byte)(x * 255 / (w - 1));
        return p;
    }

    private static Document NewDocument(int w = 40, int h = 30)
    {
        var doc = new Document(w, h, ColorMode.Rgb, 8);
        doc.Root.Add(new PixelLayer { Name = "Background", Bounds = doc.Bounds, Pixels = new Raster(ColorMode.Rgb, [Filled(w, h, 10), Filled(w, h, 20), Filled(w, h, 30)], null) });
        return doc;
    }

    private static PsdFile Save(Document doc, Raster? composite = null)
    {
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms, new PsdWriteOptions
        {
            Composite = composite ?? new Raster(ColorMode.Rgb, [Filled(doc.Width, doc.Height, 10), Filled(doc.Width, doc.Height, 20), Filled(doc.Width, doc.Height, 30)], null),
        });
        ms.Position = 0;
        return PsdFile.Read(ms, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
    }

    private static IReadOnlyList<DocumentChannel> SampleChannels(int w, int h) =>
    [
        new DocumentChannel("Sky", Gradient(w, h), ChannelKind.MaskedAreas),
        new DocumentChannel("Subject édition", Filled(w, h, 200), ChannelKind.SelectedAreas) { Color = new RgbColor(0f, 0f, 1f), Opacity = 0.7f },
        new DocumentChannel("PANTONE Warm Red", Filled(w, h, 64), ChannelKind.Spot) { Color = new RgbColor(1f, 0.3f, 0.2f), Opacity = 0.9f },
    ];

    [Fact]
    public void Two_alpha_channels_and_a_spot_channel_round_trip()
    {
        var doc = NewDocument();
        doc.SourceData = PsdChannels.WithChannels(PsdPathResources.Empty(doc), SampleChannels(doc.Width, doc.Height));
        var file = Save(doc);

        Assert.Equal(6, file.Header.Channels);
        var channels = PsdChannels.Read(file);
        Assert.Equal(["Sky", "Subject édition", "PANTONE Warm Red"], channels.Select(c => c.Name));
        Assert.Equal([ChannelKind.MaskedAreas, ChannelKind.SelectedAreas, ChannelKind.Spot], channels.Select(c => c.Kind));
        Assert.Equal(Gradient(doc.Width, doc.Height).Data, channels[0].Pixels.Data);
        Assert.All(channels[2].Pixels.Data, v => Assert.Equal(64, v));
        Assert.Equal(0.7f, channels[1].Opacity, 2);
        Assert.Equal(0.9f, channels[2].Opacity, 2);
        Assert.Equal(new RgbColor(0f, 0f, 1f), channels[1].Color);
        Assert.Equal(3, channels.Select(c => c.Id).Distinct().Count());
        Assert.All(channels, c => Assert.True(c.Id > 0));

        // Resource layouts as Photoshop writes them.
        var display = file.FindResource(PsdChannels.DisplayResource)!.Data;
        Assert.Equal(4 + 3 * 13, display.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(display));
        Assert.Equal([1, 0, 2], new[] { display[4 + 12], display[4 + 13 + 12], display[4 + 26 + 12] });
        var unicode = file.FindResource(PsdChannels.UnicodeNamesResource)!.Data;
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(unicode)); // "Sky" and its terminating null
        Assert.Equal((byte)3, file.FindResource(PsdChannels.NamesResource)!.Data[0]);
    }

    [Fact]
    public void Unchanged_channels_save_byte_for_byte_and_rewriting_the_same_list_changes_nothing()
    {
        var doc = NewDocument();
        doc.SourceData = PsdChannels.WithChannels(PsdPathResources.Empty(doc), SampleChannels(doc.Width, doc.Height));
        var first = Save(doc);
        var again = Save(first.ToDocument());
        foreach (int id in new[] { 1006, 1045, 1053, 1077 })
            Assert.Equal(first.FindResource(id)!.Data, again.FindResource(id)!.Data);
        Assert.Equal(PsdChannels.ExtraPlanes(first).Select(p => p.Data), PsdChannels.ExtraPlanes(again).Select(p => p.Data));

        // Writing the list it already has keeps the resources' bytes and the planes themselves.
        var same = PsdChannels.WithChannels(first, PsdChannels.Read(first));
        foreach (int id in new[] { 1006, 1045, 1053, 1077 })
            Assert.Same(first.FindResource(id), same.FindResource(id));
        Assert.Same(PsdChannels.Read(first)[2].Pixels, PsdChannels.ExtraPlanes(same)[2]);
    }

    [Fact]
    public void Spot_colors_stored_as_lab_keep_their_bytes_until_the_color_changes()
    {
        var doc = NewDocument();
        var lab = new PsdStoredColor(7, [5000, unchecked((ushort)(short)6000), unchecked((ushort)(short)4000), 0], default);
        var rgb = PsdChannels.ToRgb(lab.ColorSpace, lab.Components);
        Assert.True(rgb.R > rgb.G && rgb.R > rgb.B); // L 50, a 60, b 40 is a red
        var spot = new DocumentChannel("Spot", Filled(doc.Width, doc.Height, 0), ChannelKind.Spot) { Color = rgb, SourceColor = lab with { Rgb = rgb }, Opacity = 1f };
        doc.SourceData = PsdChannels.WithChannels(PsdPathResources.Empty(doc), [spot]);
        var file = Save(doc);
        var display = file.FindResource(PsdChannels.DisplayResource)!.Data;
        Assert.Equal(7, BinaryPrimitives.ReadInt16BigEndian(display.AsSpan(4)));
        var read = PsdChannels.Read(file).Single();
        Assert.Equal(rgb, read.Color);

        var recolored = PsdChannels.WithChannels(file, [read with { Color = new RgbColor(0f, 1f, 0f) }]);
        display = recolored.FindResource(PsdChannels.DisplayResource)!.Data;
        Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(display.AsSpan(4)));
        Assert.Equal(65535, BinaryPrimitives.ReadUInt16BigEndian(display.AsSpan(8)));
    }

    [Fact]
    public void The_transparency_entry_stays_first_and_follows_the_composite()
    {
        var doc = NewDocument();
        var alpha = Filled(doc.Width, doc.Height, 255);
        var withAlpha = new Raster(ColorMode.Rgb, [Filled(doc.Width, doc.Height, 10), Filled(doc.Width, doc.Height, 20), Filled(doc.Width, doc.Height, 30)], alpha);
        var transparent = Save(doc, withAlpha);
        Assert.True(transparent.CompositeHasTransparency);

        var edited = transparent.ToDocument();
        edited.SourceData = PsdChannels.WithChannels(transparent, [new DocumentChannel("Edges", Gradient(doc.Width, doc.Height), ChannelKind.MaskedAreas)]);
        var file = Save(edited, withAlpha);
        Assert.Equal(5, file.Header.Channels);
        var names = file.FindResource(PsdChannels.NamesResource)!.Data;
        Assert.Equal("\u000cTransparency\u0005Edges", System.Text.Encoding.Latin1.GetString(names));
        Assert.Equal("Edges", PsdChannels.Read(file).Single().Name);

        // Saved again without transparency: the lists lose its entry, so the names still match the channels.
        var opaque = Save(file.ToDocument());
        Assert.False(opaque.CompositeHasTransparency);
        Assert.Equal("\u0005Edges", System.Text.Encoding.Latin1.GetString(opaque.FindResource(PsdChannels.NamesResource)!.Data));
        var channel = PsdChannels.Read(opaque).Single();
        Assert.Equal("Edges", channel.Name);
        Assert.Equal(Gradient(doc.Width, doc.Height).Data, channel.Pixels.Data);
    }

    [Fact]
    public void Deleting_every_channel_removes_the_lists()
    {
        var doc = NewDocument();
        doc.SourceData = PsdChannels.WithChannels(PsdPathResources.Empty(doc), SampleChannels(doc.Width, doc.Height));
        var file = PsdChannels.WithChannels(Save(doc), []);
        Assert.Null(file.FindResource(PsdChannels.NamesResource));
        Assert.Null(file.FindResource(PsdChannels.DisplayResource));
        var saved = Save(file.ToDocument());
        Assert.Equal(3, saved.Header.Channels);
        Assert.Empty(PsdChannels.Read(saved));
    }

    [Fact]
    public void Channels_follow_a_canvas_change()
    {
        var doc = NewDocument();
        doc.SourceData = PsdChannels.WithChannels(PsdPathResources.Empty(doc), SampleChannels(doc.Width, doc.Height));
        var file = Save(doc);
        var cropped = PsdCanvas.WithCanvas(file, 20, 10, CanvasMap.Translation(-10, -5), p => new Plane(20, 10, 8, new byte[200]));
        var channels = PsdChannels.Read(cropped);
        Assert.Equal(3, channels.Count);
        Assert.All(channels, c => Assert.Equal((20, 10), (c.Pixels.Width, c.Pixels.Height)));
        Assert.Equal("Sky", channels[0].Name);
    }

    [Fact]
    public void Save_and_load_selection_operations()
    {
        var canvas = PixelRect.FromSize(10, 10);
        var left = SelectionMask.Rectangle(new PixelRect(0, 0, 5, 10), canvas);
        var top = SelectionMask.Rectangle(new PixelRect(0, 0, 10, 5), canvas);
        var channel = ChannelSelection.FromSelection(left, canvas, 8);
        Assert.Equal(255, channel.Data[0]);
        Assert.Equal(0, channel.Data[9]);

        var added = ChannelSelection.Combine(channel, top, SelectionMode.Add, canvas);
        Assert.Equal(255, added.Data[9]);        // (9, 0): in the top half
        Assert.Equal(0, added.Data[99]);         // (9, 9): in neither
        var subtracted = ChannelSelection.Combine(channel, top, SelectionMode.Subtract, canvas);
        Assert.Equal(0, subtracted.Data[0]);
        Assert.Equal(255, subtracted.Data[90]);  // (0, 9): left, not top
        var intersected = ChannelSelection.Combine(channel, top, SelectionMode.Intersect, canvas);
        Assert.Equal(255, intersected.Data[0]);
        Assert.Equal(0, intersected.Data[90]);

        // Loading: the same selection back; inverted; and a selected-areas channel (stored black where selected).
        Assert.Equal(new PixelRect(0, 0, 5, 10), ChannelSelection.ToSelection(channel, canvas)!.Bounds);
        Assert.Equal(new PixelRect(5, 0, 10, 10), ChannelSelection.ToSelection(channel, canvas, invert: true)!.Bounds);
        var selectedAreas = new DocumentChannel("S", ChannelSelection.FromSelection(left, canvas, 8, invert: true), ChannelKind.SelectedAreas);
        Assert.Equal(0, selectedAreas.Pixels.Data[0]);
        Assert.Equal(new PixelRect(0, 0, 5, 10), ChannelSelection.ToSelection(selectedAreas, canvas)!.Bounds);
        // Combining with the current selection when loading.
        var both = SelectionMask.Combine(top, ChannelSelection.ToSelection(channel, canvas), SelectionMode.Intersect);
        Assert.Equal(new PixelRect(0, 0, 5, 5), both!.Bounds);
        Assert.Null(ChannelSelection.ToSelection(ChannelSelection.Solid(10, 10, 8, 0f), canvas));
    }

    private static readonly string? CorpusDir = Environment.GetEnvironmentVariable("STRAYTA_CORPUS");

    /// <summary>Corpus files with saved selections: names read, and an unedited save keeps the channel data and lists.</summary>
    [Fact]
    public void Corpus_channels_read_and_survive_saving()
    {
        if (CorpusDir is null || !Directory.Exists(CorpusDir)) Assert.Skip("Set STRAYTA_CORPUS to run corpus tests.");
        var options = new PsdReadOptions { MaxRawBlockBytes = long.MaxValue };
        int checkedFiles = 0;
        foreach (var path in Directory.EnumerateFiles(CorpusDir, "*.ps?", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path).StartsWith("._")) continue;
            var original = PsdFile.Open(path, options);
            var channels = PsdChannels.Read(original);
            if (channels.Count == 0 || original.Header.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale)) continue;
            Assert.All(channels, c => Assert.False(string.IsNullOrEmpty(c.Name)));
            var doc = original.ToDocument();
            var ms = new MemoryStream();
            PsdWriter.Write(doc, ms, new PsdWriteOptions { Composite = doc.Composite });
            ms.Position = 0;
            var again = PsdFile.Read(ms, options);
            Assert.Equal(channels.Select(c => (c.Name, c.Kind, c.Id, c.Color, c.Opacity)), PsdChannels.Read(again).Select(c => (c.Name, c.Kind, c.Id, c.Color, c.Opacity)));
            Assert.Equal(channels.Select(c => c.Pixels.Data), PsdChannels.Read(again).Select(c => c.Pixels.Data));
            if (again.CompositeHasTransparency == original.CompositeHasTransparency)
                foreach (int id in new[] { 1006, 1045, 1053, 1077 })
                    Assert.Equal(original.FindResource(id)?.Data, again.FindResource(id)?.Data);
            checkedFiles++;
        }
        Assert.True(checkedFiles > 0, "No corpus file has saved selections.");
    }
}
