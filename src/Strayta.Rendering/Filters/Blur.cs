using System.Buffers;

namespace Strayta.Rendering.Filters;

/// <summary>
/// Blur kernels on float planes, all with the image's edge pixels repeated beyond its border (so an opaque image never
/// fades at the edges). Every blur is separable or reduced to rows: each row is copied into a padded buffer, filtered,
/// and copied back; columns are done as rows of the transposed plane, which keeps memory access sequential.
/// </summary>
/// <remarks>
/// Gaussian blur costs the same for any radius: below σ = 2 it is a direct convolution with the sampled Gaussian, above
/// it a recursive filter (<see cref="RecursiveGaussian"/>) whose profile is within 0.5% of the true Gaussian's peak at any
/// σ up to 1000. Very small σ (under 0.8, where a sampled Gaussian's variance falls short of σ²) uses a sampled kernel
/// widened until its variance is σ², so radii like 0.3 still soften a little, as they do in Photoshop. Box and motion
/// blurs use running sums, with a fractionally weighted outer tap so any width is possible.
/// </remarks>
internal static class Blur
{
    /// <summary>Below this σ a Gaussian leaves pixels unchanged at 16 bits.</summary>
    private const double NoBlurSigma = 0.02;

    /// <summary>How far a Gaussian of <paramref name="sigma"/> reads (both methods stay inside this).</summary>
    public static int GaussianReach(double sigma) => sigma < NoBlurSigma ? 0 : (int)Math.Ceiling(3.5 * sigma) + 4;

    public static void Gaussian(float[] plane, int w, int h, double sigma, CancellationToken cancel)
    {
        if (sigma < NoBlurSigma) return;
        var kernel = sigma < 2 ? SampledKernel.ForSigma(sigma) : (RowKernel)new RecursiveGaussian(sigma);
        Separable(plane, w, h, kernel, cancel);
    }

    /// <summary>Box average over [-halfWidth, halfWidth] in both directions (0.5 = no change).</summary>
    public static void Box(float[] plane, int w, int h, double halfWidth, CancellationToken cancel)
    {
        if (halfWidth <= 0.5 + 1e-9) return;
        Separable(plane, w, h, BoxCascade.FromHalfWidth(halfWidth, 1), cancel);
    }

    /// <summary>
    /// Averages along a line <paramref name="distance"/> pixels long through each pixel, at <paramref name="angle"/>
    /// degrees counterclockwise from horizontal.
    /// </summary>
    /// <remarks>
    /// The plane is sheared so the line runs along rows (columns shift by a fraction of a pixel each, with linear
    /// interpolation), each row gets a one-dimensional box blur, and every output pixel reads the blurred rows back
    /// along its own line. Steep angles transpose first, so the shear never exceeds 45°. The cost is independent of
    /// the distance.
    /// </remarks>
    public static void Motion(float[] plane, int w, int h, double angle, double distance, CancellationToken cancel)
    {
        double rad = angle * Math.PI / 180;
        double dx = Math.Cos(rad), dy = -Math.Sin(rad); // image rows grow downwards
        bool steep = Math.Abs(dy) > Math.Abs(dx);
        if (steep) (dx, dy) = (dy, dx);
        double halfWidth = distance * Math.Abs(dx) / 2;
        if (halfWidth <= 0.5 + 1e-9) return;
        double slope = dy / dx;

        if (!steep)
        {
            ShearBlur(plane, w, h, slope, halfWidth, cancel);
            return;
        }
        var t = ArrayPool<float>.Shared.Rent(w * h);
        try
        {
            Transpose(plane, w, h, t, cancel);
            ShearBlur(t, h, w, slope, halfWidth, cancel);
            Transpose(t, h, w, plane, cancel);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(t);
        }
    }

    private static void ShearBlur(float[] p, int w, int h, double slope, double halfWidth, CancellationToken cancel)
    {
        var box = BoxCascade.FromHalfWidth(halfWidth, 1);
        // Sheared row j holds the pixels (x, j + x·slope); output (x, y) lies on sheared row y - x·slope.
        double minShift = Math.Min(0, (w - 1) * slope), maxShift = Math.Max(0, (w - 1) * slope);
        int j0 = (int)Math.Floor(-maxShift) - 1, j1 = (int)Math.Ceiling(h - 1 - minShift) + 2;
        int rows = j1 - j0;
        var sheared = ArrayPool<float>.Shared.Rent(rows * w);
        try
        {
            var options = new ParallelOptions { CancellationToken = cancel };
            int len = w + 2 * box.Pad;
            Parallel.For(0, rows, options, () => (new float[len], new float[len]), (row, _, bufs) =>
            {
                var (a, b) = bufs;
                double j = j0 + row;
                for (int x = 0; x < w; x++)
                {
                    double y = j + x * slope;
                    int y0 = (int)Math.Floor(y);
                    float f = (float)(y - y0);
                    int ya = Math.Clamp(y0, 0, h - 1), yb = Math.Clamp(y0 + 1, 0, h - 1);
                    a[box.Pad + x] = p[ya * w + x] * (1 - f) + p[yb * w + x] * f;
                }
                PadEdges(a, w, box.Pad);
                var result = box.Run(a, b, len);
                Array.Copy(result, box.Pad, sheared, row * w, w);
                return bufs;
            }, _ => { });

            Parallel.For(0, h, options, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double jj = y - x * slope - j0;
                    int jf = (int)Math.Floor(jj);
                    float g = (float)(jj - jf);
                    p[y * w + x] = sheared[jf * w + x] * (1 - g) + sheared[(jf + 1) * w + x] * g;
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(sheared);
        }
    }

    // ---- Separable filtering ------------------------------------------------------------------------

    /// <summary>Filters rows, then columns (as rows of the transposed plane).</summary>
    private static void Separable(float[] plane, int w, int h, RowKernel kernel, CancellationToken cancel)
    {
        Rows(plane, w, h, kernel, cancel);
        var t = ArrayPool<float>.Shared.Rent(w * h);
        try
        {
            Transpose(plane, w, h, t, cancel);
            Rows(t, h, w, kernel, cancel);
            Transpose(t, h, w, plane, cancel);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(t);
        }
    }

    private static void Rows(float[] plane, int w, int h, RowKernel kernel, CancellationToken cancel)
    {
        int pad = kernel.Pad, len = w + 2 * pad;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, () => (new float[len], new float[len]), (y, _, bufs) =>
        {
            var (a, b) = bufs;
            Array.Copy(plane, y * w, a, pad, w);
            PadEdges(a, w, pad);
            var result = kernel.Run(a, b, len);
            Array.Copy(result, pad, plane, y * w, w);
            return bufs;
        }, _ => { });
    }

    /// <summary>Repeats the first and last of the <paramref name="n"/> values at <c>buf[pad..]</c> into the padding.</summary>
    private static void PadEdges(float[] buf, int n, int pad)
    {
        buf.AsSpan(0, pad).Fill(buf[pad]);
        buf.AsSpan(pad + n, pad).Fill(buf[pad + n - 1]);
    }

    /// <summary>dst (h rows of w) becomes src transposed: dst[x·h + y] = src[y·w + x].</summary>
    internal static void Transpose(float[] src, int w, int h, float[] dst, CancellationToken cancel)
    {
        const int block = 64;
        int blocksY = (h + block - 1) / block;
        Parallel.For(0, blocksY, new ParallelOptions { CancellationToken = cancel }, by =>
        {
            int y0 = by * block, y1 = Math.Min(h, y0 + block);
            for (int x0 = 0; x0 < w; x0 += block)
            {
                int x1 = Math.Min(w, x0 + block);
                for (int y = y0; y < y1; y++)
                {
                    int row = y * w;
                    for (int x = x0; x < x1; x++) dst[x * h + y] = src[row + x];
                }
            }
        });
    }

    // ---- Row kernels --------------------------------------------------------------------------------

    /// <summary>A one-dimensional filter over a padded row.</summary>
    private abstract class RowKernel
    {
        /// <summary>Padding needed on each side: the result is exact in [Pad, len - Pad).</summary>
        public abstract int Pad { get; }

        /// <summary>Filters <paramref name="a"/> (length <paramref name="len"/>), using <paramref name="b"/> as scratch; returns the buffer holding the result.</summary>
        public abstract float[] Run(float[] a, float[] b, int len);
    }

    /// <summary>Direct convolution with a normalized, sampled Gaussian.</summary>
    private sealed class SampledKernel : RowKernel
    {
        private readonly float[] _weights;
        private readonly int _radius;

        private SampledKernel(double sigma)
        {
            _radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
            var w = Weights(sigma, _radius);
            _weights = w.Select(v => (float)v).ToArray();
        }

        public override int Pad => _radius;

        /// <summary>A kernel whose variance is <paramref name="sigma"/>², widening the sampled Gaussian for small σ.</summary>
        public static SampledKernel ForSigma(double sigma)
        {
            if (sigma >= 0.8) return new SampledKernel(sigma);
            // Sampling a narrow Gaussian at whole pixels loses most of its tails; find the σ' whose samples have
            // variance σ² (variance grows monotonically with σ').
            double target = sigma * sigma, lo = 0.01, hi = 1.0;
            for (int i = 0; i < 50; i++)
            {
                double mid = (lo + hi) / 2;
                if (Variance(mid) < target) lo = mid;
                else hi = mid;
            }
            return new SampledKernel((lo + hi) / 2);
        }

        private static double[] Weights(double sigma, int radius)
        {
            var w = new double[2 * radius + 1];
            double sum = 0;
            for (int i = -radius; i <= radius; i++) sum += w[i + radius] = Math.Exp(-i * i / (2 * sigma * sigma));
            for (int i = 0; i < w.Length; i++) w[i] /= sum;
            return w;
        }

        private static double Variance(double sigma)
        {
            int r = Math.Max(1, (int)Math.Ceiling(3 * sigma));
            var w = Weights(sigma, r);
            double v = 0;
            for (int i = -r; i <= r; i++) v += w[i + r] * i * i;
            return v;
        }

        public override float[] Run(float[] a, float[] b, int len)
        {
            int r = _radius;
            var k = _weights;
            for (int i = r; i < len - r; i++)
            {
                float s = 0;
                for (int t = 0; t < k.Length; t++) s += k[t] * a[i - r + t];
                b[i] = s;
            }
            return b;
        }
    }

    /// <summary>
    /// A recursive (IIR) Gaussian: a causal and an anti-causal fourth-order filter whose impulse responses add up to
    /// Deriche's approximation of the Gaussian (two damped cosines per side, from his 1993 report "Recursively
    /// implementing the Gaussian and its derivatives"), accurate to a fraction of a percent of the peak. The recurrence
    /// coefficients are derived here from the approximation itself (poles and first samples), and the result is
    /// normalized to keep brightness exactly. Beyond the row's ends the edge values repeat forever, so each pass starts
    /// in the steady state for that constant: exact edge handling with no padding.
    /// </summary>
    private sealed class RecursiveGaussian : RowKernel
    {
        // Deriche's fit of exp(-x²/2σ²) for x ≥ 0, in units of σ.
        private const double A0 = 1.680, A1 = 3.735, B0 = 1.783, W0 = 0.6318, C0 = -0.6803, C1 = -0.2598, B1 = 1.723, W1 = 1.997;

        private readonly double _n0, _n1, _n2, _n3, _m1, _m2, _m3, _m4, _d1, _d2, _d3, _d4, _causalGain, _anticausalGain;

        public RecursiveGaussian(double sigma)
        {
            double H(double x) =>
                (A0 * Math.Cos(W0 * x / sigma) + A1 * Math.Sin(W0 * x / sigma)) * Math.Exp(-B0 * x / sigma)
                + (C0 * Math.Cos(W1 * x / sigma) + C1 * Math.Sin(W1 * x / sigma)) * Math.Exp(-B1 * x / sigma);

            // Denominator: the two conjugate pole pairs e^{(-b ± iw)/σ}.
            double r0 = Math.Exp(-B0 / sigma), r1 = Math.Exp(-B1 / sigma);
            double p1 = -2 * r0 * Math.Cos(W0 / sigma), p2 = r0 * r0, q1 = -2 * r1 * Math.Cos(W1 / sigma), q2 = r1 * r1;
            double[] d = [1, p1 + q1, p2 + p1 * q1 + q2, p1 * q2 + p2 * q1, p2 * q2];
            // Numerators: the samples of the response times the denominator, up to where the recurrence takes over.
            var h = new double[5];
            for (int k = 0; k < 5; k++) h[k] = H(k);
            var n = new double[4];
            for (int k = 0; k < 4; k++)
                for (int j = 0; j <= k; j++) n[k] += d[j] * h[k - j];
            var m = new double[5];
            for (int k = 1; k <= 4; k++)
                for (int j = 0; j < k; j++) m[k] += d[j] * h[k - j];

            double dsum = d.Sum();
            double causal = n.Sum() / dsum, anticausal = (m[1] + m[2] + m[3] + m[4]) / dsum;
            double norm = 1 / (causal + anticausal);
            (_n0, _n1, _n2, _n3) = (n[0] * norm, n[1] * norm, n[2] * norm, n[3] * norm);
            (_m1, _m2, _m3, _m4) = (m[1] * norm, m[2] * norm, m[3] * norm, m[4] * norm);
            (_d1, _d2, _d3, _d4) = (d[1], d[2], d[3], d[4]);
            _causalGain = causal * norm;
            _anticausalGain = anticausal * norm;
        }

        public override int Pad => 0;

        public override float[] Run(float[] a, float[] b, int len)
        {
            // Causal: y[i] = n0 x[i] + n1 x[i-1] + n2 x[i-2] + n3 x[i-3] - d1 y[i-1] - ... - d4 y[i-4].
            double x0 = a[0], y0 = x0 * _causalGain;
            double x1 = x0, x2 = x0, x3 = x0, y1 = y0, y2 = y0, y3 = y0, y4 = y0;
            for (int i = 0; i < len; i++)
            {
                double x = a[i];
                double y = _n0 * x + _n1 * x1 + _n2 * x2 + _n3 * x3 - _d1 * y1 - _d2 * y2 - _d3 * y3 - _d4 * y4;
                b[i] = (float)y;
                (x3, x2, x1) = (x2, x1, x);
                (y4, y3, y2, y1) = (y3, y2, y1, y);
            }
            // Anti-causal: y[i] = m1 x[i+1] + ... + m4 x[i+4] - d1 y[i+1] - ... - d4 y[i+4], added to the causal part.
            double xe = a[len - 1], ye = xe * _anticausalGain;
            x1 = x2 = x3 = xe;
            double x4 = xe;
            y1 = y2 = y3 = y4 = ye;
            for (int i = len - 1; i >= 0; i--)
            {
                double y = _m1 * x1 + _m2 * x2 + _m3 * x3 + _m4 * x4 - _d1 * y1 - _d2 * y2 - _d3 * y3 - _d4 * y4;
                (x4, x3, x2, x1) = (x3, x2, x1, a[i]);
                (y4, y3, y2, y1) = (y3, y2, y1, y);
                b[i] += (float)y;
            }
            return b;
        }
    }

    /// <summary>
    /// <see cref="Passes"/> box blurs in a row, each averaging 2r + 1 pixels plus the next pixel on either side at
    /// weight <see cref="Fraction"/> (0..1), so the box's width, and its variance, can take any value.
    /// </summary>
    private sealed class BoxCascade : RowKernel
    {
        private BoxCascade(int radius, double fraction, int passes)
        {
            Radius = radius;
            Fraction = fraction;
            Passes = passes;
        }

        /// <summary>A box covering [-halfWidth, halfWidth] around each pixel (halfWidth ≥ 0.5).</summary>
        public static BoxCascade FromHalfWidth(double halfWidth, int passes)
        {
            int r = Math.Max(0, (int)Math.Floor(halfWidth - 0.5));
            return new BoxCascade(r, Math.Clamp(halfWidth - 0.5 - r, 0, 1), passes);
        }

        public int Radius { get; }
        public double Fraction { get; }
        public int Passes { get; }

        public override int Pad => Passes * (Radius + 1);

        public override float[] Run(float[] a, float[] b, int len)
        {
            int r = Radius;
            double alpha = Fraction, norm = 1 / (2 * r + 1 + 2 * alpha);
            for (int pass = 1; pass <= Passes; pass++)
            {
                // Each pass is exact only where the previous one was: the valid range shrinks by r + 1 per side.
                int lo = pass * (r + 1), hi = len - pass * (r + 1);
                if (hi > lo)
                {
                    double sum = 0;
                    for (int k = lo - r; k <= lo + r; k++) sum += a[k];
                    for (int i = lo; i < hi; i++)
                    {
                        b[i] = (float)((sum + alpha * (a[i - r - 1] + a[i + r + 1])) * norm);
                        sum += a[i + r + 1] - a[i - r];
                    }
                }
                (a, b) = (b, a);
            }
            return a;
        }
    }
}
