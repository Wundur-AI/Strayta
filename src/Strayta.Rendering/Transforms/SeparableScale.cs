using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Strayta.Core;

namespace Strayta.Rendering.Transforms;

/// <summary>
/// Axis-aligned scaling (Image Size) with the same filters, area widening, premultiplied color and anti-aliased
/// edges as <see cref="Resampler"/>, but done as two one-dimensional passes: a reduction by 2 then costs about 20
/// taps per output pixel instead of 100. Output rows are processed in independent bands, each filtering only the
/// source rows it needs (converted to floats on the fly), so memory stays small and every core is busy.
/// </summary>
internal static class SeparableScale
{
    private const int Band = 48;

    /// <summary>Per output column (or row): the first source index, the normalized weights and the edge coverage.</summary>
    private sealed record Taps(int[] Start, float[] Weights, int Count, float[] Coverage);

    /// <summary>True when <paramref name="m"/> only scales and moves along the axes (no rotation or skew).</summary>
    public static bool Applies(Affine m) => m.M12 == 0 && m.M21 == 0 && m.M11 > 0 && m.M22 > 0;

    public static (Raster? Pixels, PixelRect Bounds) Raster(Raster raster, PixelRect bounds, Affine m, ResampleFilter filter, PixelRect? clip, CancellationToken cancel)
    {
        var target = Resampler.TransformBounds(bounds, m);
        if (clip is { } c) target = target.Intersect(c);
        if (target.IsEmpty) return (null, PixelRect.Empty);
        int colors = raster.ColorPlanes.Count, ch = colors + 1, w = target.Width;
        var planes = Enumerable.Range(0, ch).Select(_ => Plane.Create(w, target.Height, raster.BitDepth)).ToArray();
        Run(raster.Width, raster.Height, ch, RasterReader(raster), bounds, m, filter, target, cancel, (y, row, coverX, coverY) =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
            {
                int o = x * ch;
                float alphaSum = row[o + colors];
                float a = Math.Clamp(alphaSum, 0f, 1f) * coverX[x] * coverY;
                float inv = alphaSum > 1e-9f ? 1f / alphaSum : 0f;
                for (int k = 0; k < colors; k++) Resampler.Store(planes[k], i, row[o + k] * inv);
                Resampler.Store(planes[colors], i, a);
            }
        });
        return (new Raster(raster.ColorMode, planes[..colors], planes[colors]), target);
    }

    public static LayerMask Mask(LayerMask mask, Plane pixels, Affine m, ResampleFilter filter, PixelRect? clip, CancellationToken cancel)
    {
        var target = Resampler.TransformBounds(mask.Bounds, m);
        if (clip is { } c) target = target.Intersect(c);
        var result = new LayerMask
        {
            Bounds = target.IsEmpty ? PixelRect.Empty : target, DefaultColor = mask.DefaultColor, Disabled = mask.Disabled,
            PositionRelativeToLayer = mask.PositionRelativeToLayer,
            Pixels = target.IsEmpty ? null : Plane.Create(target.Width, target.Height, pixels.BitDepth),
        };
        if (result.Pixels is not { } plane) return result;
        float outside = mask.DefaultColor / 255f;
        int w = target.Width;
        Run(pixels.Width, pixels.Height, 1, PlaneReader(pixels), mask.Bounds, m, filter, target, cancel, (y, row, coverX, coverY) =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
            {
                float cov = coverX[x] * coverY;
                Resampler.Store(plane, i, cov * row[x] + (1 - cov) * outside);
            }
        });
        return result;
    }

    /// <summary>Receives one filtered output row (0-based), per-column coverage and the row's coverage.</summary>
    private delegate void RowSink(int y, float[] row, float[] coverX, float coverY);

    /// <summary>Writes source row <paramref name="y"/> as interleaved floats (premultiplied color and alpha for rasters).</summary>
    private delegate void RowReader(int y, Span<float> dst);

    private static RowReader RasterReader(Raster raster)
    {
        int w = raster.Width, colors = raster.ColorPlanes.Count, ch = colors + 1;
        var planes = raster.ColorPlanes;
        var alpha = raster.Alpha;
        if (raster.BitDepth == 8)
            return (y, dst) =>
            {
                const float scale = 1f / 255;
                int off = y * w;
                byte[]? a = alpha?.Data;
                for (int x = 0; x < w; x++)
                {
                    float al = a is null ? 1f : a[off + x] * scale;
                    int o = x * ch;
                    for (int c = 0; c < colors; c++) dst[o + c] = planes[c].Data[off + x] * scale * al;
                    dst[o + colors] = al;
                }
            };
        return (y, dst) =>
        {
            int off = y * w;
            for (int x = 0; x < w; x++)
            {
                float al = alpha is null ? 1f : Math.Clamp(alpha.GetNormalized(off + x), 0f, 1f);
                int o = x * ch;
                for (int c = 0; c < colors; c++) dst[o + c] = planes[c].GetNormalized(off + x) * al;
                dst[o + colors] = al;
            }
        };
    }

    private static RowReader PlaneReader(Plane plane) => (y, dst) =>
    {
        int off = y * plane.Width;
        if (plane.BitDepth == 8)
            for (int x = 0; x < plane.Width; x++) dst[x] = plane.Data[off + x] * (1f / 255);
        else
            for (int x = 0; x < plane.Width; x++) dst[x] = plane.GetNormalized(off + x);
    };

    private static void Run(int sw, int sh, int ch, RowReader read, PixelRect srcBounds, Affine m, ResampleFilter filter, PixelRect target,
        CancellationToken cancel, RowSink sink)
    {
        bool cubic = filter == ResampleFilter.Bicubic;
        // Output pixel center c maps to source coordinate (c - D) / M, relative to the source's own origin.
        var cols = Plan(target.Left, target.Width, m.M11, m.Dx, srcBounds.Left, sw, cubic);
        var rows = Plan(target.Top, target.Height, m.M22, m.Dy, srcBounds.Top, sh, cubic);
        int w = target.Width;
        int bands = (target.Height + Band - 1) / Band;

        Parallel.For(0, bands, new ParallelOptions { CancellationToken = cancel }, band =>
        {
            int y0 = band * Band, y1 = Math.Min(target.Height, y0 + Band);
            int first = int.MaxValue, last = int.MinValue;
            for (int y = y0; y < y1; y++)
            {
                first = Math.Min(first, rows.Start[y]);
                last = Math.Max(last, rows.Start[y] + rows.Count - 1);
            }
            first = Math.Clamp(first, 0, sh - 1);
            last = Math.Clamp(last, first, sh - 1);

            // Convert and filter horizontally just the source rows this band reads.
            int n = last - first + 1;
            var line = new float[sw * ch];
            var inter = new float[(long)n * w * ch];
            for (int r = 0; r < n; r++)
            {
                read(first + r, line);
                var dst = inter.AsSpan(r * w * ch, w * ch);
                for (int x = 0; x < w; x++)
                {
                    var acc = dst.Slice(x * ch, ch);
                    int start = cols.Start[x];
                    for (int k = 0; k < cols.Count; k++)
                    {
                        float wt = cols.Weights[x * cols.Count + k];
                        if (wt == 0) continue;
                        int sx = Math.Clamp(start + k, 0, sw - 1);
                        Accumulate(line, (long)sx * ch, wt, acc);
                    }
                }
            }

            // Vertical pass.
            var row = new float[w * ch];
            for (int y = y0; y < y1; y++)
            {
                Array.Clear(row);
                int start = rows.Start[y];
                for (int k = 0; k < rows.Count; k++)
                {
                    float wt = rows.Weights[y * rows.Count + k];
                    if (wt == 0) continue;
                    int r = Math.Clamp(start + k, 0, sh - 1) - first;
                    Accumulate(inter, (long)r * w * ch, wt, row);
                }
                sink(y, row, cols.Coverage, rows.Coverage[y]);
            }
        });
    }

    /// <summary>Filter taps for <paramref name="count"/> outputs from <paramref name="origin"/> along one axis.</summary>
    private static Taps Plan(int origin, int count, double scale, double offset, int srcOrigin, int srcSize, bool cubic)
    {
        double step = 1 / scale;           // source pixels per output pixel
        double f = Math.Max(1, step);      // kernel widening for reductions
        double radius = (cubic ? 2 : 1) * f;
        int taps = (int)Math.Ceiling(2 * radius) + 2;
        var start = new int[count];
        var weights = new float[count * taps];
        var coverage = new float[count];
        for (int i = 0; i < count; i++)
        {
            double s = (origin + i + 0.5 - offset) * step - srcOrigin;
            int s0 = (int)Math.Ceiling(s - radius - 0.5);
            start[i] = s0;
            float sum = 0;
            for (int k = 0; k < taps; k++) sum += weights[i * taps + k] = Resampler.Kernel((s0 + k + 0.5 - s) / f, cubic);
            if (sum > 0) for (int k = 0; k < taps; k++) weights[i * taps + k] /= sum;
            coverage[i] = (float)Resampler.EdgeCoverage(s, srcSize, step);
        }
        return new Taps(start, weights, taps, coverage);
    }

    /// <summary>acc += weight × the pixels at <paramref name="offset"/>, vectorized over the span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Accumulate(float[] data, long offset, float weight, Span<float> acc)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref float src = ref MemoryMarshal.GetArrayDataReference(data);
            ref float dst = ref MemoryMarshal.GetReference(acc);
            for (; i + 4 <= acc.Length; i += 4)
            {
                var sum = Vector128.LoadUnsafe(ref dst, (nuint)i) + Vector128.LoadUnsafe(ref src, (nuint)(offset + i)) * weight;
                sum.StoreUnsafe(ref dst, (nuint)i);
            }
        }
        for (; i < acc.Length; i++) acc[i] += data[offset + i] * weight;
    }
}
