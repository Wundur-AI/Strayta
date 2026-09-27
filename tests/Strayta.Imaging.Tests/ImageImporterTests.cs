using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Strayta.Core;
using Strayta.Rendering.Export;

namespace Strayta.Imaging.Tests;

public class ImageImporterTests
{
    /// <summary>A 4×2 test image: left half red, right half blue; alpha 128 in the bottom-right pixel.</summary>
    private static byte[] Rgba(bool transparent)
    {
        var px = new byte[4 * 2 * 4];
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
            {
                int i = (y * 4 + x) * 4;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = x < 2 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)255);
            }
        if (transparent) px[(1 * 4 + 3) * 4 + 3] = 128;
        return px;
    }

    private static byte[] Png(byte[] rgba, int w, int h, bool alpha = true, byte[]? icc = null)
    {
        var ms = new MemoryStream();
        PngEncoder.Encode(ms, rgba, w, h, alpha, icc);
        return ms.ToArray();
    }

    [Fact]
    public void Opaque_png_opens_as_a_background_layer()
    {
        var doc = ImageImporter.Decode(Png(Rgba(false), 4, 2, alpha: false));

        Assert.Equal((4, 2, ColorMode.Rgb, 8), (doc.Width, doc.Height, doc.ColorMode, doc.BitDepth));
        var layer = Assert.IsType<PixelLayer>(Assert.Single(doc.Root.Children));
        Assert.Equal("Background", layer.Name);
        Assert.Null(layer.Pixels!.Alpha);
        Assert.Equal(255, layer.Pixels.ColorPlanes[0].Data[0]);
        Assert.Equal(255, layer.Pixels.ColorPlanes[2].Data[3]);
        Assert.NotNull(doc.Composite);
    }

    [Fact]
    public void Transparent_png_keeps_alpha_in_layer_0()
    {
        var doc = ImageImporter.Decode(Png(Rgba(true), 4, 2));
        var layer = (PixelLayer)doc.Root.Children[0];
        Assert.Equal("Layer 0", layer.Name);
        Assert.Equal(128, layer.Pixels!.Alpha!.Data[7]);
        Assert.Equal(255, layer.Pixels.ColorPlanes[2].Data[7]); // straight (not premultiplied) color
    }

    [Fact]
    public void Jpeg_opens_close_to_the_source()
    {
        var ms = new MemoryStream();
        JpegEncoder.Encode(ms, Rgba(false), 4, 2, quality: 100);
        var doc = ImageImporter.Decode(ms.ToArray());
        var red = ((PixelLayer)doc.Root.Children[0]).Pixels!.ColorPlanes[0].Data;
        Assert.InRange(red[0], 230, 255);
        Assert.InRange(red[3], 0, 40);
    }

    [Fact]
    public void Sixteen_bit_png_keeps_all_16_bits()
    {
        var doc = ImageImporter.Decode(Png16(0x1234, 0xABCD, 0x0F0F));
        Assert.Equal(16, doc.BitDepth);
        var planes = ((PixelLayer)doc.Root.Children[0]).Pixels!.ColorPlanes;
        Assert.Equal(0x1234, planes[0].AsUInt16()[0]);
        Assert.Equal(0xABCD, planes[1].AsUInt16()[0]);
        Assert.Equal(0x0F0F, planes[2].AsUInt16()[0]);
    }

    [Fact]
    public void Grayscale_png_opens_as_grayscale()
    {
        var doc = ImageImporter.Decode(PngGray([10, 200]));
        Assert.Equal(ColorMode.Grayscale, doc.ColorMode);
        Assert.Equal([10, 200], ((PixelLayer)doc.Root.Children[0]).Pixels!.ColorPlanes[0].Data);
    }

    [Fact]
    public void Embedded_profiles_are_carried_along_for_png_and_jpeg()
    {
        var icc = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
        Assert.Equal(icc, ImageImporter.ReadIccProfile(Png(Rgba(false), 4, 2, icc: icc)));

        var ms = new MemoryStream();
        JpegEncoder.Encode(ms, Rgba(false), 4, 2, iccProfile: icc);
        Assert.Equal(icc, ImageImporter.ReadIccProfile(ms.ToArray()));
    }

    [Theory]
    [InlineData(6, 2, 4)] // rotated 90° clockwise: 4×2 stored, 2×4 shown
    [InlineData(3, 4, 2)] // rotated 180°
    public void Camera_orientation_is_applied(int orientation, int width, int height)
    {
        var ms = new MemoryStream();
        JpegEncoder.Encode(ms, Rgba(false), 4, 2, quality: 100);
        var doc = ImageImporter.Decode(WithExifOrientation(ms.ToArray(), orientation));

        Assert.Equal((width, height), (doc.Width, doc.Height));
        var red = ((PixelLayer)doc.Root.Children[0]).Pixels!.ColorPlanes[0].Data;
        if (orientation == 6)
        {
            // Rotating clockwise puts the stored left (red) half on top.
            Assert.InRange(red[0], 200, 255);
            Assert.InRange(red[(height - 1) * width], 0, 60);
        }
        else
        {
            // 180°: red ends up on the right.
            Assert.InRange(red[0], 0, 60);
            Assert.InRange(red[width - 1], 200, 255);
        }
    }

    [Fact]
    public void Unreadable_files_fail_with_a_clear_error() =>
        Assert.Throws<InvalidDataException>(() => ImageImporter.Decode(Encoding.ASCII.GetBytes("not an image")));

    // ---- Test image builders --------------------------------------------------------------------

    private static byte[] Png16(ushort r, ushort g, ushort b) => PngRaw(1, 1, bitDepth: 16, colorType: 2, row =>
    {
        var s = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(s, r);
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(2), g);
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(4), b);
        return s;
    });

    private static byte[] PngGray(byte[] values) => PngRaw(values.Length, 1, bitDepth: 8, colorType: 0, _ => values);

    private static byte[] PngRaw(int w, int h, byte bitDepth, byte colorType, Func<int, byte[]> row)
    {
        var o = new MemoryStream();
        o.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        Chunk(o, "IHDR", ihdr);
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(row(y)); }
        Chunk(o, "IDAT", raw.ToArray());
        Chunk(o, "IEND", []);
        return o.ToArray();
    }

    private static void Chunk(Stream o, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        o.Write(len);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        o.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(body));
        o.Write(len);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>Inserts a minimal EXIF APP1 segment with an Orientation tag right after the JPEG's SOI marker.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        var tiff = new byte[26];
        tiff[0] = (byte)'M'; tiff[1] = (byte)'M';              // big-endian TIFF header
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8); // first IFD
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1); // one entry
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x0112); // Orientation
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3);      // SHORT
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);      // count
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), (ushort)orientation);
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF; segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return jpeg[..2].Concat(segment).Concat(jpeg[2..]).ToArray();
    }
}

public class Png16DecoderTests
{
    [Fact]
    public void Every_png_filter_is_reversed()
    {
        // A 3×3 16-bit RGBA image whose rows use filters 1 (Sub), 2 (Up) and 4 (Paeth); row 0 uses 3 (Average).
        var values = Enumerable.Range(0, 3 * 3 * 4).Select(i => (ushort)(i * 1733 % 65536)).ToArray();
        int stride = 3 * 8;
        var img = new byte[3 * stride];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(i * 2), values[i]);
        byte[] filters = [3, 1, 4];
        var raw = new MemoryStream();
        for (int y = 0; y < 3; y++)
        {
            raw.WriteByte(filters[y]);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= 8 ? img[y * stride + i - 8] : 0;
                int b = y > 0 ? img[(y - 1) * stride + i] : 0;
                int c = i >= 8 && y > 0 ? img[(y - 1) * stride + i - 8] : 0;
                int pred = filters[y] switch { 1 => a, 3 => (a + b) >> 1, _ => Paeth(a, b, c) };
                raw.WriteByte((byte)(img[y * stride + i] - pred));
            }
        }

        var png = BuildPng(3, 3, colorType: 6, raw.ToArray());
        var doc = ImageImporter.Decode(png);

        var px = (PixelLayer)doc.Root.Children[0];
        Assert.Equal(16, doc.BitDepth);
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(values[i * 4], px.Pixels!.ColorPlanes[0].AsUInt16()[i]);
            Assert.Equal(values[i * 4 + 3], px.Pixels.Alpha!.AsUInt16()[i]);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] BuildPng(int w, int h, byte colorType, byte[] filteredRows)
    {
        var o = new MemoryStream();
        o.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 16;
        ihdr[9] = colorType;
        Chunk(o, "IHDR", ihdr);
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(filteredRows);
        Chunk(o, "IDAT", z.ToArray());
        Chunk(o, "IEND", []);
        return o.ToArray();
    }

    private static void Chunk(Stream o, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        o.Write(len);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        o.Write(body);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in body) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1; }
        BinaryPrimitives.WriteUInt32BigEndian(len, crc ^ 0xFFFFFFFF);
        o.Write(len);
    }
}

public class PngWriterTests
{
    [Theory]
    [InlineData(8, false, false)]
    [InlineData(8, true, true)]
    [InlineData(16, false, true)]
    [InlineData(16, true, false)]
    public void Written_pngs_reopen_with_identical_samples(int depth, bool gray, bool alpha)
    {
        int w = 7, h = 5;
        Plane P(int seed)
        {
            var p = Plane.Create(w, h, depth);
            for (int i = 0; i < w * h; i++)
                if (depth == 8) p.Data[i] = (byte)(i * 37 + seed);
                else p.AsUInt16()[i] = (ushort)(i * 4099 + seed * 311);
            return p;
        }
        var color = gray ? new[] { P(1) } : new[] { P(1), P(2), P(3) };
        var raster = new Raster(gray ? ColorMode.Grayscale : ColorMode.Rgb, color, alpha ? P(4) : null);
        var icc = new byte[] { 1, 2, 3, 4, 5 };

        var ms = new MemoryStream();
        PngWriter.Write(ms, raster, icc);
        var doc = ImageImporter.Decode(ms.ToArray());

        var back = ((PixelLayer)doc.Root.Children[0]).Pixels!;
        Assert.Equal((depth, gray ? ColorMode.Grayscale : ColorMode.Rgb), (doc.BitDepth, doc.ColorMode));
        for (int c = 0; c < color.Length; c++) Assert.Equal(color[c].Data, back.ColorPlanes[c].Data);
        if (alpha) Assert.Equal(raster.Alpha!.Data, back.Alpha!.Data);
        else Assert.Null(back.Alpha);
        Assert.Equal(icc, doc.IccProfile);
    }
}
