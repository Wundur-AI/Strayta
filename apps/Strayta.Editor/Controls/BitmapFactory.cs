using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Strayta.Editor.Controls;

internal static class BitmapFactory
{
    /// <summary>Wraps straight-alpha RGBA8 pixels in an Avalonia bitmap.</summary>
    public static WriteableBitmap FromRgba(byte[] rgba, int width, int height)
    {
        var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        int stride = width * 4;
        for (int y = 0; y < height; y++)
            Marshal.Copy(rgba, y * stride, fb.Address + y * fb.RowBytes, stride);
        return bmp;
    }
}
