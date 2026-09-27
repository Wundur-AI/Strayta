using System.Buffers.Binary;
using System.Text;
using Strayta.Core;

namespace Strayta.Psd;

public sealed class PsdWriteOptions
{
    /// <summary>
    /// The flattened image to store (straight alpha, in the document's color mode and bit depth).
    /// Editors should pass a fresh render so other applications see the current state; when null the
    /// document's existing <see cref="Document.Composite"/> is written.
    /// </summary>
    public Raster? Composite { get; init; }
}

/// <summary>
/// Writes a <see cref="Document"/> as PSD or PSB. Layers read from a PSD keep every block this library
/// does not model (text, smart objects, effects, ...) byte for byte; only the modeled properties are rewritten.
/// </summary>
public static class PsdWriter
{
    // Image resources that describe the layer structure or a thumbnail; Photoshop rebuilds them, and stale
    // copies could disagree with edited layers.
    private static readonly HashSet<int> DroppedResources = [1024, 1026, 1033, 1036, 1069, 1072, 1073];

    // Layer blocks the writer regenerates from the model.
    private static readonly HashSet<string> ManagedBlocks = ["luni", "lsct", "lsdk", "iOpa"];

    private static readonly HashSet<string> WideLengthKeys =
        ["LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD"];

    /// <summary>Writes to a temporary file first and then replaces <paramref name="path"/>, so a failed save never corrupts the original.</summary>
    public static void Save(Document doc, string path, PsdWriteOptions? options = null)
    {
        string temp = path + ".saving";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                Write(doc, stream, options);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static void Write(Document doc, Stream stream, PsdWriteOptions? options = null)
    {
        if (doc.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale))
            throw new NotSupportedException($"Saving {doc.ColorMode} documents is not supported yet.");
        if (doc.BitDepth is not (8 or 16 or 32))
            throw new NotSupportedException($"Saving {doc.BitDepth}-bit documents is not supported yet.");

        var source = doc.SourceData as PsdFile;
        bool psb = source?.Header.IsPsb == true || doc.Width > 30_000 || doc.Height > 30_000;
        var composite = options?.Composite ?? doc.Composite;
        int colorChannels = doc.ColorMode.ColorChannelCount();
        bool compositeAlpha = composite?.Alpha is not null;

        // Saved selections and spot channels live after the color (and transparency) channels of the
        // original composite; keep them as long as the canvas size is unchanged.
        var extraChannels = source is null || source.Header.Width != doc.Width || source.Header.Height != doc.Height
            ? []
            : source.CompositeChannels.Skip(colorChannels + (source.CompositeHasTransparency ? 1 : 0))
                .Where(p => p.BitDepth == doc.BitDepth).ToList();

        var w = new BigEndianWriter(stream);

        // Header
        w.Ascii("8BPS");
        w.U16((ushort)(psb ? 2 : 1));
        w.Zeros(6);
        w.U16((ushort)(colorChannels + (compositeAlpha ? 1 : 0) + extraChannels.Count));
        w.U32((uint)doc.Height);
        w.U32((uint)doc.Width);
        w.U16((ushort)doc.BitDepth);
        w.U16((ushort)doc.ColorMode);

        // Color mode data
        var colorModeData = source?.ColorModeData ?? [];
        w.U32((uint)colorModeData.Length);
        w.Bytes(colorModeData);

        WriteResources(w, doc, source);
        WriteLayerAndMaskInfo(w, doc, source, psb, compositeAlpha);
        WriteComposite(w, doc, composite, colorChannels, extraChannels, psb);
    }

    private static void WriteResources(BigEndianWriter w, Document doc, PsdFile? source)
    {
        var section = new MemoryStream();
        var s = new BigEndianWriter(section);
        void Write(ImageResource r)
        {
            s.Ascii(r.Signature);
            s.U16((ushort)r.Id);
            s.PascalString(r.Name, 2);
            s.U32((uint)r.Data.Length);
            s.Bytes(r.Data);
            if ((r.Data.Length & 1) != 0) s.Zeros(1);
        }

        // Resolution is modeled (Image Size can change it): the stored block is rewritten, keeping its display units.
        var resolution = source?.FindResource(PsdResolution.ResourceId);
        bool writeResolution = resolution is not null || source is null || doc.Resolution != 72;
        foreach (var r in source?.Resources ?? [])
        {
            if (DroppedResources.Contains(r.Id) || r.Id == PsdVersionInfo.ResourceId) continue;
            Write(r.Id == PsdResolution.ResourceId ? r with { Data = PsdResolution.Write(doc.Resolution, r.Data) } : r);
        }
        if (writeResolution && resolution is null)
            Write(new ImageResource("8BIM", PsdResolution.ResourceId, "", PsdResolution.Write(doc.Resolution, null)));

        // Record that Strayta wrote this file, so its composite is not mistaken for a Photoshop render.
        var info = PsdVersionInfo.Read(source?.FindResource(PsdVersionInfo.ResourceId)?.Data);
        var version = new PsdVersionInfo(info?.Version ?? 1, true, "Strayta", info?.Reader ?? "Adobe Photoshop", info?.FileVersion ?? 1);
        Write(new ImageResource("8BIM", PsdVersionInfo.ResourceId, "", version.ToBytes()));
        w.U32((uint)section.Length);
        w.Bytes(section.ToArray());
    }

    // ---- Layers ----------------------------------------------------------------------------------

    private sealed record Record(
        PixelRect Rect,
        List<(short Id, byte[] Data)> Channels,
        string BlendKey,
        byte Opacity,
        bool Clipped,
        byte Flags,
        byte[] MaskData,
        byte[] BlendingRanges,
        string Name,
        List<(string Key, byte[] Data)> Blocks);

    private static void WriteLayerAndMaskInfo(BigEndianWriter w, Document doc, PsdFile? source, bool psb, bool compositeAlpha)
    {
        var records = new List<Record>();
        foreach (var child in doc.Root.Children) Flatten(child, doc, psb, records);

        byte[] layerInfo = records.Count == 0 ? [] : LayerInfo(records, psb, compositeAlpha);
        bool layersInBlock = doc.BitDepth != 8 && records.Count > 0;

        var section = new MemoryStream();
        var s = new BigEndianWriter(section);

        // 8-bit documents store layers here; 16/32-bit ones store them in an Lr16/Lr32 block below.
        if (layersInBlock) s.Length(0, psb);
        else
        {
            s.Length(layerInfo.Length, psb);
            s.Bytes(layerInfo);
        }

        var globalMask = source?.GlobalLayerMaskInfo ?? [];
        s.U32((uint)globalMask.Length);
        s.Bytes(globalMask);

        if (layersInBlock)
            WriteBlock(s, doc.BitDepth == 16 ? "Lr16" : "Lr32", layerInfo, psb);
        foreach (var block in source?.GlobalBlocks ?? [])
        {
            if (block.Key is "Layr" or "Lr16" or "Lr32") continue;
            WriteBlock(s, block.Key, RequireData(block, "document"), psb, block.Signature);
        }

        while (section.Length % 4 != 0) section.WriteByte(0);
        w.Length(section.Length, psb);
        w.Bytes(section.ToArray());
    }

    private static byte[] LayerInfo(List<Record> records, bool psb, bool compositeAlpha)
    {
        var o = new MemoryStream();
        var s = new BigEndianWriter(o);
        // A negative count tells readers the composite's first extra channel is its transparency.
        s.U16((ushort)(short)(compositeAlpha ? -records.Count : records.Count));

        foreach (var r in records)
        {
            s.I32(r.Rect.Top); s.I32(r.Rect.Left); s.I32(r.Rect.Bottom); s.I32(r.Rect.Right);
            s.U16((ushort)r.Channels.Count);
            foreach (var (id, data) in r.Channels)
            {
                s.U16((ushort)id);
                s.Length(data.Length, psb);
            }
            s.Ascii("8BIM");
            s.Ascii(r.BlendKey);
            s.U8(r.Opacity);
            s.U8(r.Clipped ? (byte)1 : (byte)0);
            s.U8(r.Flags);
            s.U8(0);

            var extra = new MemoryStream();
            var e = new BigEndianWriter(extra);
            e.U32((uint)r.MaskData.Length);
            e.Bytes(r.MaskData);
            e.U32((uint)r.BlendingRanges.Length);
            e.Bytes(r.BlendingRanges);
            e.PascalString(r.Name, 4);
            foreach (var (key, data) in r.Blocks) WriteBlock(e, key, data, psb);

            s.U32((uint)extra.Length);
            s.Bytes(extra.ToArray());
        }

        foreach (var r in records)
            foreach (var (_, data) in r.Channels)
                s.Bytes(data);

        if (o.Length % 2 != 0) o.WriteByte(0);
        return o.ToArray();
    }

    /// <summary>Emits records bottom to top; a group becomes a divider, its children, then its folder record.</summary>
    private static void Flatten(LayerNode node, Document doc, bool psb, List<Record> records)
    {
        var src = node.SourceData as PsdLayerRecord;
        switch (node)
        {
            case LayerGroup group:
            {
                var groupSource = group.SourceData as PsdGroupRecords;
                src = groupSource?.Folder;
                var divider = groupSource?.Divider;
                var dividerBlocks = new List<(string, byte[])> { ("lsct", SectionBlock(PsdSectionType.BoundingDivider, null)) };
                foreach (var b in divider?.Blocks ?? [])
                    if (!ManagedBlocks.Contains(b.Key)) dividerBlocks.Add((b.Key, RequireData(b, "a group end")));
                records.Add(new Record(PixelRect.Empty, EmptyChannels(doc, psb), divider?.BlendModeKey ?? "norm", divider?.Opacity ?? 255,
                    false, divider?.Flags ?? 0x18, [], divider?.BlendingRanges ?? DefaultRanges(doc),
                    divider?.PascalName ?? "</Layer group>", dividerBlocks));
                foreach (var child in group.Children) Flatten(child, doc, psb, records);

                var (maskData, maskChannels) = Mask(group.Mask, src, psb);
                var folderChannels = EmptyChannels(doc, psb);
                folderChannels.AddRange(maskChannels);
                // Photoshop keeps "pass" only in the section block; the record itself says "norm".
                string groupKey = PsdBlocks.BlendKeyOf(group.BlendMode);
                string recordKey = group.BlendMode == BlendMode.PassThrough ? "norm" : groupKey;
                var blocks = Blocks(group, src);
                blocks.Insert(0, ("lsct", SectionBlock(group.Expanded ? PsdSectionType.OpenFolder : PsdSectionType.ClosedFolder, groupKey)));
                records.Add(new Record(PixelRect.Empty, folderChannels, recordKey, Opacity(group.Opacity), group.Clipped,
                    GroupFlags(group, src), maskData, src?.BlendingRanges ?? DefaultRanges(doc), group.Name, blocks));
                break;
            }

            case PixelLayer layer:
            {
                var channels = new List<(short, byte[])>();
                var rect = layer.Pixels is null ? PixelRect.Empty : layer.Bounds;
                if (layer.Pixels is { } px)
                {
                    if (px.Alpha is not null) channels.Add((PsdChannelId.Transparency, ChannelEncoder.EncodeLayerChannel(px.Alpha, psb)));
                    for (short c = 0; c < px.ColorPlanes.Count; c++)
                        channels.Add((c, ChannelEncoder.EncodeLayerChannel(px.ColorPlanes[c], psb)));
                }
                else channels.AddRange(EmptyChannels(doc, psb));

                var (maskData, maskChannels) = Mask(layer.Mask, src, psb);
                channels.AddRange(maskChannels);
                records.Add(new Record(rect, channels, PsdBlocks.BlendKeyOf(layer.BlendMode), Opacity(layer.Opacity), layer.Clipped,
                    Flags(layer, src, layer.TransparencyLocked), maskData, src?.BlendingRanges ?? DefaultRanges(doc), layer.Name, Blocks(layer, src)));
                break;
            }

            case AdjustmentLayer adj when src is not null:
            {
                var (maskData, maskChannels) = Mask(adj.Mask, src, psb);
                var channels = EmptyChannels(doc, psb);
                channels.AddRange(maskChannels);
                var blocks = Blocks(adj, src);
                PsdAdjustmentWriter.Refresh(adj, src, blocks); // edited settings replace the stored block
                records.Add(new Record(PixelRect.Empty, channels, PsdBlocks.BlendKeyOf(adj.BlendMode), Opacity(adj.Opacity), adj.Clipped,
                    Flags(adj, src), maskData, src.BlendingRanges, adj.Name, blocks));
                break;
            }

            case AdjustmentLayer { Adjustment: { } adjustment } adj:
            {
                // A layer created in the editor: no source record, so the adjustment block is encoded from the model.
                var (maskData, maskChannels) = Mask(adj.Mask, null, psb);
                var channels = EmptyChannels(doc, psb);
                channels.AddRange(maskChannels);
                var blocks = Blocks(adj, null);
                blocks.Add((PsdAdjustmentWriter.KeyOf(adjustment), PsdAdjustmentWriter.Encode(adjustment)));
                // Like Photoshop, flag the (empty) pixel data as irrelevant to the image.
                records.Add(new Record(PixelRect.Empty, channels, PsdBlocks.BlendKeyOf(adj.BlendMode), Opacity(adj.Opacity), adj.Clipped,
                    (byte)(0x18 | Flags(adj, null)), maskData, DefaultRanges(doc), adj.Name, blocks));
                break;
            }

            case AdjustmentLayer adj:
                throw new NotSupportedException($"Saving new adjustment layers without settings (\"{adj.Name}\") is not supported.");
        }
    }

    /// <summary>The source record's blocks with the ones the model owns regenerated.</summary>
    private static List<(string, byte[])> Blocks(LayerNode node, PsdLayerRecord? src)
    {
        var blocks = new List<(string, byte[])>();
        blocks.Add(("luni", UnicodeName(node.Name)));
        if (node.FillOpacity < 1f || src?.FindBlock("iOpa") is not null)
            blocks.Add(("iOpa", [Opacity(node.FillOpacity), 0, 0, 0]));
        foreach (var b in src?.Blocks ?? [])
        {
            if (ManagedBlocks.Contains(b.Key)) continue;
            blocks.Add((b.Key, RequireData(b, $"layer \"{node.Name}\"")));
        }
        return blocks;
    }

    private static (byte[] Data, List<(short, byte[])> Channels) Mask(LayerMask? mask, PsdLayerRecord? src, bool psb)
    {
        if (mask is null) return ([], []);
        var o = new MemoryStream();
        var s = new BigEndianWriter(o);
        var channels = new List<(short, byte[])> { (PsdChannelId.UserMask, ChannelEncoder.EncodeLayerChannel(mask.Pixels, psb)) };

        var bounds = mask.Pixels is null ? PixelRect.Empty : mask.Bounds;
        s.I32(bounds.Top); s.I32(bounds.Left); s.I32(bounds.Bottom); s.I32(bounds.Right);
        s.U8(mask.DefaultColor);
        s.U8((byte)((mask.PositionRelativeToLayer ? 0x01 : 0) | (mask.Disabled ? 0x02 : 0)));

        // Keep the combined vector+pixel ("real") mask when the source had one.
        if (src?.Mask is { RealRect: { } real, RealFlags: { } realFlags, RealDefaultColor: { } realDefault }
            && src.ChannelData.TryGetValue(PsdChannelId.RealUserMask, out var realPlane))
        {
            s.U8(realFlags);
            s.U8(realDefault);
            s.I32(real.Top); s.I32(real.Left); s.I32(real.Bottom); s.I32(real.Right);
            channels.Add((PsdChannelId.RealUserMask, ChannelEncoder.EncodeLayerChannel(realPlane, psb)));
        }
        else s.Zeros(2);

        return (o.ToArray(), channels);
    }

    private static List<(short, byte[])> EmptyChannels(Document doc, bool psb)
    {
        var list = new List<(short, byte[])> { (PsdChannelId.Transparency, ChannelEncoder.EncodeLayerChannel(null, psb)) };
        for (short c = 0; c < doc.ColorMode.ColorChannelCount(); c++) list.Add((c, ChannelEncoder.EncodeLayerChannel(null, psb)));
        return list;
    }

    /// <summary>Source-over ranges for the composite and each channel: "blend if" fully open.</summary>
    private static byte[] DefaultRanges(Document doc)
    {
        int pairs = doc.ColorMode.ColorChannelCount() + 1;
        var r = new byte[pairs * 8];
        for (int i = 0; i < pairs * 2; i++) { r[i * 4 + 2] = 255; r[i * 4 + 3] = 255; }
        return r;
    }

    private static byte Flags(LayerNode node, PsdLayerRecord? src, bool locked = false) =>
        (byte)((src?.Flags ?? 0) & ~0x03 | (locked ? 0x01 : 0) | (node.Visible ? 0 : 0x02));

    /// <summary>Photoshop marks folder records with bits 3 and 4 ("pixel data irrelevant").</summary>
    private static byte GroupFlags(LayerGroup g, PsdLayerRecord? src) =>
        (byte)((src?.Flags ?? 0x18) & ~0x03 | (g.Visible ? 0 : 0x02));

    private static byte Opacity(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);

    private static byte[] UnicodeName(string name)
    {
        var o = new MemoryStream();
        var s = new BigEndianWriter(o);
        s.U32((uint)name.Length);
        s.Bytes(Encoding.BigEndianUnicode.GetBytes(name));
        return o.ToArray();
    }

    private static byte[] SectionBlock(PsdSectionType type, string? blendKey)
    {
        var o = new MemoryStream();
        var s = new BigEndianWriter(o);
        s.U32((uint)type);
        if (blendKey is not null) { s.Ascii("8BIM"); s.Ascii(blendKey); }
        return o.ToArray();
    }

    private static byte[] RequireData(TaggedBlock block, string owner) => block.Data ??
        throw new InvalidOperationException(
            $"Block '{block.Key}' on {owner} was too large to keep in memory, so it cannot be saved. " +
            $"Open the file with a larger {nameof(PsdReadOptions.MaxRawBlockBytes)}.");

    /// <summary>
    /// Writes the exact data length (Photoshop itself stores odd lengths for some blocks) and pads the
    /// data to an even size; readers skip the padding.
    /// </summary>
    private static void WriteBlock(BigEndianWriter w, string key, byte[] data, bool psb, string signature = "8BIM")
    {
        w.Ascii(signature);
        w.Ascii(key);
        w.Length(data.Length, psb && WideLengthKeys.Contains(key));
        w.Bytes(data);
        if ((data.Length & 1) != 0) w.Zeros(1);
    }

    // ---- Composite --------------------------------------------------------------------------------

    /// <summary>Photoshop stores a transparent composite blended over white; see the matching reader code.</summary>
    private static void WriteComposite(BigEndianWriter w, Document doc, Raster? composite, int colorChannels,
        IReadOnlyList<Plane> extraChannels, bool psb)
    {
        int rowBytes = doc.Width * (doc.BitDepth / 8);
        var channels = new List<byte[]>();
        if (composite is null || composite.Width != doc.Width || composite.Height != doc.Height)
        {
            // No image available: write opaque white so the file is still valid.
            for (int c = 0; c < colorChannels; c++)
                channels.Add(ChannelEncoder.ToBigEndian(Fill(doc, 1f)));
        }
        else
        {
            for (int c = 0; c < colorChannels; c++)
            {
                var plane = composite.ColorPlanes[Math.Min(c, composite.ColorPlanes.Count - 1)];
                channels.Add(ChannelEncoder.ToBigEndian(composite.Alpha is { } a ? MatteOverWhite(plane, a) : plane));
            }
            if (composite.Alpha is { } alpha) channels.Add(ChannelEncoder.ToBigEndian(alpha));
        }
        foreach (var extra in extraChannels) channels.Add(ChannelEncoder.ToBigEndian(extra));
        ChannelEncoder.WriteCompositeRle(w.Stream, channels, rowBytes, doc.Height, psb);
    }

    private static Plane MatteOverWhite(Plane color, Plane alpha)
    {
        var o = Plane.Create(color.Width, color.Height, color.BitDepth);
        int n = color.Width * color.Height;
        for (int i = 0; i < n; i++)
        {
            float a = alpha.GetNormalized(i);
            float v = color.GetNormalized(i) * a + (1f - a);
            switch (color.BitDepth)
            {
                case 8: o.Data[i] = (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f); break;
                case 16: o.AsUInt16()[i] = (ushort)Math.Clamp(MathF.Round(v * 65535f), 0f, 65535f); break;
                default: o.AsSingle()[i] = v; break;
            }
        }
        return o;
    }

    private static Plane Fill(Document doc, float value)
    {
        var p = Plane.Create(doc.Width, doc.Height, doc.BitDepth);
        switch (doc.BitDepth)
        {
            case 8: p.Data.AsSpan().Fill((byte)(value * 255)); break;
            case 16: p.AsUInt16().Fill((ushort)(value * 65535)); break;
            default: p.AsSingle().Fill(value); break;
        }
        return p;
    }
}

internal sealed class BigEndianWriter(Stream stream)
{
    public Stream Stream { get; } = stream;

    public void U8(byte v) => Stream.WriteByte(v);
    public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); Stream.Write(b); }
    public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); Stream.Write(b); }
    public void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); Stream.Write(b); }
    public void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); Stream.Write(b); }
    public void Length(long v, bool wide) { if (wide) U64((ulong)v); else U32(checked((uint)v)); }
    public void Ascii(string s) => Stream.Write(Encoding.ASCII.GetBytes(s));
    public void Bytes(ReadOnlySpan<byte> b) => Stream.Write(b);
    public void Zeros(int n) { for (int i = 0; i < n; i++) Stream.WriteByte(0); }

    /// <summary>Length byte plus Latin-1 text (max 255 chars), padded so the total is a multiple of <paramref name="padTo"/>.</summary>
    public void PascalString(string s, int padTo)
    {
        var bytes = Encoding.Latin1.GetBytes(s.Length > 255 ? s[..255] : s);
        U8((byte)bytes.Length);
        Bytes(bytes);
        int rem = (bytes.Length + 1) % padTo;
        if (rem != 0) Zeros(padTo - rem);
    }
}
