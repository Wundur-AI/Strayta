using Strayta.Core;

namespace Strayta.Rendering.Transforms;

/// <summary>
/// Photoshop's named warp styles as meshes. Each style is a smooth map of the unit square (u across, v down) that the
/// bend (<see cref="WarpSpec.Value"/>) scales, followed by the horizontal and vertical distortions (a perspective-like
/// taper), turned a quarter for vertical warps; the result is fitted with one bicubic Bézier patch, the form
/// Photoshop shows and edits them in, by least squares over a dense grid of samples.
/// </summary>
public static class WarpStyles
{
    /// <summary>The style IDs this class draws.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "warpArc", "warpArcLower", "warpArcUpper", "warpArch", "warpBulge", "warpShellLower", "warpShellUpper", "warpFlag",
        "warpWave", "warpFish", "warpRise", "warpFisheye", "warpInflate", "warpSqueeze", "warpTwist",
    ];

    /// <summary>The one-patch mesh of a named style, or null for an unknown style.</summary>
    public static WarpMesh? Envelope(WarpSpec spec)
    {
        if (!Names.Contains(spec.Style)) return null;
        var (left, top, right, bottom) = spec.Bounds;
        double w = right - left, h = bottom - top;
        double b = Math.Clamp(spec.Value / 100.0, -1, 1);
        double ph = Math.Clamp(spec.Perspective / 100.0, -1, 1), pv = Math.Clamp(spec.PerspectiveOther / 100.0, -1, 1);
        bool vertical = spec.Vertical;
        // Styles are written for a horizontal bend in a box of aspect (w, h); vertical ones work on the transposed box.
        double bw = vertical ? h : w, bh = vertical ? w : h;
        (double X, double Y) Unit(double u, double v)
        {
            var (x, y) = Style(spec.Style, u, v, b, bw, bh);
            // Distortions: horizontal tapers the height along x, vertical tapers the width along y.
            double sy = 1 + ph * (x - 0.5), sx = 1 + pv * (y - 0.5);
            x = 0.5 + (x - 0.5) * sx;
            y = 0.5 + (y - 0.5) * sy;
            return (x, y);
        }
        (double X, double Y) Map(double u, double v)
        {
            if (!vertical)
            {
                var (x, y) = Unit(u, v);
                return (left + x * w, top + y * h);
            }
            var (tx, ty) = Unit(v, 1 - u); // transpose: the style's "across" runs down the content
            return (left + (1 - ty) * w, top + tx * h);
        }
        return WarpMesh.Single(spec.Bounds, Fit(Map));
    }

    /// <summary>
    /// One style on the unit square. <paramref name="b"/> is the bend (−1..1); <paramref name="w"/> and
    /// <paramref name="h"/> give the box's aspect, for styles that bend along circles.
    /// </summary>
    private static (double X, double Y) Style(string style, double u, double v, double b, double w, double h)
    {
        double s = Math.Sin(Math.PI * u), cu = u - 0.5, cv = v - 0.5;
        switch (style)
        {
            case "warpArc":
            {
                // Concentric arcs: the middle line becomes an arc of angle π·b with the same length.
                if (Math.Abs(b) < 1e-9) return (u, v);
                double theta = Math.PI * b, radius = w / theta; // in width units: radius / w = 1 / theta
                double r = radius + (0.5 - v) * h * Math.Sign(theta);
                double phi = cu * theta;
                double x = r * Math.Sin(phi), y = -(r * Math.Cos(phi) - radius);
                // Keep the arc's chord centered on the box.
                double sag = radius - radius * Math.Cos(theta / 2);
                return (0.5 + x / w, 0.5 + y * Math.Sign(theta) * Math.Sign(theta) / h + (theta > 0 ? sag / 2 / h : -sag / 2 / h));
            }
            case "warpArcLower": return (u, v + b * 0.5 * s * v);
            case "warpArcUpper": return (u, v - b * 0.5 * s * (1 - v));
            case "warpArch": return (u, v - b * 0.5 * s);
            case "warpBulge": return (u, v + b * 0.5 * s * (2 * v - 1));
            case "warpShellLower": return (0.5 + cu * (1 - 0.5 * b * v * (1 - s)), v + b * 0.5 * s * v);
            case "warpShellUpper": return (0.5 + cu * (1 - 0.5 * b * (1 - v) * (1 - s)), v - b * 0.5 * s * (1 - v));
            case "warpFlag": return (u, v - b * 0.25 * Math.Sin(2 * Math.PI * u));
            case "warpWave": return (u, v - b * 0.25 * Math.Sin(2 * Math.PI * u) * (1 - 2 * cv * 0.5));
            case "warpFish": return (u, v + b * 0.5 * s * (2 * v - 1) * (1 - u) * 2 * 0.5);
            case "warpRise": return (u, v - b * 0.5 * Math.Sin(Math.PI * cu));
            case "warpFisheye":
            {
                double r2 = Math.Min(1, 4 * (cu * cu + cv * cv));
                double k = 1 + b * 0.5 * (1 - r2);
                return (0.5 + cu * k, 0.5 + cv * k);
            }
            case "warpInflate": return (u + b * 0.25 * Math.Sin(Math.PI * v) * 2 * cu, v + b * 0.25 * s * 2 * cv);
            case "warpSqueeze": return (u - b * 0.25 * Math.Sin(Math.PI * v) * 2 * cu, v + b * 0.25 * s * 2 * cv);
            case "warpTwist":
            {
                double r = Math.Min(1, 2 * Math.Sqrt(cu * cu + cv * cv));
                double a = b * Math.PI / 2 * (1 - r);
                double ca = Math.Cos(a), sa = Math.Sin(a);
                return (0.5 + cu * ca - cv * sa, 0.5 + cu * sa + cv * ca);
            }
            default: return (u, v);
        }
    }

    /// <summary>Least-squares Bézier control points (row by row) for a map of the unit square.</summary>
    internal static (double X, double Y)[] Fit(Func<double, double, (double X, double Y)> map)
    {
        const int n = 17;
        var ata = new double[16, 16];
        var atx = new double[16];
        var aty = new double[16];
        Span<double> bu = stackalloc double[4], bv = stackalloc double[4], row = stackalloc double[16];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double u = i / (n - 1.0), v = j / (n - 1.0);
                Bernstein(u, bu);
                Bernstein(v, bv);
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) row[r * 4 + c] = bv[r] * bu[c];
                var (x, y) = map(u, v);
                for (int a = 0; a < 16; a++)
                {
                    atx[a] += row[a] * x;
                    aty[a] += row[a] * y;
                    for (int k = 0; k < 16; k++) ata[a, k] += row[a] * row[k];
                }
            }
        var px = Solve((double[,])ata.Clone(), atx);
        var py = Solve(ata, aty);
        return [.. Enumerable.Range(0, 16).Select(i => (px[i], py[i]))];
    }

    private static void Bernstein(double t, Span<double> b)
    {
        double s = 1 - t;
        b[0] = s * s * s;
        b[1] = 3 * s * s * t;
        b[2] = 3 * s * t * t;
        b[3] = t * t * t;
    }

    /// <summary>Gaussian elimination with partial pivoting.</summary>
    internal static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var x = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (pivot != col)
            {
                for (int k = 0; k < n; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
                (x[col], x[pivot]) = (x[pivot], x[col]);
            }
            double d = a[col, col];
            if (Math.Abs(d) < 1e-15) continue;
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r, col] / d;
                if (f == 0) continue;
                for (int k = col; k < n; k++) a[r, k] -= f * a[col, k];
                x[r] -= f * x[col];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double s = x[r];
            for (int k = r + 1; k < n; k++) s -= a[r, k] * x[k];
            x[r] = Math.Abs(a[r, r]) < 1e-15 ? 0 : s / a[r, r];
        }
        return x;
    }

    /// <summary>
    /// The cylinder warp ("warpCylinder", Photoshop 2021 and later) as the split mesh Photoshop converts it to. Its
    /// "warpValues" are the left, bottom, right and top of the cylinder's front in content space, then the top and bottom
    /// ellipses' minor-to-major axis ratios (negative bows up), then a seventh value (0.9 or 1 in the files seen) that
    /// changes nothing measurable here.
    /// <para>
    /// The mesh has four patches across and one down: a sliver at each side (0.62% of the width, collapsed onto the
    /// cylinder's edge: the content there has turned out of view) and a quarter turn on each side of the middle. Columns
    /// run straight down; rows are the ellipses' front halves, their depth interpolated from the top ellipse to the
    /// bottom one. The quarter-turn control points (0.54045 of the radius in from the middle across, 0.5274 and 1.01455 of
    /// the depth down) are the ones Photoshop writes when it converts a cylinder to a custom warp, and the depth is the
    /// ratio times the radius; redrawn this way the corpus' cylinders match Photoshop's pixels to within resampling.
    /// </para>
    /// </summary>
    public static WarpMesh? Cylinder((double Left, double Top, double Right, double Bottom) bounds, IReadOnlyList<double> values)
    {
        const double Across = 0.54045, Down1 = 0.5274, Down2 = 1.01455, Sliver = 0.0062421;
        double x0 = values[0], y1 = values[1], x1 = values[2], y0 = values[3];
        double cw = x1 - x0;
        if (!(Math.Abs(cw) > 1e-6) || !(Math.Abs(y1 - y0) > 1e-6) || !values.All(double.IsFinite)) return null;
        var (left, top, right, bottom) = bounds;
        double r = cw / 2, cx = (x0 + x1) / 2;
        double dt = values[4] * r, db = values[5] * r;
        double[] xs = [x0, x0, x0, x0, x0, cx - Across * r, cx, cx + Across * r, x1, x1, x1, x1, x1];
        double[] sag = [0, 0, 0, 0, Down1, Down2, 1, Down2, Down1, 0, 0, 0, 0];
        var points = new (double X, double Y)[4 * 13];
        for (int row = 0; row < 4; row++)
        {
            double t = row / 3.0;
            double ey = y0 + (y1 - y0) * t, d = dt + (db - dt) * t;
            for (int c = 0; c < 13; c++) points[row * 13 + c] = (xs[c], ey + d * sag[c]);
        }
        double w = right - left, e = Sliver * w;
        return new WarpMesh([left - 0.6, left + e, (left + right) / 2, right - e, right + 0.6], [top, bottom], points);
    }
}
