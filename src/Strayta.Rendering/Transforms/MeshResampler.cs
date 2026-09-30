using Strayta.Core;

namespace Strayta.Rendering.Transforms;

/// <summary>
/// A piecewise-affine map: triangles whose corners have a source position (U, V: pixels of the source image) and a
/// target position (X, Y: document pixels). Warps are cut into a fine grid of such triangles; Puppet Warp's mesh is one
/// directly. Triangles later in the list are drawn over earlier ones where the map folds over itself.
/// </summary>
public sealed class TriangleMap
{
    public TriangleMap(double[] u, double[] v, double[] x, double[] y, int[] triangles)
    {
        if (u.Length != v.Length || u.Length != x.Length || u.Length != y.Length) throw new ArgumentException("Vertex arrays differ in length.");
        if (triangles.Length % 3 != 0) throw new ArgumentException("Triangles need three indices each.", nameof(triangles));
        (U, V, X, Y, Triangles) = (u, v, x, y, triangles);
    }

    public double[] U { get; }
    public double[] V { get; }
    public double[] X { get; }
    public double[] Y { get; }

    /// <summary>Vertex indices, three per triangle.</summary>
    public int[] Triangles { get; }

    /// <summary>Whole-pixel bounds of the target positions, or empty when some are not finite.</summary>
    public PixelRect TargetBounds()
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        for (int i = 0; i < X.Length; i++)
        {
            if (!double.IsFinite(X[i]) || !double.IsFinite(Y[i])) return PixelRect.Empty;
            x0 = Math.Min(x0, X[i]); x1 = Math.Max(x1, X[i]);
            y0 = Math.Min(y0, Y[i]); y1 = Math.Max(y1, Y[i]);
        }
        if (X.Length == 0 || x1 - x0 > 1 << 20 || y1 - y0 > 1 << 20) return PixelRect.Empty;
        return new PixelRect((int)Math.Floor(x0), (int)Math.Floor(y0), (int)Math.Ceiling(x1), (int)Math.Ceiling(y1));
    }

    /// <summary>
    /// A grid of triangles over the source rectangle (0..<paramref name="width"/> × 0..<paramref name="height"/>, plus a
    /// margin wide enough for the anti-aliased edge), fine enough that each piece is about eight target pixels across (the chord error of a smooth warp over that is
    /// a hundredth of a pixel),
    /// with every corner sent through <paramref name="map"/>. Null when the map sends points to infinity.
    /// </summary>
    public static TriangleMap? Grid(int width, int height, Func<double, double, (double X, double Y)> map)
    {
        // A coarse look first: how large the content lands, for the margin and the grid's density.
        const int coarse = 16;
        double longest = 0;
        var probe = new (double X, double Y)[(coarse + 1) * (coarse + 1)];
        for (int j = 0; j <= coarse; j++)
            for (int i = 0; i <= coarse; i++)
            {
                var p = map(width * (double)i / coarse, height * (double)j / coarse);
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return null;
                probe[j * (coarse + 1) + i] = p;
                if (i > 0) longest = Math.Max(longest, Dist(p, probe[j * (coarse + 1) + i - 1]) * coarse / width);
                if (j > 0) longest = Math.Max(longest, Dist(p, probe[(j - 1) * (coarse + 1) + i]) * coarse / height);
            }
        // longest: the largest output length of one source pixel (roughly). Margin: enough source pixels that the
        // anti-aliased edge (half an output pixel beyond the content) is inside the grid.
        double scale = Math.Max(longest, 1e-6);
        double margin = Math.Max(1.0, 1.0 / scale) + 1;
        int nx = Math.Clamp((int)Math.Ceiling((width + 2 * margin) * scale / 8), 2, 384);
        int ny = Math.Clamp((int)Math.Ceiling((height + 2 * margin) * scale / 8), 2, 384);
        int n = (nx + 1) * (ny + 1);
        double[] u = new double[n], v = new double[n], x = new double[n], y = new double[n];
        Parallel.For(0, ny + 1, j =>
        {
            double vj = -margin + (height + 2 * margin) * j / ny;
            for (int i = 0; i <= nx; i++)
            {
                int k = j * (nx + 1) + i;
                u[k] = -margin + (width + 2 * margin) * i / nx;
                v[k] = vj;
                (x[k], y[k]) = map(u[k], vj);
            }
        });
        var tris = new int[nx * ny * 6];
        for (int j = 0, t = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int k00 = j * (nx + 1) + i, k10 = k00 + 1, k01 = k00 + nx + 1, k11 = k01 + 1;
                tris[t++] = k00; tris[t++] = k10; tris[t++] = k11;
                tris[t++] = k00; tris[t++] = k11; tris[t++] = k01;
            }
        return new TriangleMap(u, v, x, y, tris);
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>
/// Resamples pixels through an arbitrary smooth map (a warp followed by a perspective placement, a puppet mesh), with
/// the same quality rules as <see cref="ProjectiveResampler"/>: premultiplied Catmull-Rom (or tent) filtering whose
/// kernel is widened where the map shrinks, and anti-aliased edges from each output pixel's distance to the content's
/// edge.
/// <para>
/// The map is only evaluated forward. The content (plus a margin for the anti-aliased edge) is cut into a fine grid of
/// triangles whose corners are mapped (<see cref="TriangleMap.Grid"/>); each triangle is then an affine piece,
/// rasterized in the output, so every output pixel center gets its source position and the local reduction (the
/// piece's inverse Jacobian). Pieces are a few output pixels across, so the piecewise error is far below a pixel.
/// Where a warp folds over itself, the later piece (lower and further right in the content) wins.
/// </para>
/// </summary>
public static class MeshResampler
{
    private const int MaxTaps = 64;

    /// <summary>
    /// Maps <paramref name="raster"/> (content pixels 0..Width × 0..Height) through <paramref name="map"/> (content
    /// position to document position); returns the pixels with alpha and their document bounds, within
    /// <paramref name="clip"/> when given.
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds) TransformRaster(Raster raster, Func<double, double, (double X, double Y)> map,
        ResampleFilter filter, PixelRect? clip = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(raster);
        return TransformRaster(ResampleSource.FromRaster(raster), map, filter, clip, cancel);
    }

    /// <inheritdoc cref="TransformRaster(Raster, Func{double, double, ValueTuple{double, double}}, ResampleFilter, PixelRect?, CancellationToken)"/>
    public static (Raster? Pixels, PixelRect Bounds) TransformRaster(ResampleSource source, Func<double, double, (double X, double Y)> map,
        ResampleFilter filter, PixelRect? clip = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);
        return TriangleMap.Grid(source.Width, source.Height, map) is { } mesh ? TransformRaster(source, mesh, filter, clip, cancel) : (null, PixelRect.Empty);
    }

    /// <summary>Maps a raster source through the triangles of <paramref name="mesh"/> (source pixels to document pixels).</summary>
    public static (Raster? Pixels, PixelRect Bounds) TransformRaster(ResampleSource source, TriangleMap mesh, ResampleFilter filter,
        PixelRect? clip = null, CancellationToken cancel = default)
    {
        if (!source.Premultiplied) throw new ArgumentException("Expected a raster source.", nameof(source));
        var target = mesh.TargetBounds();
        if (clip is { } c) target = target.Intersect(c);
        if (target.IsEmpty) return (null, PixelRect.Empty);
        int ch = source.Channels, colors = ch - 1, w = target.Width;
        var planes = Enumerable.Range(0, ch).Select(_ => Plane.Create(w, target.Height, source.BitDepth)).ToArray();
        bool bytes = source.BitDepth == 8;
        Sample(source, mesh, filter, target, cancel, (i, acc, weight, cov) =>
        {
            if (cov <= 0) return; // new planes are already transparent black
            float alphaSum = acc[colors];
            float a = Math.Clamp(alphaSum / weight, 0f, 1f) * cov;
            float inv = alphaSum > 1e-9f ? 1f / alphaSum : 0f;
            if (bytes)
            {
                for (int k = 0; k < colors; k++) planes[k].Data[i] = RgbaConverter.ToByte(Math.Clamp(acc[k] * inv, 0f, 1f));
                planes[colors].Data[i] = RgbaConverter.ToByte(a);
                return;
            }
            for (int k = 0; k < colors; k++) Resampler.Store(planes[k], i, acc[k] * inv);
            Resampler.Store(planes[colors], i, a);
        });
        return (new Raster(source.ColorMode, planes[..colors], planes[colors]), target);
    }

    /// <summary>
    /// Maps a layer mask through <paramref name="map"/> (document position to document position). Outside the mapped
    /// area the mask keeps its default color; masks without pixels and unlinked masks are kept as they are.
    /// </summary>
    public static LayerMask? TransformMask(LayerMask? mask, Func<double, double, (double X, double Y)> map, ResampleFilter filter,
        CancellationToken cancel = default)
    {
        if (mask is null || mask.PositionRelativeToLayer || mask.Pixels is not { } plane || mask.Bounds.IsEmpty) return mask;
        var b = mask.Bounds;
        var mesh = TriangleMap.Grid(b.Width, b.Height, (u, v) => map(b.Left + u, b.Top + v));
        return mesh is null ? mask : TransformMask(mask, ResampleSource.FromPlane(plane), mesh, filter, cancel);
    }

    /// <summary>Maps a mask whose pixels are <paramref name="source"/> through the triangles of <paramref name="mesh"/> (mask pixels to document pixels).</summary>
    public static LayerMask TransformMask(LayerMask mask, ResampleSource source, TriangleMap mesh, ResampleFilter filter, CancellationToken cancel = default)
    {
        var target = mesh.TargetBounds();
        if (target.IsEmpty) return mask;
        int w = target.Width;
        float outside = mask.DefaultColor / 255f;
        var plane = Plane.Create(w, target.Height, source.BitDepth);
        Sample(source, mesh, filter, target, cancel, (i, acc, weight, cov) =>
            Resampler.Store(plane, i, cov <= 0 ? outside : cov * (acc[0] / weight) + (1 - cov) * outside));
        return new LayerMask
        {
            Bounds = target, Pixels = plane, DefaultColor = mask.DefaultColor, Disabled = mask.Disabled,
            PositionRelativeToLayer = mask.PositionRelativeToLayer, AppliedToPixels = mask.AppliedToPixels,
        };
    }

    /// <summary>Receives one output pixel (index into the target): the kernel-weighted channel sums, the total weight and the edge coverage.</summary>
    private delegate void PixelSink(int index, float[] acc, float weight, float coverage);

    private static void Sample(ResampleSource source, TriangleMap mesh, ResampleFilter filter, PixelRect target, CancellationToken cancel, PixelSink sink)
    {
        int sw = source.Width, sh = source.Height, w = target.Width, h = target.Height;

        // Pass 1: rasterize the pieces, noting each output pixel's source position and inverse Jacobian. Bands of
        // rows run in parallel; within a band triangles are drawn in order, so folds resolve the same way every time.
        var at = new float[(long)w * h * 2];
        var jac = new float[(long)w * h * 4];
        var hit = new bool[(long)w * h];
        var tris = mesh.Triangles;
        int count = tris.Length / 3;
        int bandHeight = Math.Max(16, (h + 63) / 64), bands = (h + bandHeight - 1) / bandHeight;
        var lists = new List<int>[bands];
        for (int b = 0; b < bands; b++) lists[b] = [];
        double[] X = mesh.X, Y = mesh.Y, U = mesh.U, V = mesh.V;
        for (int t = 0; t < count; t++)
        {
            int a = tris[3 * t], b1 = tris[3 * t + 1], c = tris[3 * t + 2];
            double y0 = Math.Min(Y[a], Math.Min(Y[b1], Y[c])) - target.Top, y1 = Math.Max(Y[a], Math.Max(Y[b1], Y[c])) - target.Top;
            if (y1 < -1 || y0 > h + 1) continue;
            double x0 = Math.Min(X[a], Math.Min(X[b1], X[c])) - target.Left, x1 = Math.Max(X[a], Math.Max(X[b1], X[c])) - target.Left;
            if (x1 < -1 || x0 > w + 1) continue;
            int first = Math.Clamp((int)Math.Floor(y0 - 1) / bandHeight, 0, bands - 1), last = Math.Clamp((int)Math.Ceiling(y1 + 1) / bandHeight, 0, bands - 1);
            for (int band = first; band <= last; band++) lists[band].Add(t);
        }
        Parallel.For(0, bands, new ParallelOptions { CancellationToken = cancel }, band =>
        {
            var clip = new PixelRect(target.Left, target.Top + band * bandHeight, target.Right, Math.Min(target.Bottom, target.Top + (band + 1) * bandHeight));
            foreach (int t in lists[band])
            {
                int a = tris[3 * t], b1 = tris[3 * t + 1], c = tris[3 * t + 2];
                Triangle(X[a], Y[a], U[a], V[a], X[b1], Y[b1], U[b1], V[b1], X[c], Y[c], U[c], V[c], target, clip, at, jac, hit);
            }
        });

        // Pass 2: sample.
        bool cubic = filter == ResampleFilter.Bicubic;
        double support = cubic ? 2 : 1;
        int ch = source.Channels;
        float[] data = source.Data;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, () => new
        {
            Acc = new float[ch], Wx = new float[MaxTaps], Wy = new float[MaxTaps], Cols = new int[MaxTaps],
        }, (y, _, buf) =>
        {
            for (int x = 0; x < w; x++)
            {
                long p = (long)y * w + x;
                int i = (int)p;
                if (!hit[p])
                {
                    sink(i, buf.Acc, 1, 0);
                    continue;
                }
                double sx = at[p * 2], sy = at[p * 2 + 1];
                double j11 = jac[p * 4], j12 = jac[p * 4 + 1], j21 = jac[p * 4 + 2], j22 = jac[p * 4 + 3];
                double stepX = Math.Sqrt(j11 * j11 + j12 * j12), stepY = Math.Sqrt(j21 * j21 + j22 * j22);
                double cov = Resampler.EdgeCoverage(sx, sw, stepX) * Resampler.EdgeCoverage(sy, sh, stepY);
                if (cov <= 0)
                {
                    sink(i, buf.Acc, 1, 0);
                    continue;
                }
                double fx = Math.Max(1, stepX), fy = Math.Max(1, stepY);
                if (fx == 1 && fy == 1)
                {
                    sink(i, buf.Acc, PointSampler.Unscaled(source, sx, sy, cubic, buf.Acc), (float)cov);
                    continue;
                }
                double rx = support * fx, ry = support * fy;
                int x0 = (int)Math.Ceiling(sx - rx - 0.5), x1 = (int)Math.Floor(sx + rx - 0.5);
                int y0 = (int)Math.Ceiling(sy - ry - 0.5), y1 = (int)Math.Floor(sy + ry - 0.5);
                int tx = Math.Min(x1 - x0 + 1, MaxTaps), ty = Math.Min(y1 - y0 + 1, MaxTaps);
                float sumX = 0, sumY = 0;
                for (int k = 0; k < tx; k++)
                {
                    sumX += buf.Wx[k] = Resampler.Kernel((x0 + k + 0.5 - sx) / fx, cubic);
                    buf.Cols[k] = Math.Clamp(x0 + k, 0, sw - 1);
                }
                for (int k = 0; k < ty; k++) sumY += buf.Wy[k] = Resampler.Kernel((y0 + k + 0.5 - sy) / fy, cubic);
                Array.Clear(buf.Acc);
                for (int jj = 0; jj < ty; jj++)
                {
                    float wj = buf.Wy[jj];
                    if (wj == 0) continue;
                    long row = (long)Math.Clamp(y0 + jj, 0, sh - 1) * sw;
                    for (int ii = 0; ii < tx; ii++)
                    {
                        float wt = buf.Wx[ii] * wj;
                        if (wt == 0) continue;
                        long s = (row + buf.Cols[ii]) * ch;
                        for (int k = 0; k < ch; k++) buf.Acc[k] += data[s + k] * wt;
                    }
                }
                float weight = sumX * sumY;
                sink(i, buf.Acc, weight == 0 ? 1 : weight, (float)cov);
            }
            return buf;
        }, _ => { });
    }

    /// <summary>Rasterizes one affine piece: output pixel centers inside it (and inside <paramref name="clip"/>) get the source position by the piece's inverse.</summary>
    private static void Triangle(double ax, double ay, double au, double av, double bx, double by, double bu, double bv,
        double cx, double cy, double cu, double cv, PixelRect target, PixelRect clip, float[] at, float[] jac, bool[] hit)
    {
        double area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
        if (Math.Abs(area) < 1e-12) return;
        // Inverse affine: (u, v) = S · (x − ax, y − ay) + (au, av), with S = [du dv] · [e1 e2]⁻¹.
        double e1x = bx - ax, e1y = by - ay, e2x = cx - ax, e2y = cy - ay;
        double inv = 1 / area;
        double i11 = e2y * inv, i12 = -e2x * inv, i21 = -e1y * inv, i22 = e1x * inv;
        double du1 = bu - au, du2 = cu - au, dv1 = bv - av, dv2 = cv - av;
        double s11 = du1 * i11 + du2 * i21, s12 = du1 * i12 + du2 * i22;
        double s21 = dv1 * i11 + dv2 * i21, s22 = dv1 * i12 + dv2 * i22;

        int x0 = Math.Max(clip.Left, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)) - 0.5));
        int x1 = Math.Min(clip.Right - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx)) - 0.5));
        int y0 = Math.Max(clip.Top, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)) - 0.5));
        int y1 = Math.Min(clip.Bottom - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy)) - 0.5));
        if (x0 > x1 || y0 > y1) return;
        double sign = area > 0 ? 1 : -1;
        int w = target.Width;
        for (int py = y0; py <= y1; py++)
        {
            double yc = py + 0.5;
            for (int px = x0; px <= x1; px++)
            {
                double xc = px + 0.5;
                // Edge functions, with a half-open rule so shared edges belong to exactly one piece.
                double w0 = ((bx - ax) * (yc - ay) - (by - ay) * (xc - ax)) * sign;
                double w1 = ((cx - bx) * (yc - by) - (cy - by) * (xc - bx)) * sign;
                double w2 = ((ax - cx) * (yc - cy) - (ay - cy) * (xc - cx)) * sign;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                long p = (long)(py - target.Top) * w + (px - target.Left);
                double dx = xc - ax, dy = yc - ay;
                at[p * 2] = (float)(au + s11 * dx + s12 * dy);
                at[p * 2 + 1] = (float)(av + s21 * dx + s22 * dy);
                jac[p * 4] = (float)s11;
                jac[p * 4 + 1] = (float)s12;
                jac[p * 4 + 2] = (float)s21;
                jac[p * 4 + 3] = (float)s22;
                hit[p] = true;
            }
        }
    }
}
