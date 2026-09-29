using System.Buffers.Binary;
using System.Text;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>A layer's reference to a pattern by name and ID (a pattern fill layer, Pattern Overlay, a pattern stroke).</summary>
/// <param name="Source">Where the reference was found: the tagged block key ("PtFl", "lfx2", ...).</param>
public sealed record PsdPatternReference(string LayerName, string Source, string? PatternName, string PatternId);

/// <summary>
/// The document's patterns: the global 'Patt' (8-bit), 'Pat2' (16-bit) and 'Pat3' (32-bit) tagged blocks, and the
/// references to them in layer data.
/// </summary>
/// <remarks>
/// <para>Layout, from Adobe's file format specification: the block is a list of patterns, each a 4-byte length and
/// that many bytes, padded to a multiple of 4. A pattern is: version (1), image mode, height and width (2 bytes
/// each), a Unicode name, a Pascal-string ID, for indexed patterns a 768-byte palette plus 4 bytes, then a "virtual
/// memory array list": version (3), length, bounding rectangle, channel count, and count + 2 arrays (the color
/// channels, then a user mask and the sheet (transparency) mask). Each array is a "written" flag and, if written, a
/// length, depth, rectangle, depth again, a compression byte (0 raw, 1 PackBits rows with 2-byte counts) and data.</para>
/// <para>Pattern transparency is read from whichever extra array is present (the sheet mask first) and written as the
/// sheet mask.</para>
/// <para>Layers refer to a pattern with a 'Ptrn' descriptor object holding its name ("Nm  ") and ID ("Idnt").
/// Photoshop resolves an ID from the file's pattern blocks before its own library, so a file whose layers use a
/// pattern must carry it: <see cref="PsdWriter"/> appends any pattern of <see cref="Document.Patterns"/> that a layer
/// refers to but the file's block lacks, leaving the existing block bytes as they were.</para>
/// </remarks>
public static class PsdPatterns
{
    /// <summary>The block key for patterns of a document's bit depth.</summary>
    public static string KeyFor(int bitDepth) => bitDepth switch { 16 => "Pat2", 32 => "Pat3", _ => "Patt" };

    public static bool IsPatternKey(string key) => key is "Patt" or "Pat2" or "Pat3";

    /// <summary>Every pattern in the file's pattern blocks. Unreadable patterns are skipped, never fatal.</summary>
    public static List<Pattern> Read(PsdFile file)
    {
        var list = new List<Pattern>();
        foreach (var block in file.GlobalBlocks.Where(b => IsPatternKey(b.Key) && b.Data is not null))
            list.AddRange(Decode(block.Data!));
        return list;
    }

    /// <summary>The patterns in one block's data.</summary>
    public static List<Pattern> Decode(byte[] data)
    {
        var list = new List<Pattern>();
        int pos = 0;
        while (pos + 4 <= data.Length)
        {
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos)), (uint)(data.Length - pos - 4));
            if (length <= 0) break;
            try
            {
                if (DecodeOne(data.AsSpan(pos + 4, length)) is { } p) list.Add(p);
            }
            catch (Exception ex) when (ex is PsdFormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
            {
                // A damaged pattern must not stop the document from opening; the block itself is saved unchanged.
            }
            pos += 4 + ((length + 3) & ~3);
        }
        return list;
    }

    private static Pattern? DecodeOne(ReadOnlySpan<byte> d)
    {
        var r = new SpanReader(d);
        if (r.U32() != 1) return null;
        var mode = (ColorMode)r.U32();
        int height = r.I16(), width = r.I16();
        string name = r.Unicode().TrimEnd('\0');
        string id = r.Pascal();
        byte[]? palette = null;
        if (mode == ColorMode.Indexed)
        {
            palette = r.Bytes(768).ToArray();
            r.Skip(4);
        }
        if (width <= 0 || height <= 0 || mode is not (ColorMode.Rgb or ColorMode.Grayscale or ColorMode.Indexed)) return null;

        if (r.U32() != 3) return null;
        r.Skip(4); // list length
        int top = r.I32(), left = r.I32(), bottom = r.I32(), right = r.I32();
        int channels = (int)r.U32();
        int w = right - left, h = bottom - top;
        if (w <= 0 || h <= 0 || channels is < 1 or > 56) return null;

        var arrays = new Plane?[channels + 2];
        for (int i = 0; i < channels + 2; i++)
        {
            if (r.U32() == 0) continue;
            int length = (int)r.U32();
            if (length == 0) continue;
            var body = new SpanReader(r.Bytes(length));
            int depth = (int)body.U32();
            int ct = body.I32(), cl = body.I32(), cb = body.I32(), cr = body.I32();
            body.Skip(2);
            var compression = (PsdCompression)body.U8();
            int cw = cr - cl, chh = cb - ct;
            if (depth is not (8 or 16 or 32) || cw != w || chh != h) continue;
            arrays[i] = ChannelDecoder.Decode(body.Rest, compression, w, h, depth, psb: false);
        }

        Plane? alpha = arrays[channels + 1] ?? arrays[channels];
        if (mode == ColorMode.Indexed)
        {
            if (arrays[0] is not { BitDepth: 8 } index) return null;
            var rgb = Enumerable.Range(0, 3).Select(_ => Plane.Create(w, h, 8)).ToArray();
            for (int i = 0; i < w * h; i++)
                for (int c = 0; c < 3; c++) rgb[c].Data[i] = palette![index.Data[i] * 3 + c];
            return new Pattern(id, name, new Raster(ColorMode.Rgb, rgb, Same(alpha, rgb[0])));
        }
        int colors = mode.ColorChannelCount();
        if (arrays.Take(colors).Any(p => p is null)) return null;
        var planes = arrays.Take(colors).Select(p => p!).ToArray();
        if (planes.Any(p => p.BitDepth != planes[0].BitDepth)) return null;
        return new Pattern(id, name, new Raster(mode, planes, Same(alpha, planes[0])));
    }

    private static Plane? Same(Plane? alpha, Plane like) => alpha is not null && alpha.BitDepth == like.BitDepth ? alpha : null;

    /// <summary>Block data for <paramref name="patterns"/>, each stored at <paramref name="bitDepth"/> (8, 16 or 32).</summary>
    public static byte[] Encode(IEnumerable<Pattern> patterns, int bitDepth)
    {
        var o = new MemoryStream();
        foreach (var p in patterns)
        {
            var one = EncodeOne(p, bitDepth);
            WriteU32(o, (uint)one.Length);
            o.Write(one);
            while (o.Length % 4 != 0) o.WriteByte(0);
        }
        return o.ToArray();
    }

    private static byte[] EncodeOne(Pattern p, int bitDepth)
    {
        var o = new MemoryStream();
        var src = p.Pixels;
        int w = p.Width, h = p.Height, colors = src.ColorMode.ColorChannelCount();
        WriteU32(o, 1);
        WriteU32(o, (uint)src.ColorMode);
        WriteU16(o, (ushort)h);
        WriteU16(o, (ushort)w);
        WriteU32(o, (uint)(p.Name.Length + 1));
        o.Write(Encoding.BigEndianUnicode.GetBytes(p.Name + "\0"));
        var id = Encoding.ASCII.GetBytes(p.Id.Length > 255 ? p.Id[..255] : p.Id);
        o.WriteByte((byte)id.Length);
        o.Write(id);

        var list = new MemoryStream();
        WriteI32(list, 0); WriteI32(list, 0); WriteI32(list, h); WriteI32(list, w);
        WriteU32(list, (uint)colors);
        for (int i = 0; i < colors + 2; i++)
        {
            Plane? plane = i < colors ? src.ColorPlanes[i] : i == colors + 1 ? src.Alpha : null;
            if (plane is null)
            {
                WriteU32(list, 0);
                continue;
            }
            var converted = Convert(plane, bitDepth);
            var raw = ChannelEncoder.ToBigEndian(converted);
            var data = new MemoryStream();
            byte compression = 0;
            if (bitDepth == 8)
            {
                compression = 1;
                var rows = Enumerable.Range(0, h).Select(y => ChannelEncoder.PackBits(raw.AsSpan(y * w, w))).ToList();
                foreach (var row in rows) WriteU16(data, (ushort)row.Length);
                foreach (var row in rows) data.Write(row);
            }
            else data.Write(raw);
            WriteU32(list, 1);
            WriteU32(list, (uint)(4 + 16 + 2 + 1 + data.Length));
            WriteU32(list, (uint)bitDepth);
            WriteI32(list, 0); WriteI32(list, 0); WriteI32(list, h); WriteI32(list, w);
            WriteU16(list, (ushort)bitDepth);
            list.WriteByte(compression);
            list.Write(data.ToArray());
        }
        WriteU32(o, 3);
        WriteU32(o, (uint)list.Length);
        o.Write(list.ToArray());
        return o.ToArray();
    }

    private static Plane Convert(Plane p, int bitDepth)
    {
        if (p.BitDepth == bitDepth) return p;
        var o = Plane.Create(p.Width, p.Height, bitDepth);
        for (int i = 0, n = p.Width * p.Height; i < n; i++)
        {
            float v = p.GetNormalized(i);
            switch (bitDepth)
            {
                case 8: o.Data[i] = (byte)MathF.Round(v * 255f); break;
                case 16: o.AsUInt16()[i] = (ushort)MathF.Round(v * 65535f); break;
                default: o.AsSingle()[i] = v; break;
            }
        }
        return o;
    }

    // ---- References ----------------------------------------------------------------------------------

    private static readonly string[] ReferenceKeys = ["PtFl", "lfx2", "lmfx", "vstk"];

    /// <summary>Every pattern reference in the file's layers, in layer order.</summary>
    public static List<PsdPatternReference> References(PsdFile file)
    {
        var list = new List<PsdPatternReference>();
        foreach (var layer in file.Layers)
            foreach (var block in layer.Blocks.Where(b => ReferenceKeys.Contains(b.Key) && b.Data is not null))
            {
                Descriptor? d;
                try
                {
                    d = DescriptorReader.ReadVersioned(block.Data!, block.Key is "lfx2" or "lmfx" ? 4 : 0);
                }
                catch (Exception ex) when (ex is PsdFormatException or ArgumentException or IndexOutOfRangeException)
                {
                    continue;
                }
                Collect(d, layer.Name, block.Key, list);
            }
        return list;
    }

    private static void Collect(Descriptor d, string layer, string source, List<PsdPatternReference> into)
    {
        foreach (var (key, value) in d.Items)
        {
            if (value is ObjectValue { Value: var o })
            {
                if (key == "Ptrn" && o.Text("Idnt") is { Length: > 0 } id) into.Add(new(layer, source, o.Text("Nm  ")?.TrimEnd('\0'), id.TrimEnd('\0')));
                else Collect(o, layer, source, into);
            }
            else if (value is ListValue list)
                foreach (var item in list.Items.OfType<ObjectValue>()) Collect(item.Value, layer, source, into);
        }
    }

    /// <summary>
    /// Block data with <paramref name="added"/> appended to <paramref name="existing"/> (kept byte for byte, padded to
    /// a multiple of 4 first so the new patterns start aligned).
    /// </summary>
    public static byte[] Append(byte[] existing, IEnumerable<Pattern> added, int bitDepth)
    {
        var o = new MemoryStream();
        o.Write(existing);
        while (o.Length % 4 != 0) o.WriteByte(0);
        o.Write(Encode(added, bitDepth));
        return o.ToArray();
    }

    /// <summary>True when <paramref name="data"/> contains <paramref name="id"/> as a descriptor string (UTF-16BE).</summary>
    internal static bool Mentions(byte[] data, string id) =>
        data.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes(id)) >= 0;

    private static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
    private static void WriteI32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
    private static void WriteU16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }

    private ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _d = data;
        private int _p;

        public readonly ReadOnlySpan<byte> Rest => _d[_p..];
        public byte U8() => _d[_p++];
        public uint U32() { var v = BinaryPrimitives.ReadUInt32BigEndian(_d[_p..]); _p += 4; return v; }
        public int I32() { var v = BinaryPrimitives.ReadInt32BigEndian(_d[_p..]); _p += 4; return v; }
        public short I16() { var v = BinaryPrimitives.ReadInt16BigEndian(_d[_p..]); _p += 2; return v; }
        public void Skip(int n) => _p += n;

        public ReadOnlySpan<byte> Bytes(int n)
        {
            if (n < 0 || _p + n > _d.Length) throw new PsdFormatException("Pattern data truncated");
            var s = _d.Slice(_p, n);
            _p += n;
            return s;
        }

        public string Unicode()
        {
            int n = (int)U32();
            return Encoding.BigEndianUnicode.GetString(Bytes(checked(n * 2)));
        }

        public string Pascal()
        {
            int n = U8();
            return Encoding.ASCII.GetString(Bytes(n));
        }
    }
}
