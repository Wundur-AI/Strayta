using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Strayta.Core;

namespace Strayta.Imaging;

/// <summary>
/// Writes a raster as PNG at its own bit depth (8 or 16), gray or RGB, with or without alpha, plus its ICC
/// profile. Saving an opened PNG back through this keeps every value exact, which rendering to 8-bit RGBA
/// for export would not for 16-bit or grayscale images.
/// </summary>
public static class PngWriter
{
    public static void Write(Stream output, Raster raster, byte[]? iccProfile = null)
    {
        if (raster.BitDepth is not (8 or 16)) throw new NotSupportedException("PNG stores 8- or 16-bit images.");
        bool gray = raster.ColorMode == ColorMode.Grayscale;
        if (!gray && raster.ColorMode != ColorMode.Rgb) throw new NotSupportedException($"{raster.ColorMode} images cannot be saved as PNG.");

        var channels = raster.ColorPlanes.Take(gray ? 1 : 3).Append(raster.Alpha).OfType<Plane>().ToArray();
        int w = raster.Width, h = raster.Height, size = raster.BitDepth / 8, bpp = channels.Length * size, stride = w * bpp;
        byte colorType = (gray, raster.Alpha is not null) switch { (true, false) => 0, (true, true) => 4, (false, false) => 2, _ => 6 };

        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)raster.BitDepth;
        ihdr[9] = colorType;
        Chunk(output, "IHDR", ihdr);

        if (iccProfile is { Length: > 0 })
        {
            var icc = new MemoryStream();
            icc.Write("ICC Profile\0\0"u8); // name, terminator, compression method 0
            using (var z = new ZLibStream(icc, CompressionLevel.Optimal, leaveOpen: true)) z.Write(iccProfile);
            Chunk(output, "iCCP", icc.ToArray());
        }

        // Interleave big-endian samples and apply the Paeth filter to every row (good for photos).
        var filtered = new byte[(long)(stride + 1) * h];
        Parallel.For(0, h, y =>
        {
            var row = new byte[stride];
            var prev = new byte[stride];
            Interleave(channels, y, w, size, row);
            if (y > 0) Interleave(channels, y - 1, w, size, prev);
            long o = (long)y * (stride + 1);
            filtered[o] = 4;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                filtered[o + 1 + i] = (byte)(row[i] - Paeth(a, b, c));
            }
        });

        var data = new MemoryStream();
        using (var z = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true)) z.Write(filtered);
        Chunk(output, "IDAT", data.ToArray());
        Chunk(output, "IEND", []);
    }

    private static void Interleave(Plane[] channels, int y, int w, int size, byte[] row)
    {
        for (int x = 0; x < w; x++)
            for (int c = 0; c < channels.Length; c++)
            {
                int o = (x * channels.Length + c) * size, i = y * w + x;
                if (size == 1) row[o] = channels[c].Data[i];
                else BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(o), channels[c].AsUInt16()[i]);
            }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream o, string type, byte[] data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        o.Write(buf);
        var head = Encoding.ASCII.GetBytes(type);
        o.Write(head);
        o.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, head), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        o.Write(buf);
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
