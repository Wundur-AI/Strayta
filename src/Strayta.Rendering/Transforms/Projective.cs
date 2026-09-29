using Strayta.Core;

namespace Strayta.Rendering.Transforms;

/// <summary>
/// A 2D projective map (homography) in document pixel space:
/// (x, y) → ((M11·x + M12·y + M13) / w, (M21·x + M22·y + M23) / w) with w = M31·x + M32·y + M33.
/// The Perspective Crop tool maps the quadrilateral the user drew onto an upright rectangle with it, and smart
/// objects in perspective place their content with it.
/// </summary>
public readonly record struct Projective(double M11, double M12, double M13, double M21, double M22, double M23, double M31, double M32, double M33)
{
    public static Projective Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public static Projective FromAffine(Affine a) => new(a.M11, a.M12, a.Dx, a.M21, a.M22, a.Dy, 0, 0, 1);

    public (double X, double Y) Apply(double x, double y)
    {
        double w = M31 * x + M32 * y + M33;
        return ((M11 * x + M12 * y + M13) / w, (M21 * x + M22 * y + M23) / w);
    }

    /// <summary>True when the map has no perspective (its last row is 0, 0, 1 up to scale).</summary>
    public bool IsAffine => Math.Abs(M31) < 1e-12 * Math.Abs(M33) && Math.Abs(M32) < 1e-12 * Math.Abs(M33);

    /// <summary>The affine part, exact when <see cref="IsAffine"/>.</summary>
    public Affine ToAffine() => new(M11 / M33, M12 / M33, M21 / M33, M22 / M33, M13 / M33, M23 / M33);

    /// <summary>
    /// The affine map that agrees with this one at (<paramref name="x"/>, <paramref name="y"/>) to first order: the
    /// best affine stand-in near that point (used for guides and paths, which cannot be put in perspective).
    /// </summary>
    public Affine Linearize(double x, double y)
    {
        double w = M31 * x + M32 * y + M33;
        var (px, py) = Apply(x, y);
        double a11 = (M11 - px * M31) / w, a12 = (M12 - px * M32) / w;
        double a21 = (M21 - py * M31) / w, a22 = (M22 - py * M32) / w;
        return new Affine(a11, a12, a21, a22, px - a11 * x - a12 * y, py - a21 * x - a22 * y);
    }

    /// <summary>This map followed by <paramref name="next"/>.</summary>
    public Projective Then(Projective next) => new(
        next.M11 * M11 + next.M12 * M21 + next.M13 * M31, next.M11 * M12 + next.M12 * M22 + next.M13 * M32, next.M11 * M13 + next.M12 * M23 + next.M13 * M33,
        next.M21 * M11 + next.M22 * M21 + next.M23 * M31, next.M21 * M12 + next.M22 * M22 + next.M23 * M32, next.M21 * M13 + next.M22 * M23 + next.M23 * M33,
        next.M31 * M11 + next.M32 * M21 + next.M33 * M31, next.M31 * M12 + next.M32 * M22 + next.M33 * M32, next.M31 * M13 + next.M32 * M23 + next.M33 * M33);

    public Projective Invert()
    {
        double c11 = M22 * M33 - M23 * M32, c12 = M13 * M32 - M12 * M33, c13 = M12 * M23 - M13 * M22;
        double c21 = M23 * M31 - M21 * M33, c22 = M11 * M33 - M13 * M31, c23 = M13 * M21 - M11 * M23;
        double c31 = M21 * M32 - M22 * M31, c32 = M12 * M31 - M11 * M32, c33 = M11 * M22 - M12 * M21;
        double det = M11 * c11 + M12 * c21 + M13 * c31;
        if (Math.Abs(det) < 1e-18) throw new InvalidOperationException("The projective map is not invertible.");
        return new(c11 / det, c12 / det, c13 / det, c21 / det, c22 / det, c23 / det, c31 / det, c32 / det, c33 / det);
    }

    /// <summary>
    /// The map taking the rectangle (0, 0)–(<paramref name="width"/>, <paramref name="height"/>) to the quadrilateral
    /// <paramref name="quad"/> (images of its top-left, top-right, bottom-right and bottom-left corners).
    /// </summary>
    public static Projective RectToQuad(double width, double height, IReadOnlyList<(double X, double Y)> quad)
    {
        if (quad.Count != 4) throw new ArgumentException("A quadrilateral has four corners.", nameof(quad));
        // Unit square to quad (Heckbert's closed form), then scale the rectangle to the unit square.
        var (x0, y0) = quad[0];
        var (x1, y1) = quad[1];
        var (x2, y2) = quad[2];
        var (x3, y3) = quad[3];
        double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
        double g, h;
        if (Math.Abs(sx) < 1e-12 && Math.Abs(sy) < 1e-12) g = h = 0;
        else
        {
            double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
            double den = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(den) < 1e-18) throw new ArgumentException("The quadrilateral is degenerate.", nameof(quad));
            g = (sx * dy2 - dx2 * sy) / den;
            h = (dx1 * sy - sx * dy1) / den;
        }
        var unit = new Projective(
            x1 - x0 + g * x1, x3 - x0 + h * x3, x0,
            y1 - y0 + g * y1, y3 - y0 + h * y3, y0,
            g, h, 1);
        return new Projective(1 / width, 0, 0, 0, 1 / height, 0, 0, 0, 1).Then(unit);
    }
}

/// <summary>
/// Resamples layer pixels and masks through a <see cref="Projective"/> map with the same quality rules as
/// <see cref="Resampler"/>: premultiplied Catmull-Rom (or tent) filtering, the kernel widened where the map shrinks
/// the image (the local reduction varies across a perspective, so it is worked out per pixel), and an anti-aliased
/// edge.
/// </summary>
public static class ProjectiveResampler
{
    private const int MaxTaps = 64;

    /// <summary>Whole-pixel bounds of <paramref name="rect"/> after <paramref name="map"/> (which must keep it in front of the horizon).</summary>
    public static PixelRect TransformBounds(PixelRect rect, Projective map)
    {
        if (rect.IsEmpty) return PixelRect.Empty;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in new (double, double)[] { (rect.Left, rect.Top), (rect.Right, rect.Top), (rect.Right, rect.Bottom), (rect.Left, rect.Bottom) })
        {
            var (px, py) = map.Apply(x, y);
            minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
        }
        const double eps = 1e-6, limit = 1 << 28;
        if (!double.IsFinite(minX + minY + maxX + maxY) || maxX - minX > limit || maxY - minY > limit)
            throw new ArgumentException("The map sends the rectangle past the horizon.", nameof(map));
        return new PixelRect((int)Math.Floor(minX + eps), (int)Math.Floor(minY + eps), (int)Math.Ceiling(maxX - eps), (int)Math.Ceiling(maxY - eps));
    }

    /// <summary>
    /// Maps layer pixels covering <paramref name="bounds"/>; returns them with alpha and their new bounds, clipped to
    /// <paramref name="clip"/> (null pixels when nothing remains).
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds) TransformRaster(Raster raster, PixelRect bounds, Projective map, ResampleFilter filter,
        PixelRect? clip = null, CancellationToken cancel = default)
    {
        if (map.IsAffine) return Resampler.TransformRaster(raster, bounds, map.ToAffine(), filter, clip, cancel);
        var source = ResampleSource.FromRaster(raster);
        var target = TransformBounds(bounds, map);
        if (clip is { } c) target = target.Intersect(c);
        if (target.IsEmpty) return (null, PixelRect.Empty);
        int colors = source.Channels - 1, ch = source.Channels, w = target.Width;
        var planes = Enumerable.Range(0, ch).Select(_ => Plane.Create(w, target.Height, source.BitDepth)).ToArray();
        Resample(source, bounds, map, filter, target, cancel, (y, sums, weights, coverage) =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
            {
                int o = x * ch;
                float alphaSum = sums[o + colors];
                float a = Math.Clamp(alphaSum / weights[x], 0f, 1f) * coverage[x];
                float inv = alphaSum > 1e-9f ? 1f / alphaSum : 0f;
                for (int k = 0; k < colors; k++) Resampler.Store(planes[k], i, sums[o + k] * inv);
                Resampler.Store(planes[colors], i, a);
            }
        });
        return (new Raster(source.ColorMode, planes[..colors], planes[colors]), target);
    }

    /// <summary>Maps a layer mask (unlinked masks too: a perspective crop changes the whole canvas).</summary>
    public static LayerMask? TransformMask(LayerMask? mask, Projective map, ResampleFilter filter, PixelRect? clip = null, CancellationToken cancel = default)
    {
        if (mask is null) return null;
        if (map.IsAffine && mask.PositionRelativeToLayer is false) return Resampler.TransformMask(mask, map.ToAffine(), filter, clip, cancel);
        var target = TransformBounds(mask.Bounds, map);
        if (clip is { } c) target = target.Intersect(c);
        if (mask.Pixels is not { } plane || target.IsEmpty) return With(mask, null, target.IsEmpty ? PixelRect.Empty : target);
        var source = ResampleSource.FromPlane(plane);
        int w = target.Width;
        float outside = mask.DefaultColor / 255f;
        var result = Plane.Create(w, target.Height, plane.BitDepth);
        Resample(source, mask.Bounds, map, filter, target, cancel, (y, sums, weights, coverage) =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
                Resampler.Store(result, i, coverage[x] * (sums[x] / weights[x]) + (1 - coverage[x]) * outside);
        });
        return With(mask, result, target);
    }

    private static LayerMask With(LayerMask m, Plane? pixels, PixelRect bounds) => new()
    {
        Bounds = bounds,
        Pixels = pixels,
        DefaultColor = m.DefaultColor,
        Disabled = m.Disabled,
        PositionRelativeToLayer = m.PositionRelativeToLayer,
    };

    private delegate void RowSink(int y, float[] sums, float[] weights, float[] coverage);

    private static void Resample(ResampleSource src, PixelRect srcBounds, Projective map, ResampleFilter filter, PixelRect target,
        CancellationToken cancel, RowSink sink)
    {
        var inv = map.Invert();
        bool cubic = filter == ResampleFilter.Bicubic;
        double support = cubic ? 2 : 1;
        int sw = src.Width, sh = src.Height, ch = src.Channels, w = target.Width;
        float[] data = src.Data;

        Parallel.For(target.Top, target.Bottom, new ParallelOptions { CancellationToken = cancel }, () => new
        {
            Sums = new float[w * ch], Weights = new float[w], Coverage = new float[w],
            Wx = new float[MaxTaps], Wy = new float[MaxTaps], Cols = new int[MaxTaps],
        }, (y, _, buf) =>
        {
            Array.Clear(buf.Sums);
            double py = y + 0.5;
            for (int x = 0; x < w; x++)
            {
                double px = target.Left + x + 0.5;
                double hw = inv.M31 * px + inv.M32 * py + inv.M33;
                double hx = inv.M11 * px + inv.M12 * py + inv.M13, hy = inv.M21 * px + inv.M22 * py + inv.M23;
                double sx = hx / hw - srcBounds.Left, sy = hy / hw - srcBounds.Top;
                // The inverse map's Jacobian here: how far the source moves per output pixel along each source axis.
                double j11 = (inv.M11 - hx / hw * inv.M31) / hw, j12 = (inv.M12 - hx / hw * inv.M32) / hw;
                double j21 = (inv.M21 - hy / hw * inv.M31) / hw, j22 = (inv.M22 - hy / hw * inv.M32) / hw;
                double stepX = Math.Sqrt(j11 * j11 + j12 * j12), stepY = Math.Sqrt(j21 * j21 + j22 * j22);
                double cov = Resampler.EdgeCoverage(sx, sw, stepX) * Resampler.EdgeCoverage(sy, sh, stepY);
                buf.Coverage[x] = (float)cov;
                buf.Weights[x] = 1;
                if (cov <= 0 || hw <= 0) continue;

                double fx = Math.Max(1, stepX), fy = Math.Max(1, stepY);
                double rx = support * fx, ry = support * fy;
                int x0 = (int)Math.Ceiling(sx - rx - 0.5), x1 = (int)Math.Floor(sx + rx - 0.5);
                int y0 = (int)Math.Ceiling(sy - ry - 0.5), y1 = (int)Math.Floor(sy + ry - 0.5);
                int nx = Math.Min(x1 - x0 + 1, MaxTaps), ny = Math.Min(y1 - y0 + 1, MaxTaps);
                float sumX = 0, sumY = 0;
                for (int k = 0; k < nx; k++)
                {
                    sumX += buf.Wx[k] = Resampler.Kernel((x0 + k + 0.5 - sx) / fx, cubic);
                    buf.Cols[k] = Math.Clamp(x0 + k, 0, sw - 1);
                }
                for (int k = 0; k < ny; k++) sumY += buf.Wy[k] = Resampler.Kernel((y0 + k + 0.5 - sy) / fy, cubic);
                buf.Weights[x] = sumX * sumY;
                int o = x * ch;
                for (int j = 0; j < ny; j++)
                {
                    float wj = buf.Wy[j];
                    if (wj == 0) continue;
                    long row = (long)Math.Clamp(y0 + j, 0, sh - 1) * sw;
                    for (int i = 0; i < nx; i++)
                    {
                        float wt = buf.Wx[i] * wj;
                        if (wt == 0) continue;
                        long s = (row + buf.Cols[i]) * ch;
                        for (int c = 0; c < ch; c++) buf.Sums[o + c] += data[s + c] * wt;
                    }
                }
            }
            sink(y - target.Top, buf.Sums, buf.Weights, buf.Coverage);
            return buf;
        }, _ => { });
    }
}
