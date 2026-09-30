using System.Buffers.Binary;
using Strayta.Core;

namespace Strayta.Psd;

/// <summary>
/// Layer settings the Layers panel shows: locks (the 'lspf' protection block, and the record's transparency flag),
/// color labels (the 'lclr' sheet color block) and links (image resource 1026, one 16-bit link group per layer record;
/// linked layers share a non-zero value). Unchanged settings keep their stored bytes.
/// </summary>
public static class PsdLayerSettings
{
    public const string LocksKey = "lspf", ColorKey = "lclr";

    /// <summary>Image resource 1026: a link group per layer record, bottom to top.</summary>
    public const int LinkResourceId = 1026;

    /// <summary>The locks a record stores: its 'lspf' value, plus the record flag for locked transparency.</summary>
    public static LayerLocks ReadLocks(PsdLayerRecord record)
    {
        var locks = record.FindBlock(LocksKey)?.Data is { Length: >= 4 } d ? (LayerLocks)BinaryPrimitives.ReadUInt32BigEndian(d) : LayerLocks.None;
        if (record.TransparencyLocked) locks |= LayerLocks.Transparency;
        return locks;
    }

    /// <summary>The record's color label (0 when it has none or an unknown one).</summary>
    public static LayerColor ReadColor(PsdLayerRecord record) =>
        record.FindBlock(ColorKey)?.Data is { Length: >= 2 } d && BinaryPrimitives.ReadUInt16BigEndian(d) is var c && c <= 7 ? (LayerColor)c : LayerColor.None;

    public static byte[] EncodeLocks(LayerLocks locks)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)locks);
        return b;
    }

    /// <summary>'lclr' is four 16-bit values; the first is the color, the rest are zero.</summary>
    public static byte[] EncodeColor(LayerColor color)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)color);
        return b;
    }

    /// <summary>
    /// The record flag's "transparency protected" bit for <paramref name="node"/>: the stored bit while the locks are as the
    /// file had them, otherwise whether Lock Transparent Pixels itself is on (Lock All is stored in 'lspf' alone).
    /// </summary>
    public static bool TransparencyFlag(LayerNode node, PsdLayerRecord? src) =>
        src is not null && ReadLocks(src) == node.Locks
            ? src.TransparencyLocked
            : (node.Locks & LayerLocks.Transparency) != 0;

    /// <summary>
    /// Brings the 'lspf' and 'lclr' blocks in <paramref name="blocks"/> (the source record's, in order) up to date with the
    /// node: unchanged values keep their bytes, changed ones are rewritten in place, new ones added.
    /// </summary>
    public static void Refresh(LayerNode node, PsdLayerRecord? src, List<(string Key, byte[] Data)> blocks)
    {
        var storedLocks = src is null ? LayerLocks.None : ReadLocks(src);
        if (node.Locks != storedLocks) Set(blocks, LocksKey, EncodeLocks(node.Locks), add: node.Locks != LayerLocks.None);
        var storedColor = src is null ? LayerColor.None : ReadColor(src);
        if (node.Color != storedColor) Set(blocks, ColorKey, EncodeColor(node.Color), add: node.Color != LayerColor.None);
    }

    private static void Set(List<(string Key, byte[] Data)> blocks, string key, byte[] data, bool add)
    {
        int i = blocks.FindIndex(b => b.Key == key);
        if (i >= 0) blocks[i] = (key, data);
        else if (add) blocks.Add((key, data));
    }

    /// <summary>The link group of each layer record (bottom to top), or null when the file has none or they do not match.</summary>
    public static int[]? ReadLinkGroups(PsdFile file)
    {
        if (file.FindResource(LinkResourceId)?.Data is not { } data || data.Length / 2 != file.Layers.Count) return null;
        var groups = new int[file.Layers.Count];
        for (int i = 0; i < groups.Length; i++) groups[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i * 2));
        return groups;
    }

    /// <summary>
    /// The link group of every record the writer emits for <paramref name="doc"/>, in its order: a group's closing divider,
    /// its children, then its folder record (both carry the group's value).
    /// </summary>
    public static List<int> LinkGroupsInRecordOrder(Document doc)
    {
        var list = new List<int>();
        void Walk(LayerNode node)
        {
            if (node is LayerGroup g)
            {
                list.Add(g.LinkGroup);
                foreach (var child in g.Children) Walk(child);
            }
            list.Add(node.LinkGroup);
        }
        foreach (var child in doc.Root.Children) Walk(child);
        return list;
    }

    public static byte[] EncodeLinkGroups(IReadOnlyList<int> groups)
    {
        var b = new byte[groups.Count * 2];
        for (int i = 0; i < groups.Count; i++) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(i * 2), (ushort)Math.Clamp(groups[i], 0, ushort.MaxValue));
        return b;
    }
}
