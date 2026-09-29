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
        if (raster.BitDepth == 8 && colors == 3)
        {
            // The common case, without per-sample depth dispatch: one vector per pixel.
            byte[] r = raster.ColorPlanes[0].Data, g = raster.ColorPlanes[1].Data, b = raster.ColorPlanes[2].Data;
            byte[]? a8 = alpha?.Data;
            Parallel.For(0, h, y =>
            {
                ref float dst = ref MemoryMarshal.GetArrayDataReference(data);
                for (int x = 0, i = y * w; x < w; x++, i++)
                {
                    float a = a8 is null ? 1f : a8[i] * (1f / 255f);
                    var v = Vector128.Create(r[i] * (1f / 255f), g[i] * (1f / 255f), b[i] * (1f / 255f), 1f) * a;
                    v.StoreUnsafe(ref dst, (nuint)i * 4);
                }
            });
            return new ResampleSource(data, w, h, ch, premultiplied: true, raster.ColorMode, raster.BitDepth);
        }
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
        // A crop only needs the part of the source that lands on the canvas (a turned crop keeps well under the whole
        // image): convert just that, plus the kernel's reach, to floats.
        if (clip is { } c && NeededSource(bounds, m, c) is { } needed && needed != bounds)
        {
            if (needed.IsEmpty) return (null, PixelRect.Empty);
            (raster, bounds) = Clip(raster, bounds, needed) is ({ } part, var partBounds) ? (part, partBounds) : (raster, bounds);
        }
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
        bool bytes = source.BitDepth == 8;
        Resample(source, bounds, m, filter, target, cancel, (y, sums, weights, coverage) =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
            {
                int o = x * ch;
                float alphaSum = sums[o + colors];
                float a = Math.Clamp(alphaSum / weights[x], 0f, 1f) * coverage[x];
                float inv = alphaSum > 1e-9f ? 1f / alphaSum : 0f;
                if (bytes)
                {
                    for (int k = 0; k < colors; k++) planes[k].Data[i] = RgbaConverter.ToByte(Math.Clamp(sums[o + k] * inv, 0f, 1f));
                    planes[colors].Data[i] = RgbaConverter.ToByte(a);
                    continue;
                }
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
        // Turns and enlargements with the cubic touch exactly 4×4 source pixels with weights from a fixed
        // polynomial: that case (rotated crops, Free Transform commits) gets its own vectorized path.
        bool simpleCubic = cubic && fx == 1 && fy == 1;
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
            double py = y + 0.5;
            if (simpleCubic)
            {
                CubicRow(src, srcBounds, inv, target, py, stepX, stepY, sums, weights, coverage);
                sink(y - target.Top, sums, weights, coverage);
                return buf;
            }
            Array.Clear(sums);
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

    /// <summary>
    /// The part of the source (within <paramref name="bounds"/>) that output pixels inside <paramref name="clip"/> can
    /// read, with a margin wider than the kernel so its cut edges never show as layer edges; null when it is most
    /// of the source anyway.
    /// </summary>
    internal static PixelRect? NeededSource(PixelRect bounds, Affine m, PixelRect clip)
    {
        var inv = m.Invert();
        double stepX = Math.Sqrt(inv.M11 * inv.M11 + inv.M12 * inv.M12), stepY = Math.Sqrt(inv.M21 * inv.M21 + inv.M22 * inv.M22);
        int margin = (int)Math.Ceiling(2 * Math.Max(1, Math.Max(stepX, stepY))) + 3;
        var reach = TransformBounds(clip, inv);
        var needed = new PixelRect(reach.Left - margin, reach.Top - margin, reach.Right + margin, reach.Bottom + margin).Intersect(bounds);
        return (long)needed.Width * needed.Height < (long)bounds.Width * bounds.Height * 9 / 10 ? needed : null;
    }

    /// <summary>
    /// One output row of a map that does not shrink the source (turns, enlargements) with the Catmull-Rom cubic:
    /// every output pixel takes exactly 4×4 source pixels, with weights from the kernel's polynomial in the
    /// fractional position (they sum to one), and RGBA pixels are summed as vectors, a row of four taps at a time.
    /// </summary>
    private static void CubicRow(ResampleSource src, PixelRect srcBounds, Affine inv, PixelRect target, double py, double stepX, double stepY,
        float[] sums, float[] weights, float[] coverage)
    {
        int sw = src.Width, sh = src.Height, ch = src.Channels, w = target.Width;
        float[] data = src.Data;
        double px0 = target.Left + 0.5;
        double sx0 = inv.M11 * px0 + inv.M12 * py + inv.Dx - srcBounds.Left;
        double sy0 = inv.M21 * px0 + inv.M22 * py + inv.Dy - srcBounds.Top;
        Span<float> wx = stackalloc float[4], wy = stackalloc float[4];
        bool vector = ch == 4 && Vector128.IsHardwareAccelerated;
        ref float d = ref MemoryMarshal.GetArrayDataReference(data);
        for (int x = 0; x < w; x++)
        {
            double sx = sx0 + inv.M11 * x, sy = sy0 + inv.M21 * x;
            double cov = EdgeCoverage(sx, sw, stepX) * EdgeCoverage(sy, sh, stepY);
            coverage[x] = (float)cov;
            weights[x] = 1;
            var acc = sums.AsSpan(x * ch, ch);
            if (cov <= 0)
            {
                acc.Clear();
                continue;
            }
            double gx = sx - 0.5, gy = sy - 0.5;
            int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
            CubicWeights((float)(gx - ix), wx);
            CubicWeights((float)(gy - iy), wy);
            bool inside = ix >= 1 && ix + 2 < sw && iy >= 1 && iy + 2 < sh;
            if (inside && vector)
            {
                var total = Vector128<float>.Zero;
                for (int j = 0; j < 4; j++)
                {
                    nuint at = (nuint)(((long)(iy - 1 + j) * sw + ix - 1) * 4);
                    var row = Vector128.LoadUnsafe(ref d, at) * wx[0] + Vector128.LoadUnsafe(ref d, at + 4) * wx[1]
                              + Vector128.LoadUnsafe(ref d, at + 8) * wx[2] + Vector128.LoadUnsafe(ref d, at + 12) * wx[3];
                    total += row * wy[j];
                }
                total.StoreUnsafe(ref MemoryMarshal.GetReference(acc));
                continue;
            }
            if (inside && ch == 1)
            {
                // Masks: the four taps of a row are adjacent, so they load as one vector.
                float sum = 0;
                var weightsX = Vector128.Create(wx[0], wx[1], wx[2], wx[3]);
                for (int j = 0; j < 4; j++)
                {
                    nuint at = (nuint)((long)(iy - 1 + j) * sw + ix - 1);
                    sum += Vector128.Dot(Vector128.LoadUnsafe(ref d, at), weightsX) * wy[j];
                }
                acc[0] = sum;
                continue;
            }
            acc.Clear();
            for (int j = 0; j < 4; j++)
            {
                long row = (long)Math.Clamp(iy - 1 + j, 0, sh - 1) * sw;
                for (int i = 0; i < 4; i++)
                {
                    float wt = wx[i] * wy[j];
                    long at = (row + Math.Clamp(ix - 1 + i, 0, sw - 1)) * ch;
                    for (int c = 0; c < ch; c++) acc[c] += data[at + c] * wt;
                }
            }
        }
    }

    /// <summary>Catmull-Rom weights of the four taps around a sample <paramref name="t"/> (0..1) past the second one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CubicWeights(float t, Span<float> w)
    {
        float t2 = t * t, t3 = t2 * t;
        w[0] = 0.5f * (-t3 + 2 * t2 - t);
        w[1] = 0.5f * (3 * t3 - 5 * t2 + 2);
        w[2] = 0.5f * (-3 * t3 + 4 * t2 + t);
        w[3] = 0.5f * (t3 - t2);
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
    internal static (Raster?, PixelRect) Clip(Raster raster, PixelRect bounds, PixelRect? clip)
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
