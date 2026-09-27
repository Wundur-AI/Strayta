using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using SkiaSharp;
using Strayta.Core;

namespace Strayta.Imaging;

/// <summary>What a document was opened from, so Save can write it back the same way when that is safe.</summary>
public sealed record ImageSource(string Path, SKEncodedImageFormat Format)
{
    /// <summary>True for formats Strayta can also write (PNG and JPEG).</summary>
    public bool IsWritable => Format is SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg;
}

/// <summary>
/// Opens standard images (PNG, JPEG, WebP, GIF, BMP, ICO; HEIC on macOS) as a one-layer document, the way
/// Photoshop does: opaque images get a "Background" layer, images with transparency a "Layer 0".
/// Pixels keep their own color space (the embedded profile is carried along, not converted), camera
/// orientation is applied, 16-bit PNGs stay 16-bit and grayscale images stay grayscale.
/// </summary>
public static class ImageImporter
{
    private static readonly HashSet<string> Extensions =
        [".png", ".jpg", ".jpeg", ".jpe", ".webp", ".gif", ".bmp", ".ico", ".heic", ".heif"];

    public static bool CanOpen(string path) => Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    public static Document Open(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".heic" or ".heif") return OpenViaSystemConverter(path);

        var bytes = File.ReadAllBytes(path);
        using var codec = SKCodec.Create(new MemoryStream(bytes))
            ?? throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not an image Strayta can read.");
        var doc = Decode(codec, bytes);
        doc.SourceData = new ImageSource(path, codec.EncodedFormat);
        return doc;
    }

    /// <summary>Decodes an encoded image held in memory.</summary>
    public static Document Decode(byte[] encoded)
    {
        using var codec = SKCodec.Create(new MemoryStream(encoded)) ?? throw new InvalidDataException("Not a readable image.");
        return Decode(codec, encoded);
    }

    private static Document Decode(SKCodec codec, byte[] encoded)
    {
        var info = codec.Info;
        int w = info.Width, h = info.Height, depth = 8;
        bool gray, hasAlpha;
        Plane[] planes;

        if (IsSixteenBitPng(encoded) && Png16Decoder.TryDecode(encoded) is { } png16)
        {
            (planes, w, h, gray, hasAlpha) = png16;
            depth = 16;
        }
        else
        {
            // Skia reports gray+alpha PNGs as color, so the PNG header decides grayscale when there is one.
            bool grayAlpha = PngColorType(encoded) == 4;
            gray = info.ColorType == SKColorType.Gray8 || grayAlpha;
            hasAlpha = info.AlphaType != SKAlphaType.Opaque && (grayAlpha || info.ColorType != SKColorType.Gray8);
            // Decode without color conversion: keep the file's own color space and carry its profile along.
            bool decodeGray = gray && !hasAlpha;
            var target = new SKImageInfo(w, h, decodeGray ? SKColorType.Gray8 : SKColorType.Rgba8888,
                hasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque, info.ColorSpace);
            var pixels = new byte[target.BytesSize64];
            var result = codec.GetPixels(target, pixels);
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException($"The image could not be decoded ({result}).");
            planes = ToPlanes(pixels, w, h, decodeGray ? 1 : 4, 8);
            // Gray with alpha was decoded as RGBA: keep one gray channel plus alpha.
            if (gray && hasAlpha) planes = [planes[0], planes[3]];
        }
        (planes, w, h) = Orient(planes, w, h, codec.EncodedOrigin);

        var mode = gray ? ColorMode.Grayscale : ColorMode.Rgb;
        var color = gray ? planes[..1] : planes[..3];
        var alpha = hasAlpha ? planes[^1] : null;
        var raster = new Raster(mode, color, alpha);

        var doc = new Document(w, h, mode, depth) { IccProfile = ReadIccProfile(encoded), Composite = raster };
        doc.Root.Add(new PixelLayer
        {
            Name = alpha is null ? "Background" : "Layer 0",
            Bounds = doc.Bounds,
            Pixels = raster,
            TransparencyLocked = false,
        });
        return doc;
    }

    /// <summary>Splits interleaved 8-bit samples into planes.</summary>
    private static Plane[] ToPlanes(byte[] pixels, int w, int h, int channels, int depth)
    {
        int size = depth / 8;
        var planes = Enumerable.Range(0, channels).Select(_ => Plane.Create(w, h, depth)).ToArray();
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                for (int c = 0; c < channels; c++)
                {
                    int src = (i * channels + c) * size;
                    if (size == 1) planes[c].Data[i] = pixels[src];
                    else planes[c].AsUInt16()[i] = BitConverter.ToUInt16(pixels, src);
                }
            }
        });
        return planes;
    }

    /// <summary>Applies the EXIF orientation so photos open upright, as every image viewer shows them.</summary>
    private static (Plane[], int, int) Orient(Plane[] planes, int w, int h, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return (planes, w, h);
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int ow = swap ? h : w, oh = swap ? w : h;

        // For each output pixel, where it comes from in the stored image.
        (int, int) Source(int x, int y) => origin switch
        {
            SKEncodedOrigin.TopRight => (w - 1 - x, y),
            SKEncodedOrigin.BottomRight => (w - 1 - x, h - 1 - y),
            SKEncodedOrigin.BottomLeft => (x, h - 1 - y),
            SKEncodedOrigin.LeftTop => (y, x),
            SKEncodedOrigin.RightTop => (y, h - 1 - x),
            SKEncodedOrigin.RightBottom => (w - 1 - y, h - 1 - x),
            SKEncodedOrigin.LeftBottom => (w - 1 - y, x),
            _ => (x, y),
        };

        var result = planes.Select(p =>
        {
            var o = Plane.Create(ow, oh, p.BitDepth);
            int size = p.BitDepth / 8;
            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++)
                {
                    var (sx, sy) = Source(x, y);
                    Buffer.BlockCopy(p.Data, (sy * w + sx) * size, o.Data, (y * ow + x) * size, size);
                }
            return o;
        }).ToArray();
        return (result, ow, oh);
    }

    /// <summary>PNG header color type (byte 25), or -1 for other formats.</summary>
    private static int PngColorType(byte[] d) =>
        d.Length > 26 && d[0] == 0x89 && d[1] == (byte)'P' ? d[25] : -1;

    /// <summary>PNG header: bit depth is byte 24 (after the 8-byte signature and the IHDR chunk header).</summary>
    private static bool IsSixteenBitPng(byte[] d) =>
        d.Length > 25 && d[0] == 0x89 && d[1] == (byte)'P' && d[24] == 16;

    /// <summary>
    /// The embedded ICC profile, read from the file itself (SkiaSharp does not expose the original bytes):
    /// PNG iCCP chunk (zlib-compressed) or JPEG APP2 "ICC_PROFILE" segments (possibly split across several).
    /// </summary>
    public static byte[]? ReadIccProfile(byte[] d)
    {
        try
        {
            if (d.Length > 8 && d[0] == 0x89 && d[1] == (byte)'P') return PngIcc(d);
            if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8) return JpegIcc(d);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException)
        {
            // A damaged profile is not worth failing the open over; the image opens as sRGB.
        }
        return null;
    }

    private static byte[]? PngIcc(byte[] d)
    {
        int p = 8;
        while (p + 8 <= d.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p));
            string type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
            if (type == "iCCP")
            {
                int nameEnd = Array.IndexOf(d, (byte)0, p + 8, len);
                using var z = new ZLibStream(new MemoryStream(d, nameEnd + 2, len - (nameEnd + 2 - (p + 8))), CompressionMode.Decompress);
                using var o = new MemoryStream();
                z.CopyTo(o);
                return o.ToArray();
            }
            if (type is "IDAT" or "IEND") break;
            p += 12 + len;
        }
        return null;
    }

    private static byte[]? JpegIcc(byte[] d)
    {
        var chunks = new SortedDictionary<int, byte[]>();
        int p = 2;
        while (p + 4 <= d.Length && d[p] == 0xFF)
        {
            byte marker = d[p + 1];
            if (marker is 0xDA or 0xD9) break; // start of scan / end of image
            int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 2));
            if (marker == 0xE2 && len > 16 && System.Text.Encoding.ASCII.GetString(d, p + 4, 11) == "ICC_PROFILE")
            {
                int seq = d[p + 4 + 12];
                chunks[seq] = d.AsSpan(p + 4 + 14, len - 2 - 14).ToArray();
            }
            p += 2 + len;
        }
        return chunks.Count == 0 ? null : chunks.Values.SelectMany(c => c).ToArray();
    }

    /// <summary>
    /// HEIC (iPhone photos): SkiaSharp cannot decode it, so on macOS the system's own converter (sips) turns it
    /// into a PNG first, keeping its color profile and orientation.
    /// </summary>
    private static Document OpenViaSystemConverter(string path)
    {
        if (!OperatingSystem.IsMacOS())
            throw new NotSupportedException("HEIC images can only be opened on macOS for now.");
        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"strayta-{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/sips", ["-s", "format", "png", path, "--out", temp])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(60_000);
            if (proc.ExitCode != 0 || !File.Exists(temp))
                throw new InvalidDataException($"macOS could not convert {System.IO.Path.GetFileName(path)}.");
            var bytes = File.ReadAllBytes(temp);
            using var codec = SKCodec.Create(new MemoryStream(bytes)) ?? throw new InvalidDataException("Converted image is unreadable.");
            var doc = Decode(codec, bytes);
            doc.SourceData = new ImageSource(path, SKEncodedImageFormat.Heif);
            return doc;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
