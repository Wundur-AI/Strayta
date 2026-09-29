using Strayta.Core;

namespace Strayta.Psd;

/// <summary>The fixed 26-byte file header.</summary>
public sealed record PsdHeader(int Version, int Channels, int Width, int Height, int BitDepth, ColorMode ColorMode)
{
    /// <summary>True for the Large Document Format (.psb), which widens several length fields to 64 bits.</summary>
    public bool IsPsb => Version == 2;
}

/// <summary>An image resource block from the Image Resources section.</summary>
public sealed record ImageResource(string Signature, int Id, string Name, byte[] Data);

/// <summary>
/// A tagged "additional layer information" block. <see cref="Data"/> is kept raw so unknown blocks
/// survive a future round-trip; it is null when the block exceeded <see cref="PsdReadOptions.MaxRawBlockBytes"/>.
/// </summary>
public sealed record TaggedBlock(string Signature, string Key, long Offset, long Length, byte[]? Data);

/// <summary>Where a channel sits in a layer: a color component, transparency, or a mask.</summary>
public sealed record PsdChannelInfo(short Id, long Length);

/// <summary>Values for <see cref="PsdChannelInfo.Id"/> with special meaning.</summary>
public static class PsdChannelId
{
    public const short Transparency = -1;
    public const short UserMask = -2;
    public const short RealUserMask = -3;
}

public enum PsdCompression : ushort
{
    Raw = 0,
    Rle = 1,
    Zip = 2,
    ZipWithPrediction = 3,
}

public sealed class PsdLayerMaskData
{
    public PixelRect Rect { get; init; }
    public byte DefaultColor { get; init; }
    public byte Flags { get; init; }
    public PixelRect? RealRect { get; init; }
    public byte? RealFlags { get; init; }
    public byte? RealDefaultColor { get; init; }

    public bool PositionRelativeToLayer => (Flags & 0x01) != 0;
    public bool Disabled => (Flags & 0x02) != 0;
}

/// <summary>Kinds of section divider stored in the 'lsct' block.</summary>
public enum PsdSectionType
{
    None = 0,
    OpenFolder = 1,
    ClosedFolder = 2,
    BoundingDivider = 3,
}

/// <summary>One layer record exactly as stored in the file, with its decoded channel pixels.</summary>
public sealed class PsdLayerRecord
{
    public PixelRect Rect { get; init; }
    public IReadOnlyList<PsdChannelInfo> Channels { get; init; } = [];
    public string BlendModeKey { get; init; } = "norm";
    public byte Opacity { get; init; } = 255;
    public bool Clipped { get; init; }
    public byte Flags { get; init; }
    public PsdLayerMaskData? Mask { get; init; }
    public byte[] BlendingRanges { get; init; } = [];
    public string PascalName { get; init; } = "";
    public IReadOnlyList<TaggedBlock> Blocks { get; init; } = [];

    /// <summary>Decoded channel planes keyed by channel id. Filled in after all records are read.</summary>
    public Dictionary<short, Plane> ChannelData { get; } = [];

    public bool TransparencyLocked => (Flags & 0x01) != 0;
    public bool Hidden => (Flags & 0x02) != 0;

    public TaggedBlock? FindBlock(string key) => Blocks.FirstOrDefault(b => b.Key == key);

    // Blocks that make Photoshop regenerate a layer's pixels from other data (type, placed files, fills,
    // vector shapes). A rasterized layer drops them so its pixels are what Photoshop shows.
    private static readonly HashSet<string> LiveContentKeys =
    [
        "TySh", "tySh", "SoLd", "SoLE", "PlLd", "plLd", "SoCo", "GdFl", "PtFl", "vmsk", "vsms", "vogk", "vscg", "vstk",
    ];

    /// <summary>A copy with live content (text, smart object, fill, vector shape) removed, as Photoshop's Rasterize Layer does.</summary>
    public PsdLayerRecord Rasterized()
    {
        var copy = new PsdLayerRecord
        {
            Rect = Rect, Channels = Channels, BlendModeKey = BlendModeKey, Opacity = Opacity, Clipped = Clipped,
            Flags = Flags, Mask = Mask, BlendingRanges = BlendingRanges, PascalName = PascalName,
            Blocks = Blocks.Where(b => !LiveContentKeys.Contains(b.Key)).ToList(),
        };
        foreach (var (id, plane) in ChannelData) copy.ChannelData[id] = plane;
        return copy;
    }

    /// <summary>A copy with other tagged blocks and mask information (e.g. moved vector data).</summary>
    public PsdLayerRecord WithBlocks(IReadOnlyList<TaggedBlock> blocks, PsdLayerMaskData? mask)
    {
        var copy = new PsdLayerRecord
        {
            Rect = Rect, Channels = Channels, BlendModeKey = BlendModeKey, Opacity = Opacity, Clipped = Clipped,
            Flags = Flags, Mask = mask, BlendingRanges = BlendingRanges, PascalName = PascalName, Blocks = blocks,
        };
        foreach (var (id, plane) in ChannelData) copy.ChannelData[id] = plane;
        return copy;
    }

    /// <summary>
    /// A copy for a duplicated layer: same content blocks (text, effects, smart object) but without the
    /// layer ID, which must stay unique; Photoshop assigns a new one when it opens the file.
    /// </summary>
    public PsdLayerRecord ForDuplicate()
    {
        var copy = new PsdLayerRecord
        {
            Rect = Rect, Channels = Channels, BlendModeKey = BlendModeKey, Opacity = Opacity, Clipped = Clipped,
            Flags = Flags, Mask = Mask, BlendingRanges = BlendingRanges, PascalName = PascalName,
            Blocks = Blocks.Where(b => b.Key != "lyid").ToList(),
        };
        foreach (var (id, plane) in ChannelData) copy.ChannelData[id] = plane;
        return copy;
    }

    /// <summary>The layer name, preferring the Unicode 'luni' block over the legacy Pascal name.</summary>
    public string Name => PsdBlocks.ReadUnicodeName(FindBlock("luni")) ?? PascalName;

    public PsdSectionType SectionType => PsdBlocks.ReadSection(FindBlock("lsct") ?? FindBlock("lsdk")).Type;
}

/// <summary>Round-trip data for a group: its folder record and the divider record that closes it.</summary>
public sealed record PsdGroupRecords(PsdLayerRecord Folder, PsdLayerRecord? Divider);

public sealed class PsdFile
{
    public required PsdHeader Header { get; init; }
    public byte[] ColorModeData { get; init; } = [];
    public IReadOnlyList<ImageResource> Resources { get; init; } = [];

    /// <summary>Layer records bottom-to-top, as stored.</summary>
    public IReadOnlyList<PsdLayerRecord> Layers { get; init; } = [];

    /// <summary>
    /// True when the file stored a negative layer count, meaning the first extra channel of the
    /// composite holds the merged image's transparency.
    /// </summary>
    public bool CompositeHasTransparency { get; init; }

    public byte[] GlobalLayerMaskInfo { get; init; } = [];
    public IReadOnlyList<TaggedBlock> GlobalBlocks { get; init; } = [];

    /// <summary>Flattened image channels, in file order. Empty if the file had no image data section.</summary>
    public IReadOnlyList<Plane> CompositeChannels { get; init; } = [];

    /// <summary>
    /// From resource 1057 (version info): false when the file was saved without "Maximize Compatibility",
    /// in which case the composite is not a faithful render. Null if the resource is missing.
    /// </summary>
    public bool? HasRealMergedData { get; init; }

    public ImageResource? FindResource(int id) => Resources.FirstOrDefault(r => r.Id == id);

    /// <summary>A copy with a new header, resources and composite channels; layer records are shared.</summary>
    internal PsdFile With(PsdHeader header, IReadOnlyList<ImageResource> resources, IReadOnlyList<Plane> compositeChannels) => new()
    {
        Header = header,
        ColorModeData = ColorModeData,
        Resources = resources,
        Layers = Layers,
        CompositeHasTransparency = CompositeHasTransparency,
        GlobalLayerMaskInfo = GlobalLayerMaskInfo,
        GlobalBlocks = GlobalBlocks,
        CompositeChannels = compositeChannels,
        HasRealMergedData = HasRealMergedData,
    };

    /// <summary>A copy with other document-level tagged blocks (e.g. an updated linked-files block); everything else is shared.</summary>
    public PsdFile WithGlobalBlocks(IReadOnlyList<TaggedBlock> blocks) => new()
    {
        Header = Header,
        ColorModeData = ColorModeData,
        Resources = Resources,
        Layers = Layers,
        CompositeHasTransparency = CompositeHasTransparency,
        GlobalLayerMaskInfo = GlobalLayerMaskInfo,
        GlobalBlocks = blocks,
        CompositeChannels = CompositeChannels,
        HasRealMergedData = HasRealMergedData,
    };

    /// <summary>
    /// The application that last saved the file, from resource 1057 (e.g. "Adobe Photoshop", "Strayta"),
    /// or null if unknown. Only Photoshop-written composites are a trustworthy rendering reference.
    /// </summary>
    public string? WriterName => PsdVersionInfo.Read(FindResource(1057)?.Data)?.Writer;

    /// <summary>True when the flattened image was produced by Photoshop itself.</summary>
    public bool CompositeIsFromPhotoshop =>
        HasRealMergedData != false && WriterName?.Contains("Photoshop", StringComparison.OrdinalIgnoreCase) == true;

    public static PsdFile Open(string path, PsdReadOptions? options = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Read(stream, options);
    }

    public static PsdFile Read(Stream stream, PsdReadOptions? options = null) =>
        new PsdReader(stream, options ?? new PsdReadOptions()).Read();

    public Document ToDocument() => PsdDocumentConverter.Convert(this);

    /// <summary>
    /// Opens a file for editing: keeps every block in memory so it can be saved back unchanged. A flat file (no layer
    /// records) keeps its image only in the composite, so it becomes a Background layer, as in Photoshop; otherwise
    /// edits would start from an empty canvas and saving would write a blank image.
    /// </summary>
    public static Document OpenForEditing(string path)
    {
        var doc = Open(path, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue }).ToDocument();
        if (doc.Root.Children.Count == 0 && doc.Composite is { } composite)
            doc.Root.Add(new PixelLayer
            {
                Name = composite.Alpha is null ? "Background" : "Layer 0",
                Bounds = doc.Bounds,
                Pixels = composite,
            });
        return doc;
    }
}

public sealed class PsdReadOptions
{
    /// <summary>Tagged blocks larger than this keep only their offset and length, not their bytes.</summary>
    public long MaxRawBlockBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Skip decoding layer pixels (faster when only structure is needed).</summary>
    public bool SkipLayerPixels { get; init; }

    /// <summary>Skip decoding the flattened composite image.</summary>
    public bool SkipComposite { get; init; }
}
