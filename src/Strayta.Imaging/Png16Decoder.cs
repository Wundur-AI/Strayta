using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Strayta.Core;

namespace Strayta.Imaging;

/// <summary>
/// Decodes 16-bit PNGs at full precision. SkiaSharp only offers 8-bit or half-float output for them, and
/// half floats keep about 11 bits, so 16-bit files would lose precision on open. Handles gray, gray+alpha,
/// RGB and RGBA without interlacing (by far the common case); returns null otherwise so the caller can
/// fall back to an 8-bit decode.
/// </summary>
internal static class Png16Decoder
{
    public static (Plane[] Planes, int Width, int Height, bool Gray, bool Alpha)? TryDecode(byte[] d)
    {
        int width = 0, height = 0, colorType = -1, interlace = 0;
        var idat = new MemoryStream();
        int p = 8;
        while (p + 8 <= d.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p));
            string type = Encoding.ASCII.GetString(d, p + 4, 4);
            var data = d.AsSpan(p + 8, len);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    if (data[8] != 16) return null;
                    colorType = data[9];
                    interlace = data[12];
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
            }
            if (type == "IEND") break;
            p += 12 + len;
        }
        if (interlace != 0 || colorType is not (0 or 2 or 4 or 6)) return null;

        int channels = colorType switch { 0 => 1, 4 => 2, 2 => 3, _ => 4 };
        int bpp = channels * 2, stride = width * bpp;
        var raw = new byte[(long)(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);

        // Undo the per-row filters (PNG spec section 9) into image rows.
        var img = new byte[(long)stride * height];
        for (int y = 0; y < height; y++)
        {
            byte filter = raw[(long)y * (stride + 1)];
            var src = raw.AsSpan((int)((long)y * (stride + 1) + 1), stride);
            var cur = img.AsSpan(y * stride, stride);
            var prev = y > 0 ? img.AsSpan((y - 1) * stride, stride) : Span<byte>.Empty;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0;
                int b = prev.IsEmpty ? 0 : prev[i];
                int c = i >= bpp && !prev.IsEmpty ? prev[i - bpp] : 0;
                int predicted = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Unknown PNG filter {filter}."),
                };
                cur[i] = (byte)(src[i] + predicted);
            }
        }

        // Split into host-order 16-bit planes: gray[+alpha] or RGB[+alpha].
        var planes = Enumerable.Range(0, channels).Select(_ => Plane.Create(width, height, 16)).ToArray();
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
                for (int c = 0; c < channels; c++)
                    planes[c].AsUInt16()[y * width + x] = BinaryPrimitives.ReadUInt16BigEndian(img.AsSpan(y * stride + (x * channels + c) * 2));
        });
        bool gray = channels <= 2, alpha = channels is 2 or 4;
        return (planes, width, height, gray, alpha);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
