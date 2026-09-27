using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Strayta.Psd.Tests;

/// <summary>
/// Writes small, fully specified PSD/PSB files for tests. Deliberately simple and written from the
/// spec independently of the reader, so the two can check each other.
/// </summary>
internal sealed class PsdTestBuilder(int width, int height, bool psb = false)
{
    public sealed class Layer
    {
        public string Name = "Layer";
        public int Top, Left, Bottom, Right;
        public string Blend = "norm";
        public byte Opacity = 255;
        public byte Flags;
        public bool Clipped;
        /// <summary>Channel id -> 8-bit samples (Width*Height of the layer rect, or of the mask rect for -2).</summary>
        public Dictionary<short, byte[]> Channels = [];
        public ushort Compression; // 0 raw, 1 RLE, 2 zip
        public (int Top, int Left, int Bottom, int Right, byte Default, byte Flags)? Mask;
        public int? SectionType;
        public string? SectionBlend;
        public byte? FillOpacity;
        public bool WriteUnicodeName = true;
        public List<(string Key, byte[] Data)> ExtraBlocks = [];
    }

    public List<Layer> Layers { get; } = [];
    public bool NegativeLayerCount { get; set; }
    public byte[][]? Composite { get; set; }
    public ushort CompositeCompression { get; set; }

    public byte[] Build()
    {
        var o = new MemoryStream();
        Ascii(o, "8BPS");
        U16(o, (ushort)(psb ? 2 : 1));
        o.Write(new byte[6]);
        U16(o, (ushort)(Composite?.Length ?? 3));
        U32(o, (uint)height);
        U32(o, (uint)width);
        U16(o, 8);
        U16(o, 3); // RGB

        U32(o, 0); // color mode data
        U32(o, 0); // image resources

        var layerInfo = BuildLayerInfo();
        var lmi = new MemoryStream();
        Len(lmi, layerInfo.Length);
        lmi.Write(layerInfo);
        U32(lmi, 0); // global layer mask info
        Len(o, lmi.Length);
        lmi.Position = 0;
        lmi.CopyTo(o);

        if (Composite is not null)
        {
            U16(o, CompositeCompression);
            if (CompositeCompression == 1)
            {
                var rows = Composite.SelectMany(ch => Enumerable.Range(0, height).Select(y => PackBits(ch.AsSpan(y * width, width)))).ToList();
                foreach (var r in rows) RleCount(o, r.Length);
                foreach (var r in rows) o.Write(r);
            }
            else
            {
                foreach (var ch in Composite) o.Write(ch);
            }
        }
        return o.ToArray();
    }

    private byte[] BuildLayerInfo()
    {
        if (Layers.Count == 0) return [];
        var o = new MemoryStream();
        U16(o, (ushort)(short)(NegativeLayerCount ? -Layers.Count : Layers.Count));

        var channelBlobs = Layers.Select(l => l.Channels.ToDictionary(kv => kv.Key, kv => EncodeChannel(l, kv.Key, kv.Value))).ToList();

        for (int i = 0; i < Layers.Count; i++)
        {
            var l = Layers[i];
            I32(o, l.Top); I32(o, l.Left); I32(o, l.Bottom); I32(o, l.Right);
            U16(o, (ushort)l.Channels.Count);
            foreach (var (id, blob) in channelBlobs[i])
            {
                U16(o, (ushort)id);
                Len(o, blob.Length);
            }
            Ascii(o, "8BIM");
            Ascii(o, l.Blend);
            o.WriteByte(l.Opacity);
            o.WriteByte(l.Clipped ? (byte)1 : (byte)0);
            o.WriteByte(l.Flags);
            o.WriteByte(0);

            var extra = new MemoryStream();
            if (l.Mask is { } m)
            {
                U32(extra, 20);
                I32(extra, m.Top); I32(extra, m.Left); I32(extra, m.Bottom); I32(extra, m.Right);
                extra.WriteByte(m.Default);
                extra.WriteByte(m.Flags);
                extra.Write(new byte[2]);
            }
            else U32(extra, 0);
            U32(extra, 0); // blending ranges

            var nameBytes = Encoding.Latin1.GetBytes(l.Name);
            extra.WriteByte((byte)nameBytes.Length);
            extra.Write(nameBytes);
            for (int pad = (nameBytes.Length + 1) % 4; pad != 0 && pad < 4; pad++) extra.WriteByte(0);

            if (l.WriteUnicodeName)
            {
                var u = new MemoryStream();
                U32(u, (uint)l.Name.Length);
                u.Write(Encoding.BigEndianUnicode.GetBytes(l.Name));
                Block(extra, "luni", u.ToArray());
            }
            if (l.SectionType is { } st)
            {
                var s = new MemoryStream();
                U32(s, (uint)st);
                if (l.SectionBlend is { } sb) { Ascii(s, "8BIM"); Ascii(s, sb); }
                Block(extra, "lsct", s.ToArray());
            }
            if (l.FillOpacity is { } fo) Block(extra, "iOpa", [fo, 0, 0, 0]);
            foreach (var (key, data) in l.ExtraBlocks) Block(extra, key, data);

            U32(o, (uint)extra.Length);
            extra.Position = 0;
            extra.CopyTo(o);
        }

        foreach (var blobs in channelBlobs)
            foreach (var blob in blobs.Values)
                o.Write(blob);

        if (o.Length % 2 != 0) o.WriteByte(0);
        return o.ToArray();
    }

    private byte[] EncodeChannel(Layer l, short id, byte[] samples)
    {
        int w, h;
        if (id == -2 && l.Mask is { } m) { w = m.Right - m.Left; h = m.Bottom - m.Top; }
        else { w = l.Right - l.Left; h = l.Bottom - l.Top; }

        var o = new MemoryStream();
        U16(o, l.Compression);
        switch (l.Compression)
        {
            case 0:
                o.Write(samples);
                break;
            case 1:
                var rows = Enumerable.Range(0, h).Select(y => PackBits(samples.AsSpan(y * w, w))).ToList();
                foreach (var r in rows) RleCount(o, r.Length);
                foreach (var r in rows) o.Write(r);
                break;
            case 2:
                using (var z = new ZLibStream(o, CompressionLevel.Optimal, leaveOpen: true)) z.Write(samples);
                break;
        }
        return o.ToArray();
    }

    /// <summary>Straightforward PackBits encoder: runs of 3+ become repeats, everything else literals.</summary>
    public static byte[] PackBits(ReadOnlySpan<byte> src)
    {
        var o = new List<byte>();
        int i = 0;
        while (i < src.Length)
        {
            int run = 1;
            while (i + run < src.Length && run < 128 && src[i + run] == src[i]) run++;
            if (run >= 3)
            {
                o.Add((byte)(sbyte)(1 - run));
                o.Add(src[i]);
                i += run;
                continue;
            }
            int start = i;
            while (i < src.Length && i - start < 128)
            {
                if (i + 2 < src.Length && src[i] == src[i + 1] && src[i] == src[i + 2]) break;
                i++;
            }
            o.Add((byte)(i - start - 1));
            for (int k = start; k < i; k++) o.Add(src[k]);
        }
        return o.ToArray();
    }

    private void Block(Stream o, string key, byte[] data)
    {
        Ascii(o, "8BIM");
        Ascii(o, key);
        U32(o, (uint)data.Length);
        o.Write(data);
    }

    private void Len(Stream o, long v) { if (psb) U64(o, (ulong)v); else U32(o, (uint)v); }
    private void RleCount(Stream o, int v) { if (psb) U32(o, (uint)v); else U16(o, (ushort)v); }

    private static void Ascii(Stream o, string s) => o.Write(Encoding.ASCII.GetBytes(s));
    private static void U16(Stream o, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); o.Write(b); }
    private static void U32(Stream o, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); o.Write(b); }
    private static void I32(Stream o, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); o.Write(b); }
    private static void U64(Stream o, ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); o.Write(b); }
}
