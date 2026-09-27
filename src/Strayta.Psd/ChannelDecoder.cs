using System.Buffers.Binary;
using System.IO.Compression;
using Strayta.Core;

namespace Strayta.Psd;

/// <summary>Decompresses PSD channel data into host-order <see cref="Plane"/>s.</summary>
internal static class ChannelDecoder
{
    public static int RowBytes(int width, int depth) => depth == 1 ? (width + 7) / 8 : width * (depth / 8);

    /// <summary>
    /// Decodes one channel whose compressed bytes are <paramref name="source"/> (after the 2-byte compression field).
    /// </summary>
    public static Plane Decode(ReadOnlySpan<byte> source, PsdCompression compression, int width, int height, int depth, bool psb)
    {
        int rowBytes = RowBytes(width, depth);
        var raw = new byte[checked((long)rowBytes * height)];

        switch (compression)
        {
            case PsdCompression.Raw:
                if (source.Length < raw.Length) throw new PsdFormatException($"Raw channel has {source.Length} bytes, expected {raw.Length}");
                source[..raw.Length].CopyTo(raw);
                break;

            case PsdCompression.Rle:
                int countSize = psb ? 4 : 2;
                if (source.Length < countSize * height) throw new PsdFormatException("RLE row table truncated");
                var counts = new int[height];
                for (int y = 0; y < height; y++)
                    counts[y] = psb
                        ? (int)BinaryPrimitives.ReadUInt32BigEndian(source[(y * 4)..])
                        : BinaryPrimitives.ReadUInt16BigEndian(source[(y * 2)..]);
                DecodeRleRows(source[(countSize * height)..], counts, raw, rowBytes);
                break;

            case PsdCompression.Zip:
            case PsdCompression.ZipWithPrediction:
                Inflate(source, raw);
                if (compression == PsdCompression.ZipWithPrediction)
                    Unpredict(raw, width, height, depth);
                break;

            default:
                throw new PsdFormatException($"Unknown compression method {(int)compression}");
        }

        return ToPlane(raw, width, height, depth);
    }

    /// <summary>Decodes consecutive PackBits rows into <paramref name="dest"/>; returns bytes consumed.</summary>
    public static int DecodeRleRows(ReadOnlySpan<byte> source, ReadOnlySpan<int> rowCounts, Span<byte> dest, int rowBytes)
    {
        int pos = 0;
        for (int y = 0; y < rowCounts.Length; y++)
        {
            int count = rowCounts[y];
            if (pos + count > source.Length) throw new PsdFormatException($"RLE row {y} runs past end of channel data");
            UnpackBits(source.Slice(pos, count), dest.Slice(y * rowBytes, rowBytes));
            pos += count;
        }
        return pos;
    }

    /// <summary>
    /// PackBits: a header n in 0..127 copies n+1 literal bytes; -127..-1 repeats the next byte 1-n times; -128 is a no-op.
    /// Rows that decode short are left zero-padded; excess output is dropped, matching Photoshop's tolerance.
    /// </summary>
    public static void UnpackBits(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int i = 0, o = 0;
        while (i < src.Length && o < dst.Length)
        {
            sbyte n = (sbyte)src[i++];
            if (n >= 0)
            {
                int len = Math.Min(n + 1, Math.Min(src.Length - i, dst.Length - o));
                src.Slice(i, len).CopyTo(dst[o..]);
                i += n + 1;
                o += len;
            }
            else if (n != -128)
            {
                if (i >= src.Length) break;
                int len = Math.Min(1 - n, dst.Length - o);
                dst.Slice(o, len).Fill(src[i++]);
                o += len;
            }
        }
    }

    public static void Inflate(ReadOnlySpan<byte> source, Span<byte> dest)
    {
        using var input = new MemoryStream(source.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        int read = zlib.ReadAtLeast(dest, dest.Length, throwOnEndOfStream: false);
        if (read < dest.Length) throw new PsdFormatException($"ZIP channel inflated to {read} bytes, expected {dest.Length}");
    }

    /// <summary>Reverses Photoshop's per-row delta encoding (big-endian data, before byte swapping).</summary>
    public static void Unpredict(Span<byte> data, int width, int height, int depth)
    {
        switch (depth)
        {
            case 8:
                for (int y = 0; y < height; y++)
                {
                    var row = data.Slice(y * width, width);
                    for (int x = 1; x < width; x++) row[x] += row[x - 1];
                }
                break;

            case 16:
                for (int y = 0; y < height; y++)
                {
                    var row = data.Slice(y * width * 2, width * 2);
                    ushort prev = BinaryPrimitives.ReadUInt16BigEndian(row);
                    for (int x = 1; x < width; x++)
                    {
                        prev = (ushort)(prev + BinaryPrimitives.ReadUInt16BigEndian(row[(x * 2)..]));
                        BinaryPrimitives.WriteUInt16BigEndian(row[(x * 2)..], prev);
                    }
                }
                break;

            case 32:
                // Each row is delta-encoded bytewise, then stored as four planes (all byte 0s, all byte 1s, ...).
                var tmp = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    var row = data.Slice(y * width * 4, width * 4);
                    for (int i = 1; i < row.Length; i++) row[i] += row[i - 1];
                    row.CopyTo(tmp);
                    for (int x = 0; x < width; x++)
                    {
                        row[x * 4] = tmp[x];
                        row[x * 4 + 1] = tmp[width + x];
                        row[x * 4 + 2] = tmp[2 * width + x];
                        row[x * 4 + 3] = tmp[3 * width + x];
                    }
                }
                break;

            default:
                throw new PsdFormatException($"ZIP prediction is not defined for {depth}-bit data");
        }
    }

    /// <summary>Converts big-endian file samples to a host-order plane, expanding 1-bit data to 8-bit.</summary>
    public static Plane ToPlane(byte[] raw, int width, int height, int depth)
    {
        switch (depth)
        {
            case 1:
                // In bitmap mode a set bit is black.
                var expanded = new byte[(long)width * height];
                int rowBytes = RowBytes(width, 1);
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        bool black = (raw[y * rowBytes + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                        expanded[(long)y * width + x] = black ? (byte)0 : (byte)255;
                    }
                return new Plane(width, height, 8, expanded);

            case 8:
                return new Plane(width, height, 8, raw);

            case 16:
                if (BitConverter.IsLittleEndian)
                    for (int i = 0; i < raw.Length; i += 2) (raw[i], raw[i + 1]) = (raw[i + 1], raw[i]);
                return new Plane(width, height, 16, raw);

            case 32:
                if (BitConverter.IsLittleEndian)
                    for (int i = 0; i < raw.Length; i += 4)
                    {
                        (raw[i], raw[i + 3]) = (raw[i + 3], raw[i]);
                        (raw[i + 1], raw[i + 2]) = (raw[i + 2], raw[i + 1]);
                    }
                return new Plane(width, height, 32, raw);

            default:
                throw new PsdFormatException($"Unsupported bit depth {depth}");
        }
    }
}
