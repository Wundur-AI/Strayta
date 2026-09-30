using System.Text;

namespace Strayta.Rendering.Export;

/// <summary>
/// Writes single-frame GIF89a images, following the GIF89a specification: a global color table from
/// <see cref="ColorQuantizer"/> (median cut), a Graphic Control Extension naming the transparent index when the image
/// has transparency, an optional comment, and the pixels LZW-compressed in 255-byte sub-blocks.
/// </summary>
public static class GifEncoder
{
    /// <param name="rgba">Straight-alpha RGBA. Pixels below half opacity become transparent (when
    /// <paramref name="transparency"/> is on); partly transparent ones should already be flattened onto a matte.</param>
    public static void Encode(Stream output, byte[] rgba, int width, int height, bool transparency = true, string? comment = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width > 65535 || height > 65535) throw new ArgumentException("GIF images are limited to 65535 pixels per side.");
        if (!transparency)
        {
            rgba = (byte[])rgba.Clone();
            for (long i = 3; i < rgba.LongLength; i += 4) rgba[i] = 255;
        }
        Encode(output, ColorQuantizer.Quantize(rgba, width, height, 256, keepAlpha: false), comment);
    }

    /// <summary>Writes an already indexed image; entries with alpha below 128 mark the transparent index.</summary>
    public static void Encode(Stream output, IndexedImage image, string? comment = null)
    {
        int colors = Math.Max(2, image.ColorCount);
        int bits = 1;
        while ((1 << bits) < colors) bits++;
        int transparent = -1;
        for (int k = 0; k < image.ColorCount; k++)
            if (image.Palette[k * 4 + 3] < 128) { transparent = k; break; }

        var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        w.Write("GIF89a"u8);
        // Logical screen: size, global color table present with 2^bits entries, 8 bits of color resolution.
        w.Write((ushort)image.Width);
        w.Write((ushort)image.Height);
        w.Write((byte)(0x80 | 0x70 | (bits - 1)));
        w.Write((byte)0); // background color index
        w.Write((byte)0); // pixel aspect ratio: unspecified
        for (int k = 0; k < 1 << bits; k++)
        {
            if (k < image.ColorCount) w.Write(image.Palette.AsSpan(k * 4, 3));
            else w.Write([0, 0, 0]);
        }

        if (transparent >= 0)
        {
            // Graphic Control Extension: no disposal, transparent color flag and index.
            w.Write([0x21, 0xF9, 4, 0x01, 0, 0, (byte)transparent, 0]);
        }
        if (!string.IsNullOrEmpty(comment))
        {
            w.Write([0x21, 0xFE]);
            SubBlocks(w, Encoding.ASCII.GetBytes(comment));
        }

        // Image descriptor: the whole screen, no local table, not interlaced.
        w.Write((byte)0x2C);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)image.Width);
        w.Write((ushort)image.Height);
        w.Write((byte)0);

        int minCodeSize = Math.Max(2, bits);
        w.Write((byte)minCodeSize);
        SubBlocks(w, Lzw(image.Indices, minCodeSize));
        w.Write((byte)0x3B); // trailer
        w.Flush();
    }

    private static void SubBlocks(BinaryWriter w, byte[] data)
    {
        for (int o = 0; o < data.Length; o += 255)
        {
            int n = Math.Min(255, data.Length - o);
            w.Write((byte)n);
            w.Write(data, o, n);
        }
        w.Write((byte)0);
    }

    /// <summary>Variable-length-code LZW as GIF uses it: codes packed least significant bit first, up to 12 bits.</summary>
    internal static byte[] Lzw(byte[] indices, int minCodeSize)
    {
        int clear = 1 << minCodeSize, end = clear + 1;
        var output = new MemoryStream();
        int bitBuffer = 0, bitCount = 0;
        int codeSize = minCodeSize + 1, next = end + 1;
        var table = new Dictionary<int, int>(4096);

        void Emit(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                output.WriteByte((byte)bitBuffer);
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        Emit(clear);
        if (indices.Length > 0)
        {
            int prefix = indices[0];
            for (long i = 1; i < indices.LongLength; i++)
            {
                int k = indices[i];
                int key = (prefix << 8) | k;
                if (table.TryGetValue(key, out int code))
                {
                    prefix = code;
                    continue;
                }
                Emit(prefix);
                if (next < 4096)
                {
                    table[key] = next++;
                    // The decoder widens its codes once the next code no longer fits.
                    if (next > (1 << codeSize) && codeSize < 12) codeSize++;
                }
                else
                {
                    Emit(clear);
                    table.Clear();
                    codeSize = minCodeSize + 1;
                    next = end + 1;
                }
                prefix = k;
            }
            Emit(prefix);
        }
        Emit(end);
        if (bitCount > 0) output.WriteByte((byte)bitBuffer);
        return output.ToArray();
    }
}
