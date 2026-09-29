using Strayta.Core;

namespace Strayta.Rendering;

/// <summary>A premultiplied RGBA float buffer covering <see cref="Bounds"/> in document space.</summary>
public sealed class RenderBuffer
{
    public RenderBuffer(PixelRect bounds)
    {
        Bounds = bounds;
        Pixels = new float[(long)bounds.Width * bounds.Height * 4];
    }

    /// <summary>A buffer whose pixels the caller overwrites entirely (so they need not be cleared first).</summary>
    private RenderBuffer(PixelRect bounds, bool uninitialized)
    {
        Bounds = bounds;
        Pixels = uninitialized ? GC.AllocateUninitializedArray<float>(bounds.Width * bounds.Height * 4) : new float[(long)bounds.Width * bounds.Height * 4];
    }

    public PixelRect Bounds { get; }

    /// <summary>Premultiplied RGBA, row-major over <see cref="Bounds"/>.</summary>
    public float[] Pixels { get; }

    public int Width => Bounds.Width;
    public int Height => Bounds.Height;

    public int IndexOf(int docX, int docY) => ((docY - Bounds.Top) * Bounds.Width + (docX - Bounds.Left)) * 4;

    /// <summary>Copies all pixels into <paramref name="other"/>, which must have the same bounds.</summary>
    public void CopyTo(RenderBuffer other)
    {
        if (other.Bounds != Bounds) throw new ArgumentException("Buffers must have the same bounds.", nameof(other));
        Pixels.AsSpan().CopyTo(other.Pixels);
    }

    public RenderBuffer CopyRegion(PixelRect region)
    {
        var copy = new RenderBuffer(region, uninitialized: true);
        Parallel.For(region.Top, region.Bottom, y =>
            Array.Copy(Pixels, IndexOf(region.Left, y), copy.Pixels, copy.IndexOf(region.Left, y), region.Width * 4));
        return copy;
    }

    /// <summary>Converts to straight-alpha 8-bit RGBA, optionally encoding linear values to sRGB.</summary>
    public byte[] ToRgba8(bool linearToSrgb = false, CancellationToken cancel = default)
    {
        var rgba = new byte[(long)Width * Height * 4];
        Parallel.For(0, Height, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * 4;
                float a = Pixels[i + 3];
                for (int c = 0; c < 3; c++)
                {
                    float v = a > 0f ? Pixels[i + c] / a : 0f;
                    if (linearToSrgb) v = RgbaConverter.LinearToSrgb(Math.Clamp(v, 0f, 1f));
                    rgba[i + c] = RgbaConverter.ToByte(v);
                }
                rgba[i + 3] = RgbaConverter.ToByte(a);
            }
        });
        return rgba;
    }
}
