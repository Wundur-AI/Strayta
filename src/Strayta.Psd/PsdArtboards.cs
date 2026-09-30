using Strayta.Core;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>
/// Artboards in PSD files. Adobe's specification lists the layer blocks 'artb', 'artd' and 'abdd' ("Artboard data",
/// a version-16 descriptor); files written by Photoshop CC put 'artb' on the artboard group's folder record and a
/// document-wide 'artd' block (auto-nesting and default settings) among the global blocks. The artboard descriptor
/// holds <c>artboardRect</c> (a float rectangle in document pixels), <c>guideIndeces</c>, <c>artboardPresetName</c>,
/// <c>Clr </c> (RGB) and <c>artboardBackgroundType</c> (1 white, 2 black, 3 transparent, 4 other). Unedited blocks
/// are written back byte for byte; edited ones keep every item Strayta does not model.
/// </summary>
public static class PsdArtboards
{
    public static readonly string[] LayerKeys = ["artb", "artd", "abdd"];

    /// <summary>The artboard stored on a group's folder record, or null if the group is not an artboard.</summary>
    public static Artboard? Read(PsdLayerRecord? folder)
    {
        if (folder is null) return null;
        foreach (var key in LayerKeys)
            if (folder.FindBlock(key) is { Data: { Length: > 8 } data } && Parse(data) is { } artboard)
                return artboard;
        return null;
    }

    /// <summary>Decodes an artboard descriptor block (version 16 + descriptor); null if it has no rectangle.</summary>
    public static Artboard? Parse(byte[] data)
    {
        Descriptor d;
        try
        {
            d = DescriptorReader.ReadVersioned(data);
        }
        catch (Exception e) when (e is PsdFormatException or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
        {
            return null;
        }
        if (d.Object("artboardRect") is not { } rect) return null;
        int Round(string key) => (int)Math.Round(rect.Number(key) ?? 0);
        var r = new PixelRect(Round("Left"), Round("Top "), Round("Rght"), Round("Btom"));
        var background = (int)(d.Number("artboardBackgroundType") ?? 1) switch
        {
            2 => ArtboardBackground.Black,
            3 => ArtboardBackground.Transparent,
            4 => ArtboardBackground.Custom,
            _ => ArtboardBackground.White,
        };
        var color = d.Object("Clr ") is { } c ? (Byte(c.Number("Rd  ")), Byte(c.Number("Grn ")), Byte(c.Number("Bl  "))) : ((byte)255, (byte)255, (byte)255);
        return new Artboard { Rect = r, Background = background, Color = color, PresetName = d.Text("artboardPresetName") ?? "" };

        static byte Byte(double? v) => (byte)Math.Clamp(Math.Round(v ?? 255), 0, 255);
    }

    /// <summary>
    /// Brings a group's blocks in line with its artboard: removes the artboard blocks from a group that is no longer
    /// one, keeps a stored block that still says the same, and otherwise writes a new 'artb' (keeping the stored
    /// descriptor's other items, such as guide indices).
    /// </summary>
    public static void Refresh(LayerGroup group, PsdLayerRecord? source, List<(string Key, byte[] Data)> blocks)
    {
        if (group.Artboard is not { } artboard)
        {
            blocks.RemoveAll(b => LayerKeys.Contains(b.Key));
            return;
        }
        int index = blocks.FindIndex(b => LayerKeys.Contains(b.Key));
        if (index >= 0 && Parse(blocks[index].Data) == artboard) return;

        Descriptor? stored = null;
        if (index >= 0)
            try { stored = DescriptorReader.ReadVersioned(blocks[index].Data); }
            catch (Exception e) when (e is PsdFormatException or ArgumentException or IndexOutOfRangeException or EndOfStreamException) { }
        var data = Encode(artboard, stored);
        if (index >= 0) blocks[index] = ("artb", data);
        else blocks.Add(("artb", data));
    }

    /// <summary>An 'artb' block for <paramref name="artboard"/>, replacing the modeled items of <paramref name="stored"/>.</summary>
    public static byte[] Encode(Artboard artboard, Descriptor? stored = null)
    {
        var r = artboard.Rect;
        var modeled = new List<KeyValuePair<string, DescriptorValue>>
        {
            new("artboardRect", new ObjectValue(new Descriptor
            {
                ClassId = "classFloatRect",
                Items =
                [
                    new("Top ", new DoubleValue(r.Top)),
                    new("Left", new DoubleValue(r.Left)),
                    new("Btom", new DoubleValue(r.Bottom)),
                    new("Rght", new DoubleValue(r.Right)),
                ],
            })),
            new("guideIndeces", stored?["guideIndeces"] ?? new ListValue([])),
            new("artboardPresetName", new TextValue(artboard.PresetName)),
            new("Clr ", new ObjectValue(Rgb(artboard.Color))),
            new("artboardBackgroundType", new IntegerValue((int)artboard.Background)),
        };
        // Items Strayta does not know stay, after the ones it writes.
        var items = modeled.Concat((stored?.Items ?? []).Where(kv => modeled.All(m => m.Key != kv.Key))).ToList();
        return DescriptorWriter.WriteVersioned(new Descriptor { ClassId = "artboard", Items = items });
    }

    /// <summary>
    /// The document-wide 'artd' block Photoshop writes with artboards: automatic nesting, positioning and canvas
    /// growth on, and white as the background of new artboards.
    /// </summary>
    public static byte[] EncodeDocumentBlock(int count) => DescriptorWriter.WriteVersioned(new Descriptor
    {
        ClassId = "null",
        Items =
        [
            new("Cnt ", new IntegerValue(count)),
            new("autoExpandOffset", new ObjectValue(Point(0, 0))),
            new("origin", new ObjectValue(Point(0, 0))),
            new("autoExpandEnabled", new BoolValue(true)),
            new("autoNestEnabled", new BoolValue(true)),
            new("autoPositionEnabled", new BoolValue(true)),
            new("shrinkwrapOnSaveEnabled", new BoolValue(false)),
            new("docDefaultNewArtboardBackgroundColor", new ObjectValue(Rgb((255, 255, 255)))),
            new("docDefaultNewArtboardBackgroundType", new IntegerValue(1)),
        ],
    });

    private static Descriptor Rgb((byte R, byte G, byte B) c) => new()
    {
        ClassId = "RGBC",
        Items = [new("Rd  ", new DoubleValue(c.R)), new("Grn ", new DoubleValue(c.G)), new("Bl  ", new DoubleValue(c.B))],
    };

    private static Descriptor Point(double h, double v) => new()
    {
        ClassId = "Pnt ",
        Items = [new("Hrzn", new DoubleValue(h)), new("Vrtc", new DoubleValue(v))],
    };
}
