namespace Strayta.Rendering.Transforms;

/// <summary>
/// The geometry behind editing a warp by hand (Edit › Transform › Warp, Custom): fitting a style's envelope into its
/// box, re-splitting a mesh into another number of patches, finding where on the warped surface a point lies, and
/// moving control points so a grabbed point of the surface follows the pointer.
/// </summary>
public static class WarpEditing
{
    /// <summary>
    /// The control points of <paramref name="mesh"/> with their bounding box scaled onto (0, 0)–(<paramref name="width"/>,
    /// <paramref name="height"/>): how Photoshop frames a warp in its transform box (<see cref="SmartObjectPlacement"/>).
    /// </summary>
    public static (double X, double Y)[] Fitted(WarpMesh mesh, double width, double height) =>
        [.. SmartObjectPlacement.PlacedMesh(mesh, width, height, Projective.Identity).Points];

    /// <summary>Evenly spaced slices from <paramref name="a"/> to <paramref name="b"/> for <paramref name="n"/> patches.</summary>
    public static double[] Even(double a, double b, int n) => [.. Enumerable.Range(0, n + 1).Select(i => a + (b - a) * i / n)];

    /// <summary>The identity mesh over (0, 0)–(width, height) split into <paramref name="columns"/> × <paramref name="rows"/> patches.</summary>
    public static WarpMesh Identity(double width, double height, int columns, int rows)
    {
        var sx = Even(0, width, columns);
        var sy = Even(0, height, rows);
        int cols = 3 * columns + 1, rws = 3 * rows + 1;
        var points = new (double X, double Y)[cols * rws];
        for (int r = 0; r < rws; r++)
            for (int c = 0; c < cols; c++)
                points[r * cols + c] = (width * c / (cols - 1.0), height * r / (rws - 1.0));
        return new WarpMesh(sx, sy, points);
    }

    /// <summary>
    /// A mesh of <paramref name="columns"/> × <paramref name="rows"/> patches over (0, 0)–(width, height) that follows
    /// <paramref name="surface"/> (or the identity when null): each new patch is a least-squares fit of the surface over
    /// its area, and the points shared by neighboring patches are averaged so the patches stay joined.
    /// </summary>
    public static WarpMesh Resplit(WarpMesh? surface, double width, double height, int columns, int rows)
    {
        if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        var identity = Identity(width, height, columns, rows);
        if (surface is null) return identity;
        int cols = 3 * columns + 1, rws = 3 * rows + 1;
        var sum = new (double X, double Y)[cols * rws];
        var count = new int[cols * rws];
        var sx = identity.SlicesX;
        var sy = identity.SlicesY;
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < columns; i++)
            {
                double x0 = sx[i], x1 = sx[i + 1], y0 = sy[j], y1 = sy[j + 1];
                var patch = WarpStyles.Fit((u, v) => surface.Map(x0 + (x1 - x0) * u, y0 + (y1 - y0) * v));
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                    {
                        int k = (3 * j + r) * cols + 3 * i + c;
                        sum[k] = (sum[k].X + patch[r * 4 + c].X, sum[k].Y + patch[r * 4 + c].Y);
                        count[k]++;
                    }
            }
        var points = sum.Select((p, k) => (p.X / count[k], p.Y / count[k])).ToArray();
        return new WarpMesh(sx, sy, points);
    }

    /// <summary>
    /// The content position (in the mesh's slice space) whose warped image is nearest (<paramref name="x"/>,
    /// <paramref name="y"/>), or null when the point is not on the warped surface (farther than <paramref name="tolerance"/>).
    /// </summary>
    public static (double U, double V)? Locate(WarpMesh mesh, double x, double y, double tolerance)
    {
        var sx = mesh.SlicesX;
        var sy = mesh.SlicesY;
        double left = sx[0], right = sx[^1], top = sy[0], bottom = sy[^1];
        const int n = 48;
        double bestD = double.MaxValue, bu = 0, bv = 0;
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                double u = left + (right - left) * i / n, v = top + (bottom - top) * j / n;
                var p = mesh.Map(u, v);
                double d = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
                if (d < bestD) (bestD, bu, bv) = (d, u, v);
            }
        // Newton steps with a numeric Jacobian, kept inside the mesh.
        double h = Math.Max(right - left, bottom - top) * 1e-5;
        for (int it = 0; it < 12; it++)
        {
            var p = mesh.Map(bu, bv);
            double ex = x - p.X, ey = y - p.Y;
            if (ex * ex + ey * ey < 1e-8) break;
            var pu = mesh.Map(bu + h, bv);
            var pv = mesh.Map(bu, bv + h);
            double a = (pu.X - p.X) / h, b = (pv.X - p.X) / h, c = (pu.Y - p.Y) / h, d = (pv.Y - p.Y) / h;
            double det = a * d - b * c;
            if (Math.Abs(det) < 1e-12) break;
            bu = Math.Clamp(bu + (d * ex - b * ey) / det, left, right);
            bv = Math.Clamp(bv + (-c * ex + a * ey) / det, top, bottom);
        }
        var q = mesh.Map(bu, bv);
        return Math.Sqrt((q.X - x) * (q.X - x) + (q.Y - y) * (q.Y - y)) <= tolerance ? (bu, bv) : null;
    }

    /// <summary>
    /// Control points moved so that the surface point at content position (<paramref name="u"/>, <paramref name="v"/>)
    /// lands on (<paramref name="x"/>, <paramref name="y"/>): the smallest change to the 16 points of its patch, each
    /// moved in proportion to its Bernstein weight there (so the points nearest the grab move most).
    /// </summary>
    public static (double X, double Y)[] DragSurface(WarpMesh mesh, double u, double v, double x, double y)
    {
        var sx = mesh.SlicesX;
        var sy = mesh.SlicesY;
        int i = Patch(sx, u), j = Patch(sy, v);
        double pu = (u - sx[i]) / (sx[i + 1] - sx[i]), pv = (v - sy[j]) / (sy[j + 1] - sy[j]);
        Span<double> bu = stackalloc double[4], bv = stackalloc double[4];
        Bernstein(pu, bu);
        Bernstein(pv, bv);
        var now = mesh.Map(u, v);
        // Each point's share is b³ / Σb⁴ (the smallest change when moving a point costs 1 / b²): the grabbed point lands
        // exactly, and points with little say there (the far corners) hardly move.
        double dx = x - now.X, dy = y - now.Y, norm = 0;
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) norm += Math.Pow(bv[r] * bu[c], 4);
        var points = mesh.Points.ToArray();
        int columns = 3 * (sx.Count - 1) + 1;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                double w = Math.Pow(bv[r] * bu[c], 3) / norm;
                int k = (3 * j + r) * columns + 3 * i + c;
                points[k] = (points[k].X + w * dx, points[k].Y + w * dy);
            }
        return points;
    }

    private static int Patch(IReadOnlyList<double> slices, double t)
    {
        int n = slices.Count - 1;
        for (int i = 0; i < n - 1; i++)
            if (t < slices[i + 1]) return i;
        return n - 1;
    }

    private static void Bernstein(double t, Span<double> b)
    {
        double s = 1 - t;
        b[0] = s * s * s;
        b[1] = 3 * s * s * t;
        b[2] = 3 * s * t * t;
        b[3] = t * t * t;
    }
}
