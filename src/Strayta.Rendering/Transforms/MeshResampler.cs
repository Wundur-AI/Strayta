using Strayta.Core;

namespace Strayta.Rendering.Transforms;

/// <summary>
/// Resamples pixels through an arbitrary smooth map (a warp followed by a perspective placement), with the same
/// quality rules as <see cref="ProjectiveResampler"/>: premultiplied Catmull-Rom (or tent) filtering whose kernel is
/// widened where the map shrinks, and anti-aliased edges from each output pixel's distance to the content's edge.
/// <para>
/// The map is only evaluated forward. The content (plus a margin for the anti-aliased edge) is cut into a fine grid of
/// triangles whose corners are mapped; each triangle is then an affine piece, rasterized in the output, so every output
/// pixel center gets its source position and the local reduction (the piece's inverse Jacobian). Pieces are a few
/// output pixels across, so the piecewise error is far below a pixel. Where a warp folds over itself, the later piece
/// (lower and further right in the content) wins.
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
        ArgumentNullException.ThrowIfNull(map);
        var source = ResampleSource.FromRaster(raster);
        int sw = source.Width, sh = source.Height;

        // A coarse look first: how large the content lands, for the margin and the grid's density.
        const int coarse = 16;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, longest = 0;
        var probe = new (double X, double Y)[(coarse + 1) * (coarse + 1)];
        for (int j = 0; j <= coarse; j++)
            for (int i = 0; i <= coarse; i++)
            {
                var p = map(sw * (double)i / coarse, sh * (double)j / coarse);
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return (null, PixelRect.Empty);
                probe[j * (coarse + 1) + i] = p;
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                if (i > 0) longest = Math.Max(longest, Dist(p, probe[j * (coarse + 1) + i - 1]) * coarse / sw);
                if (j > 0) longest = Math.Max(longest, Dist(p, probe[(j - 1) * (coarse + 1) + i]) * coarse / sh);
            }
        // longest: the largest output length of one content pixel (roughly). Margin: enough content pixels that the
        // anti-aliased edge (half an output pixel beyond the content) is inside the grid.
        double scale = Math.Max(longest, 1e-6);
        double margin = Math.Max(1.0, 1.0 / scale) + 1;
        if ((maxX - minX) > 1 << 20 || (maxY - minY) > 1 << 20) return (null, PixelRect.Empty);

        // Grid density: pieces about 3 output pixels across, at most 512 per side.
        int nx = Math.Clamp((int)Math.Ceiling((sw + 2 * margin) * scale / 3), 2, 512);
        int ny = Math.Clamp((int)Math.Ceiling((sh + 2 * margin) * scale / 3), 2, 512);
        var gx = new double[(nx + 1) * (ny + 1)];
        var gy = new double[(nx + 1) * (ny + 1)];
        var ux = new double[nx + 1];
        var vy = new double[ny + 1];
        for (int i = 0; i <= nx; i++) ux[i] = -margin + (sw + 2 * margin) * i / nx;
        for (int j = 0; j <= ny; j++) vy[j] = -margin + (sh + 2 * margin) * j / ny;
        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
        Parallel.For(0, ny + 1, j =>
        {
            for (int i = 0; i <= nx; i++)
            {
                var (x, y) = map(ux[i], vy[j]);
                gx[j * (nx + 1) + i] = x;
                gy[j * (nx + 1) + i] = y;
            }
        });
        for (int k = 0; k < gx.Length; k++)
        {
            if (!double.IsFinite(gx[k]) || !double.IsFinite(gy[k])) return (null, PixelRect.Empty);
            bx0 = Math.Min(bx0, gx[k]); bx1 = Math.Max(bx1, gx[k]);
            by0 = Math.Min(by0, gy[k]); by1 = Math.Max(by1, gy[k]);
        }
        var target = new PixelRect((int)Math.Floor(bx0), (int)Math.Floor(by0), (int)Math.Ceiling(bx1), (int)Math.Ceiling(by1));
        if (clip is { } c) target = target.Intersect(c);
        if (target.IsEmpty) return (null, PixelRect.Empty);
        int w = target.Width, h = target.Height;

        // Pass 1: rasterize the pieces, noting each output pixel's source position and inverse Jacobian.
        var at = new float[(long)w * h * 2];
        var jac = new float[(long)w * h * 4];
        var hit = new bool[(long)w * h];
        for (int j = 0; j < ny; j++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int i = 0; i < nx; i++)
            {
                int k00 = j * (nx + 1) + i, k10 = k00 + 1, k01 = k00 + nx + 1, k11 = k01 + 1;
                Triangle(gx[k00], gy[k00], ux[i], vy[j], gx[k10], gy[k10], ux[i + 1], vy[j], gx[k11], gy[k11], ux[i + 1], vy[j + 1], target, at, jac, hit);
                Triangle(gx[k00], gy[k00], ux[i], vy[j], gx[k11], gy[k11], ux[i + 1], vy[j + 1], gx[k01], gy[k01], ux[i], vy[j + 1], target, at, jac, hit);
            }
        }

        // Pass 2: sample.
        bool cubic = filter == ResampleFilter.Bicubic;
        double support = cubic ? 2 : 1;
        int ch = source.Channels, colors = ch - 1;
        float[] data = source.Data;
        var planes = Enumerable.Range(0, ch).Select(_ => Plane.Create(w, h, source.BitDepth)).ToArray();
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
                    for (int k = 0; k < ch; k++) Resampler.Store(planes[k], i, 0);
                    continue;
                }
                double sx = at[p * 2], sy = at[p * 2 + 1];
                double j11 = jac[p * 4], j12 = jac[p * 4 + 1], j21 = jac[p * 4 + 2], j22 = jac[p * 4 + 3];
                double stepX = Math.Sqrt(j11 * j11 + j12 * j12), stepY = Math.Sqrt(j21 * j21 + j22 * j22);
                double cov = Resampler.EdgeCoverage(sx, sw, stepX) * Resampler.EdgeCoverage(sy, sh, stepY);
                if (cov <= 0)
                {
                    for (int k = 0; k < ch; k++) Resampler.Store(planes[k], i, 0);
                    continue;
                }
                double fx = Math.Max(1, stepX), fy = Math.Max(1, stepY);
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
                float alphaSum = buf.Acc[colors];
                float a = Math.Clamp(alphaSum / weight, 0f, 1f) * (float)cov;
                float inv = alphaSum > 1e-9f ? 1f / alphaSum : 0f;
                for (int k = 0; k < colors; k++) Resampler.Store(planes[k], i, buf.Acc[k] * inv);
                Resampler.Store(planes[colors], i, a);
            }
            return buf;
        }, _ => { });
        return (new Raster(source.ColorMode, planes[..colors], planes[colors]), target);
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Rasterizes one affine piece: output pixel centers inside it get the source position by the piece's inverse.</summary>
    private static void Triangle(double ax, double ay, double au, double av, double bx, double by, double bu, double bv,
        double cx, double cy, double cu, double cv, PixelRect target, float[] at, float[] jac, bool[] hit)
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

        int x0 = Math.Max(target.Left, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)) - 0.5));
        int x1 = Math.Min(target.Right - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx)) - 0.5));
        int y0 = Math.Max(target.Top, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)) - 0.5));
        int y1 = Math.Min(target.Bottom - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy)) - 0.5));
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
