using System.Buffers.Binary;
using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd.Text;

/// <summary>
/// Warp Text: the warp stored in a type layer's 'TySh' block after the text descriptor (a version, 1, and a versioned
/// "warp" descriptor with "warpStyle", "warpValue", "warpPerspective", "warpPerspectiveOther" and "warpRotate"). Type
/// takes only the named styles, bent over the text's own bounds (the text descriptor's "bounds", in text space).
/// Replacing the warp keeps every other byte of the block.
/// </summary>
public static class PsdTypeWarp
{
    /// <summary>Where the parts of a 'TySh' block are: the text descriptor, and the warp (version and descriptor).</summary>
    private static (Descriptor Text, int WarpStart, int WarpEnd, Descriptor Warp)? Parse(byte[] data)
    {
        try
        {
            if (data.Length < 56 || BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(52)) != 16) return null;
            var reader = new DescriptorReader(data, 56);
            var text = reader.ReadDescriptor();
            int warpStart = reader.Position;
            var warpReader = new DescriptorReader(data, warpStart + 6);
            var warp = warpReader.ReadDescriptor();
            return (text, warpStart, warpReader.Position, warp);
        }
        catch (Exception e) when (e is PsdFormatException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The text's bounds in text space (the layout box the warp bends), or null when unreadable.</summary>
    public static TextRect? TextBounds(PsdLayerRecord record)
    {
        if (record.FindBlock("TySh")?.Data is not { } data || Parse(data) is not { } p || p.Text.Object("bounds") is not { } b) return null;
        var r = new TextRect(b.Number("Left") ?? 0, b.Number("Top ") ?? 0, b.Number("Rght") ?? 0, b.Number("Btom") ?? 0);
        return r.Width > 0 && r.Height > 0 ? r : null;
    }

    /// <summary>The type layer's warp with its bounds set to the text's bounds; null when it has none or it cannot be read.</summary>
    public static WarpSpec? Read(PsdLayerRecord record)
    {
        if (record.FindBlock("TySh")?.Data is not { } data || Parse(data) is not { } p) return null;
        var spec = PsdLiveContent.ReadWarp(p.Warp);
        if (spec is null || spec.IsNone) return null;
        var b = TextBounds(record);
        return b is { } r ? spec with { Bounds = (r.Left, r.Top, r.Right, r.Bottom) } : spec;
    }

    /// <summary>
    /// The record with the type's warp set to <paramref name="warp"/>'s style, bend, distortions and orientation (a none
    /// warp removes it); every other byte of the block is kept.
    /// </summary>
    public static PsdLayerRecord WithWarp(PsdLayerRecord record, WarpSpec warp)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(warp);
        if (record.FindBlock("TySh")?.Data is not { } data || Parse(data) is not { } p)
            throw new ArgumentException("The layer's type data cannot be read.", nameof(record));
        string style = warp.IsNone ? "warpNone" : warp.Style;
        var descriptor = new Descriptor
        {
            Name = p.Warp.Name,
            ClassId = "warp",
            Items =
            [
                new("warpStyle", new EnumValue("warpStyle", style)),
                new("warpValue", new DoubleValue(style == "warpNone" ? 0 : warp.Value)),
                new("warpPerspective", new DoubleValue(style == "warpNone" ? 0 : warp.Perspective)),
                new("warpPerspectiveOther", new DoubleValue(style == "warpNone" ? 0 : warp.PerspectiveOther)),
                new("warpRotate", new EnumValue("Ornt", warp.Vertical ? "Vrtc" : "Hrzn")),
            ],
        };
        var version = data.AsSpan(p.WarpStart, 2).ToArray();
        var warpBytes = DescriptorWriter.WriteVersioned(descriptor);
        // After the warp come four 32-bit values, then zero padding to a multiple of four bytes.
        var tail = data.AsSpan(p.WarpEnd, Math.Min(16, data.Length - p.WarpEnd)).ToArray();
        byte[] result = [.. data.AsSpan(0, p.WarpStart), .. version, .. warpBytes, .. tail];
        if (result.Length % 4 != 0) result = [.. result, .. new byte[4 - result.Length % 4]];
        // Photoshop's stored pixels no longer show this warp, and its cached text engine data is not to be trusted.
        PsdTypeLayer.MarkRegenerated(result);
        var blocks = record.Blocks.ToList();
        int at = blocks.FindIndex(b => b.Key == "TySh");
        blocks[at] = blocks[at] with { Data = result, Length = result.Length };
        return record.WithBlocks(blocks, record.Mask);
    }
}
