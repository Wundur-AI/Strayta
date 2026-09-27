using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Strayta.Core;

namespace Strayta.Rendering.Transforms;

public enum ResampleFilter
{
    /// <summary>Tent filter: fast, slightly soft; used for interactive previews.</summary>
    Bilinear,

    /// <summary>Catmull-Rom cubic: sharp enlargements without the overshoot of sharper cubics; used on commit.</summary>
    Bicubic,
}

/// <summary>
/// A raster or mask converted once to interleaved floats (color premultiplied by alpha), so it can be
/// resampled repeatedly, e.g. on every frame of a transform drag, without converting it again.
/// </summary>
public sealed class ResampleSource
{
    private ResampleSource(float[] data, int width, int height, int channels, bool premultiplied, ColorMode mode, int bitDepth)
    {
        Data = data;
        Width = width;
        Height = height;
        Channels = channels;
        Premultiplied = premultiplied;
        ColorMode = mode;
        BitDepth = bitDepth;
    }

    internal float[] Data { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Samples per pixel: the color planes plus alpha for rasters, 1 for masks.</summary>
    internal int Channels { get; }

    /// <summary>True for rasters (last channel is alpha, colors are premultiplied), false for masks.</summary>
    internal bool Premultiplied { get; }

    public ColorMode ColorMode { get; }
    public int BitDepth { get; }

    /// <summary>A raster's color and alpha; a raster without alpha is treated as opaque.</summary>
    public static ResampleSource FromRaster(Raster raster)
    {
        int w = raster.Width, h = raster.Height, colors = raster.ColorPlanes.Count, ch = colors + 1;
        var data = new float[(long)w * h * ch];
        var alpha = raster.Alpha;
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
            {
                float a = alpha is null ? 1f : Math.Clamp(alpha.GetNormalized(i), 0f, 1f);
                long o = (long)i * ch;
                for (int c = 0; c < colors; c++) data[o + c] = raster.ColorPlanes[c].GetNormalized(i) * a;
                data[o + colors] = a;
            }
        });
        return new ResampleSource(data, w, h, ch, premultiplied: true, raster.ColorMode, raster.BitDepth);
    }

    /// <summary>A single plane (a layer mask), resampled as plain values.</summary>
    public static ResampleSource FromPlane(Plane plane)
    {
        var data = new float[(long)plane.Width * plane.Height];
        Parallel.For(0, plane.Height, y =>
        {
            for (int x = 0, i = y * plane.Width; x < plane.Width; x++, i++) data[i] = plane.GetNormalized(i);
        });
        return new ResampleSource(data, plane.Width, plane.Height, 1, premultiplied: false, ColorMode.Grayscale, plane.BitDepth);
    }
}

/// <summary>
/// Applies affine transforms (scale, rotation, skew, translation) to layer pixels and masks.
/// <para>
/// Each output pixel center is mapped back into the source and sampled with a separable kernel. When the
/// transform shrinks the image the kernel is widened by the reduction along each source axis, so strong
/// reductions average every source pixel instead of skipping some (no aliasing or moiré). Color is filtered
/// premultiplied by alpha, so transparent pixels never bleed dark fringes into edges. Sampling clamps to
/// the source edge and the edge itself is anti-aliased by the output pixel's geometric coverage, so an
/// enlarged or rotated layer keeps a crisp outline rather than a half-transparent blurred rim.
/// </para>
/// </summary>
public static class Resampler
{
    /// <summary>Document-space bounds of a layer's non-transparent pixels: the box Free Transform frames.</summary>
    public static PixelRect ContentBounds(PixelLayer layer)
    {
        if (layer.Pixels is not { } raster || layer.Bounds.IsEmpty) return PixelRect.Empty;
        if (raster.Alpha is null) return layer.Bounds;
        var nz = Coverage.NonZeroBounds(raster.Alpha);
        var b = layer.Bounds;
        return nz.IsEmpty ? PixelRect.Empty : new PixelRect(b.Left + nz.Left, b.Top + nz.Top, b.Left + nz.Right, b.Top + nz.Bottom);
    }

    /// <summary>The whole-pixel bounding box of <paramref name="rect"/> after <paramref name="m"/>.</summary>
    public static PixelRect TransformBounds(PixelRect rect, Affine m)
    {
        if (rect.IsEmpty) return PixelRect.Empty;
        Span<(double X, double Y)> corners =
        [
            m.Apply(rect.Left, rect.Top), m.Apply(rect.Right, rect.Top),
            m.Apply(rect.Right, rect.Bottom), m.Apply(rect.Left, rect.Bottom),
        ];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in corners)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        // Tolerate floating-point noise so a 90° turn of whole pixels stays exactly the same size.
        const double eps = 1e-6;
        return new PixelRect((int)Math.Floor(minX + eps), (int)Math.Floor(minY + eps), (int)Math.Ceiling(maxX - eps), (int)Math.Ceiling(maxY - eps));
    }

    /// <summary>
    /// Transforms layer pixels covering <paramref name="bounds"/>. Returns the new pixels (always with an
    /// alpha plane) and their bounds; null pixels when nothing remains inside <paramref name="clip"/>.
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds) TransformRaster(
        Raster raster, PixelRect bounds, Affine m, ResampleFilter filter, PixelRect? clip = null, CancellationToken cancel = default)
    {
        if (m.IsIntegerTranslation(out int dx, out int dy))
            return Clip(raster, Shift(bounds, dx, dy), clip);
        return TransformRaster(ResampleSource.FromRaster(raster), bounds, m, filter, clip, cancel);
    }

    /// <inheritdoc cref="TransformRaster(Raster, PixelRect, Affine, ResampleFilter, PixelRect?, CancellationToken)"/>
    public static (Raster? Pixels, PixelRect Bounds) TransformRaster(
        ResampleSource source, PixelRect bounds, Affine m, ResampleFilter filter, PixelRect? clip = null, CancellationToken cancel = default)
    {
        if (!source.Premultiplied) throw new ArgumentException("Expected a raster source.", nameof(source));
        var target = TransformBounds(bounds, m);
        if (clip is { } c) target = target.Intersect(c);
        if (target.IsEmpty) return (null, PixelRect.Empty);

        int colors = source.Channels - 1, ch = source.Channels, w = target.Width;
        var planes = Enumerable.Range(0, ch).Select(_ => Plane.Create(w, target.Height, source.BitDepth)).ToArray();
        Resample(source, bounds, m, filter, target, cancel, (y, sums, weights, coverage) =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
            {
                int o = x * ch;
                float alphaSum = sums[o + colors];
                float a = Math.Clamp(alphaSum / weights[x], 0f, 1f) * coverage[x];
                float inv = alphaSum > 1e-9f ? 1f / alphaSum : 0f;
                for (int k = 0; k < colors; k++) Store(planes[k], i, sums[o + k] * inv);
                Store(planes[colors], i, a);
            }
        });
        return (new Raster(source.ColorMode, planes[..colors], planes[colors]), target);
    }

    /// <summary>
    /// Transforms a layer mask along with its layer. Unlinked masks (positioned independently of the layer)
    /// and solid masks without pixels are only repositioned. Outside the transformed area the mask keeps its
    /// default color.
    /// </summary>
    public static LayerMask? TransformMask(LayerMask? mask, Affine m, ResampleFilter filter, PixelRect? clip = null, CancellationToken cancel = default)
    {
        if (mask is null || mask.PositionRelativeToLayer) return mask;
        if (mask.Pixels is null || m.IsIntegerTranslation(out _, out _))
            return With(mask, mask.Pixels, TransformBounds(mask.Bounds, m));
        return TransformMask(mask, ResampleSource.FromPlane(mask.Pixels), m, filter, clip, cancel);
    }

    /// <inheritdoc cref="TransformMask(LayerMask?, Affine, ResampleFilter, PixelRect?, CancellationToken)"/>
    public static LayerMask TransformMask(LayerMask mask, ResampleSource source, Affine m, ResampleFilter filter, PixelRect? clip = null, CancellationToken cancel = default)
    {
        var target = TransformBounds(mask.Bounds, m);
        if (clip is { } c) target = target.Intersect(c);
        if (target.IsEmpty) return With(mask, null, PixelRect.Empty);

        int w = target.Width;
        float outside = mask.DefaultColor / 255f;
        var plane = Plane.Create(w, target.Height, source.BitDepth);
        Resample(source, mask.Bounds, m, filter, target, cancel, (y, sums, weights, coverage) =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
                Store(plane, i, coverage[x] * (sums[x] / weights[x]) + (1 - coverage[x]) * outside);
        });
        return With(mask, plane, target);
    }

    private static LayerMask With(LayerMask m, Plane? pixels, PixelRect bounds) => new()
    {
        Bounds = bounds,
        Pixels = pixels,
        DefaultColor = m.DefaultColor,
        Disabled = m.Disabled,
        PositionRelativeToLayer = m.PositionRelativeToLayer,
    };

    /// <summary>
    /// Receives one output row (0-based): per pixel, the kernel-weighted channel sums, the total kernel weight
    /// (sums divided by it are the filtered values) and the edge coverage (0..1).
    /// </summary>
    private delegate void RowSink(int y, float[] sums, float[] weights, float[] coverage);

    private static void Resample(ResampleSource src, PixelRect srcBounds, Affine m, ResampleFilter filter, PixelRect target,
        CancellationToken cancel, RowSink sink)
    {
        var inv = m.Invert();
        // How far the source moves per output pixel along each source axis; above 1 the transform shrinks
        // that axis and the kernel widens to cover every source pixel that falls into the output pixel.
        double stepX = Math.Sqrt(inv.M11 * inv.M11 + inv.M12 * inv.M12);
        double stepY = Math.Sqrt(inv.M21 * inv.M21 + inv.M22 * inv.M22);
        double fx = Math.Max(1, stepX), fy = Math.Max(1, stepY);
        bool cubic = filter == ResampleFilter.Bicubic;
        // Without reduction a tent filter touches exactly 2×2 source pixels; that case (interactive previews)
        // gets a direct path.
        bool simpleBilinear = !cubic && fx == 1 && fy == 1;
        double support = cubic ? 2 : 1;
        double rx = support * fx, ry = support * fy;
        int maxTapsX = (int)Math.Ceiling(2 * rx) + 2, maxTapsY = (int)Math.Ceiling(2 * ry) + 2;

        int sw = src.Width, sh = src.Height, ch = src.Channels, w = target.Width;
        float[] data = src.Data;

        Parallel.For(target.Top, target.Bottom, new ParallelOptions { CancellationToken = cancel },
            () => new RowBuffers(w, ch, maxTapsX, maxTapsY),
            (y, _, buf) =>
        {
            float[] sums = buf.Sums, weights = buf.Weights, coverage = buf.Coverage, wx = buf.Wx, wy = buf.Wy;
            int[] cols = buf.Cols;
            Array.Clear(sums);
            double py = y + 0.5;
            for (int x = 0; x < w; x++)
            {
                double px = target.Left + x + 0.5;
                // Output pixel center in the source's own pixel coordinates.
                double sx = inv.M11 * px + inv.M12 * py + inv.Dx - srcBounds.Left;
                double sy = inv.M21 * px + inv.M22 * py + inv.Dy - srcBounds.Top;

                // Distance (in output pixels) from the center to the nearest source edge on each axis gives an
                // anti-aliased coverage of the transformed source rectangle.
                double cov = EdgeCoverage(sx, sw, stepX) * EdgeCoverage(sy, sh, stepY);
                coverage[x] = (float)cov;
                weights[x] = 1;
                if (cov <= 0) continue;
                var acc = sums.AsSpan(x * ch, ch);

                if (simpleBilinear)
                {
                    double gx = sx - 0.5, gy = sy - 0.5;
                    int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
                    float tx = (float)(gx - ix), ty = (float)(gy - iy);
                    int c0 = Math.Clamp(ix, 0, sw - 1), c1 = Math.Clamp(ix + 1, 0, sw - 1);
                    long r0 = (long)Math.Clamp(iy, 0, sh - 1) * sw, r1 = (long)Math.Clamp(iy + 1, 0, sh - 1) * sw;
                    Accumulate(data, (r0 + c0) * ch, (1 - tx) * (1 - ty), acc);
                    Accumulate(data, (r0 + c1) * ch, tx * (1 - ty), acc);
                    Accumulate(data, (r1 + c0) * ch, (1 - tx) * ty, acc);
                    Accumulate(data, (r1 + c1) * ch, tx * ty, acc);
                    continue;
                }

                int x0 = (int)Math.Ceiling(sx - rx - 0.5), x1 = (int)Math.Floor(sx + rx - 0.5);
                int y0 = (int)Math.Ceiling(sy - ry - 0.5), y1 = (int)Math.Floor(sy + ry - 0.5);
                int nx = Math.Min(x1 - x0 + 1, maxTapsX), ny = Math.Min(y1 - y0 + 1, maxTapsY);
                float sumX = 0, sumY = 0;
                for (int k = 0; k < nx; k++)
                {
                    sumX += wx[k] = Kernel((x0 + k + 0.5 - sx) / fx, cubic);
                    cols[k] = Math.Clamp(x0 + k, 0, sw - 1);
                }
                for (int k = 0; k < ny; k++) sumY += wy[k] = Kernel((y0 + k + 0.5 - sy) / fy, cubic);
                weights[x] = sumX * sumY;

                for (int j = 0; j < ny; j++)
                {
                    float wj = wy[j];
                    if (wj == 0) continue;
                    long row = (long)Math.Clamp(y0 + j, 0, sh - 1) * sw;
                    for (int i = 0; i < nx; i++)
                    {
                        float wt = wx[i] * wj;
                        if (wt != 0) Accumulate(data, (row + cols[i]) * ch, wt, acc);
                    }
                }
            }
            sink(y - target.Top, sums, weights, coverage);
            return buf;
        }, _ => { });
    }

    /// <summary>Scratch space for one worker thread, reused across the rows it processes.</summary>
    private sealed class RowBuffers(int width, int channels, int tapsX, int tapsY)
    {
        public float[] Sums { get; } = new float[width * channels];
        public float[] Weights { get; } = new float[width];
        public float[] Coverage { get; } = new float[width];
        public float[] Wx { get; } = new float[tapsX];
        public float[] Wy { get; } = new float[tapsY];
        public int[] Cols { get; } = new int[tapsX];
    }

    /// <summary>acc += weight × the pixel at <paramref name="offset"/>; RGBA pixels take one vector operation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Accumulate(float[] data, long offset, float weight, Span<float> acc)
    {
        if (acc.Length == 4 && Vector128.IsHardwareAccelerated)
        {
            ref float src = ref MemoryMarshal.GetArrayDataReference(data);
            ref float dst = ref MemoryMarshal.GetReference(acc);
            var sum = Vector128.LoadUnsafe(ref dst) + Vector128.LoadUnsafe(ref src, (nuint)offset) * weight;
            sum.StoreUnsafe(ref dst);
            return;
        }
        for (int c = 0; c < acc.Length; c++) acc[c] += data[offset + c] * weight;
    }

    internal static float Kernel(double d, bool cubic)
    {
        d = Math.Abs(d);
        if (!cubic) return d < 1 ? (float)(1 - d) : 0f;
        // Catmull-Rom (a = -0.5).
        if (d < 1) return (float)(1.5 * d * d * d - 2.5 * d * d + 1);
        if (d < 2) return (float)(-0.5 * d * d * d + 2.5 * d * d - 4 * d + 2);
        return 0f;
    }

    internal static double EdgeCoverage(double s, int size, double step)
    {
        double inside = Math.Min(s, size - s) / step; // output pixels from the center to the nearer edge
        return Math.Clamp(0.5 + inside, 0, 1);
    }

    internal static void Store(Plane p, int i, float v)
    {
        switch (p.BitDepth)
        {
            case 8: p.Data[i] = RgbaConverter.ToByte(Math.Clamp(v, 0f, 1f)); break;
            case 16: p.AsUInt16()[i] = (ushort)Math.Clamp(MathF.Round(v * 65535f), 0f, 65535f); break;
            default: p.AsSingle()[i] = Math.Max(0f, v); break; // 32-bit is linear light and may exceed 1
        }
    }

    private static PixelRect Shift(PixelRect r, int dx, int dy) => new(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);

    /// <summary>Crops already-positioned pixels to <paramref name="clip"/> (the whole-pixel move shortcut).</summary>
    private static (Raster?, PixelRect) Clip(Raster raster, PixelRect bounds, PixelRect? clip)
    {
        if (clip is not { } c) return (raster, bounds);
        var inside = bounds.Intersect(c);
        if (inside == bounds) return (raster, bounds);
        if (inside.IsEmpty) return (null, PixelRect.Empty);
        Plane Crop(Plane p)
        {
            int bpp = p.BitDepth / 8;
            var o = Plane.Create(inside.Width, inside.Height, p.BitDepth);
            for (int y = 0; y < inside.Height; y++)
                Array.Copy(p.Data, (((long)(y + inside.Top - bounds.Top) * bounds.Width) + inside.Left - bounds.Left) * bpp,
                    o.Data, (long)y * inside.Width * bpp, (long)inside.Width * bpp);
            return o;
        }
        return (new Raster(raster.ColorMode, raster.ColorPlanes.Select(Crop).ToArray(), raster.Alpha is null ? null : Crop(raster.Alpha)), inside);
    }
}
