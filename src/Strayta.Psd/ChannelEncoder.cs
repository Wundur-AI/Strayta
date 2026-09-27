using System.Buffers.Binary;
using System.IO.Compression;
using Strayta.Core;

namespace Strayta.Psd;

/// <summary>Encodes host-order <see cref="Plane"/>s into PSD channel data.</summary>
internal static class ChannelEncoder
{
    /// <summary>Big-endian sample bytes as stored in the file.</summary>
    public static byte[] ToBigEndian(Plane p)
    {
        var raw = (byte[])p.Data.Clone();
        if (BitConverter.IsLittleEndian && p.BitDepth > 8)
        {
            int size = p.BitDepth / 8;
            for (int i = 0; i < raw.Length; i += size) raw.AsSpan(i, size).Reverse();
        }
        return raw;
    }

    /// <summary>
    /// A layer channel: 2-byte compression, then data. 8-bit uses RLE (what Photoshop writes);
    /// 16/32-bit use ZIP.
    /// </summary>
    public static byte[] EncodeLayerChannel(Plane? p, bool psb)
    {
        var o = new MemoryStream();
        if (p is null || p.Width == 0 || p.Height == 0)
        {
            o.Write([0, 0]); // raw, no data
            return o.ToArray();
        }

        var raw = ToBigEndian(p);
        if (p.BitDepth == 8)
        {
            o.Write([0, (byte)PsdCompression.Rle]);
            WriteRle(o, raw, p.Width, p.Height, psb);
        }
        else
        {
            o.Write([0, (byte)PsdCompression.Zip]);
            using var z = new ZLibStream(o, CompressionLevel.Optimal, leaveOpen: true);
            z.Write(raw);
        }
        return o.ToArray();
    }

    /// <summary>The flattened image: one compression field, all row counts, then all packed rows.</summary>
    public static void WriteCompositeRle(Stream o, IReadOnlyList<byte[]> channels, int rowBytes, int height, bool psb)
    {
        o.Write([0, (byte)PsdCompression.Rle]);
        var packedRows = new List<byte[]>(channels.Count * height);
        foreach (var raw in channels)
            for (int y = 0; y < height; y++)
                packedRows.Add(PackBits(raw.AsSpan(y * rowBytes, rowBytes)));

        Span<byte> buf = stackalloc byte[4];
        foreach (var row in packedRows)
        {
            if (psb) { BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)row.Length); o.Write(buf); }
            else { BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)row.Length); o.Write(buf[..2]); }
        }
        foreach (var row in packedRows) o.Write(row);
    }

    private static void WriteRle(Stream o, byte[] raw, int width, int height, bool psb)
    {
        int rowBytes = raw.Length / height;
        var rows = new byte[height][];
        Parallel.For(0, height, y => rows[y] = PackBits(raw.AsSpan(y * rowBytes, rowBytes)));
        Span<byte> buf = stackalloc byte[4];
        foreach (var row in rows)
        {
            if (psb) { BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)row.Length); o.Write(buf); }
            else { BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)row.Length); o.Write(buf[..2]); }
        }
        foreach (var row in rows) o.Write(row);
    }

    /// <summary>PackBits: runs of 3+ equal bytes become repeat packets, everything else literal packets of up to 128.</summary>
    public static byte[] PackBits(ReadOnlySpan<byte> src)
    {
        var o = new List<byte>(src.Length + src.Length / 128 + 2);
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
}
