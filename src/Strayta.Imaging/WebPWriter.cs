using System.Runtime.InteropServices;
using SkiaSharp;

namespace Strayta.Imaging;

/// <summary>WebP encoding and ICC-to-sRGB conversion for exports, through SkiaSharp (libwebp and skcms).</summary>
public static class WebPWriter
{
    /// <param name="rgba">Straight-alpha RGBA8, row-major.</param>
    /// <param name="quality">1–100; for lossless, how hard to compress.</param>
    /// <param name="iccProfile">The profile the pixels are in (embedded), or null for sRGB.</param>
    public static void Encode(Stream output, byte[] rgba, int width, int height, int quality, bool lossless, byte[]? iccProfile = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width > 16383 || height > 16383) throw new ArgumentException("WebP images are limited to 16383 pixels per side.");
        var space = iccProfile is { Length: > 0 } ? SKColorSpace.CreateIcc(iccProfile) : null;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul, space);
        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), width * 4);
            var options = new SKWebpEncoderOptions(lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, Math.Clamp(quality, 1, 100));
            using var data = pixmap.Encode(options) ?? throw new InvalidOperationException("WebP encoding failed.");
            data.SaveTo(output);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Converts straight-alpha RGBA8 pixels from <paramref name="iccProfile"/> to sRGB in place; false if the profile is unusable.</summary>
    public static bool ConvertToSrgb(byte[] rgba, int width, int height, byte[] iccProfile)
    {
        using var source = SKColorSpace.CreateIcc(iccProfile);
        if (source is null) return false;
        if (source.IsSrgb) return true;
        var srcInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul, source);
        var dstInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
        var result = new byte[rgba.Length];
        var src = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        var dst = GCHandle.Alloc(result, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(srcInfo, src.AddrOfPinnedObject(), width * 4);
            if (!pixmap.ReadPixels(dstInfo, dst.AddrOfPinnedObject(), width * 4)) return false;
        }
        finally
        {
            src.Free();
            dst.Free();
        }
        // Alpha is unchanged; keep the exact values in case the conversion rounded them.
        for (long i = 0; i < result.LongLength; i += 4) result[i + 3] = rgba[i + 3];
        result.CopyTo(rgba, 0);
        return true;
    }
}
