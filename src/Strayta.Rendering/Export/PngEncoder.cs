using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Strayta.Rendering.Export;

/// <summary>
/// 8-bit PNG encoder (RGBA, or RGB when every pixel is opaque). Each row gets the filter that makes it
/// smallest by the usual sum-of-absolute-differences heuristic, which compresses photos about as well as
/// common image libraries.
/// </summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="rgba">Straight-alpha RGBA, row-major.</param>
    /// <param name="iccProfile">Embedded as the image's color profile; when null the image is marked sRGB.</param>
    /// <param name="text">tEXt chunks (keyword, Latin-1 text), such as "Copyright" and "Author".</param>
    public static void Encode(Stream output, byte[] rgba, int width, int height, bool keepAlpha = true, byte[]? iccProfile = null,
        IReadOnlyList<(string Keyword, string Text)>? text = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgba.LongLength != (long)width * height * 4) throw new ArgumentException("Pixel buffer does not match the size.", nameof(rgba));

        bool alpha = keepAlpha && HasTransparency(rgba);
        int bpp = alpha ? 4 : 3;
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bits per sample
        ihdr[9] = (byte)(alpha ? 6 : 2); // color type: RGBA or RGB
        WriteChunk(output, "IHDR", ihdr);

        if (iccProfile is not null)
        {
            using var z = new MemoryStream();
            z.Write("ICC Profile\0\0"u8); // name, terminator, compression method 0 (zlib)
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(iccProfile);
            WriteChunk(output, "iCCP", z.ToArray());
        }
        else
        {
            WriteChunk(output, "sRGB", [0]); // perceptual rendering intent
        }
        WriteText(output, text);

        // Filter rows in parallel (the expensive part), then compress them in order.
        int stride = width * bpp;
        var filtered = new byte[(long)height * (stride + 1)];
        Parallel.For(0, height, () => (new byte[stride], new byte[stride], new byte[stride]), (y, _, buffers) =>
        {
            var (row, prev, trial) = buffers;
            Pack(rgba, width, y, alpha, row);
            if (y > 0) Pack(rgba, width, y - 1, alpha, prev);
            else Array.Clear(prev);
            FilterRow(row, prev, bpp, filtered.AsSpan((int)((long)y * (stride + 1)), stride + 1), trial);
            return buffers;
        }, _ => { });

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(filtered);
        // Split the image data into chunks of at most 1 MiB, as encoders customarily do.
        var data = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);
        for (int o = 0; o < data.Length || o == 0; o += 1 << 20)
            WriteChunk(output, "IDAT", data.Slice(o, Math.Min(1 << 20, data.Length - o)));
        WriteChunk(output, "IEND", []);
    }

    /// <summary>
    /// Writes an 8-bit palette PNG (color type 3; "smaller file"): the palette, a tRNS chunk with the entries' alpha
    /// when any is below 255, and one index per pixel.
    /// </summary>
    public static void EncodeIndexed(Stream output, IndexedImage image, byte[]? iccProfile = null, IReadOnlyList<(string Keyword, string Text)>? text = null)
    {
        int width = image.Width, height = image.Height;
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 3; // palette
        WriteChunk(output, "IHDR", ihdr);
        if (iccProfile is not null)
        {
            using var z = new MemoryStream();
            z.Write("ICC Profile\0\0"u8);
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(iccProfile);
            WriteChunk(output, "iCCP", z.ToArray());
        }
        else WriteChunk(output, "sRGB", [0]);
        WriteText(output, text);

        int n = Math.Max(1, image.ColorCount);
        var plte = new byte[n * 3];
        var trns = new byte[n];
        int lastTransparent = -1;
        for (int k = 0; k < image.ColorCount; k++)
        {
            plte[k * 3] = image.Palette[k * 4];
            plte[k * 3 + 1] = image.Palette[k * 4 + 1];
            plte[k * 3 + 2] = image.Palette[k * 4 + 2];
            trns[k] = image.Palette[k * 4 + 3];
            if (trns[k] != 255) lastTransparent = k;
        }
        WriteChunk(output, "PLTE", plte);
        if (lastTransparent >= 0) WriteChunk(output, "tRNS", trns.AsSpan(0, lastTransparent + 1));

        // Indexed rows are filtered with None, which suits palette images best.
        var raw = new byte[(long)height * (width + 1)];
        for (int y = 0; y < height; y++)
            Array.Copy(image.Indices, (long)y * width, raw, (long)y * (width + 1) + 1, width);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);
        var data = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);
        for (int o = 0; o < data.Length || o == 0; o += 1 << 20)
            WriteChunk(output, "IDAT", data.Slice(o, Math.Min(1 << 20, data.Length - o)));
        WriteChunk(output, "IEND", []);
    }

    private static void WriteText(Stream output, IReadOnlyList<(string Keyword, string Text)>? text)
    {
        foreach (var (keyword, value) in text ?? [])
        {
            if (string.IsNullOrEmpty(keyword) || keyword.Length > 79) continue;
            var latin1 = Encoding.Latin1;
            WriteChunk(output, "tEXt", [.. latin1.GetBytes(keyword), 0, .. latin1.GetBytes(value)]);
        }
    }

    private static bool HasTransparency(byte[] rgba)
    {
        for (long i = 3; i < rgba.LongLength; i += 4)
            if (rgba[i] != 255) return true;
        return false;
    }

    private static void Pack(byte[] rgba, int width, int y, bool alpha, byte[] row)
    {
        long src = (long)y * width * 4;
        if (alpha)
        {
            Array.Copy(rgba, src, row, 0, width * 4);
            return;
        }
        for (int x = 0; x < width; x++)
        {
            row[x * 3] = rgba[src + x * 4];
            row[x * 3 + 1] = rgba[src + x * 4 + 1];
            row[x * 3 + 2] = rgba[src + x * 4 + 2];
        }
    }

    /// <summary>Writes the filter type byte and the filtered row, choosing the filter with the smallest residuals.</summary>
    private static void FilterRow(byte[] row, byte[] prev, int bpp, Span<byte> dest, byte[] trial)
    {
        long best = long.MaxValue;
        for (byte type = 0; type <= 4; type++)
        {
            long cost = 0;
            for (int i = 0; i < row.Length; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int predictor = type switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    _ => Paeth(a, b, c),
                };
                byte v = (byte)(row[i] - predictor);
                trial[i] = v;
                cost += (sbyte)v < 0 ? -(sbyte)v : v; // residuals near 0 or 256 are both cheap
            }
            if (cost < best)
            {
                best = cost;
                dest[0] = type;
                trial.CopyTo(dest[1..]);
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        s.Write(word);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        s.Write(word);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
