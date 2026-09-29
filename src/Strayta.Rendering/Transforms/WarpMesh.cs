using Strayta.Core;

namespace Strayta.Rendering.Transforms;

/// <summary>
/// A warp as a grid of bicubic Bézier patches in content space, the form Photoshop edits warps in: one patch of 4×4
/// control points for a plain custom warp and every named style, or several patches side by side (split, "quilt"
/// warps) whose extents are the slices. A content point inside patch (i, j) takes the parameters
/// u = (x − sliceX[i]) / (sliceX[i+1] − sliceX[i]) and v likewise, and lands at the Bernstein-weighted sum of that
/// patch's 16 control points. Points outside the slices extend the nearest patch's polynomial, so a little margin
/// around the content (for anti-aliased edges) maps smoothly.
/// </summary>
public sealed class WarpMesh
{
    private readonly double[] _sliceX, _sliceY;
    private readonly (double X, double Y)[] _points;
    private readonly int _columns;

    public WarpMesh(IReadOnlyList<double> slicesX, IReadOnlyList<double> slicesY, IReadOnlyList<(double X, double Y)> points)
    {
        int px = slicesX.Count - 1, py = slicesY.Count - 1;
        if (px < 1 || py < 1) throw new ArgumentException("A mesh needs at least one patch.");
        _columns = 3 * px + 1;
        if (points.Count != _columns * (3 * py + 1)) throw new ArgumentException($"Expected {_columns * (3 * py + 1)} control points, got {points.Count}.", nameof(points));
        _sliceX = [.. slicesX];
        _sliceY = [.. slicesY];
        _points = [.. points];
        for (int i = 0; i < px; i++) if (!(_sliceX[i + 1] > _sliceX[i])) throw new ArgumentException("Slices must increase.", nameof(slicesX));
        for (int j = 0; j < py; j++) if (!(_sliceY[j + 1] > _sliceY[j])) throw new ArgumentException("Slices must increase.", nameof(slicesY));
    }

    public IReadOnlyList<double> SlicesX => _sliceX;
    public IReadOnlyList<double> SlicesY => _sliceY;
    public IReadOnlyList<(double X, double Y)> Points => _points;

    /// <summary>
    /// The mesh for <paramref name="spec"/>, or null when it does not warp (or is not understood).
    /// </summary>
    public static WarpMesh? From(WarpSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var (left, top, right, bottom) = spec.Bounds;
        if (!(right > left) || !(bottom > top)) return null;
        switch (spec.Style)
        {
            case "warpNone":
                return null;
            case "warpCustom":
            {
                if (spec.Mesh is not { } mesh) return null;
                int rows = spec.Rows, cols = spec.Columns;
                if ((rows - 1) % 3 != 0 || (cols - 1) % 3 != 0 || mesh.Count != rows * cols) return null;
                var sx = spec.SlicesX is { } xs && xs.Count == (cols - 1) / 3 + 1 ? xs : Even(left, right, (cols - 1) / 3);
                var sy = spec.SlicesY is { } ys && ys.Count == (rows - 1) / 3 + 1 ? ys : Even(top, bottom, (rows - 1) / 3);
                return new WarpMesh(sx, sy, mesh);
            }
            case "warpCylinder":
                return spec.Values is { Count: >= 7 } v ? WarpStyles.Cylinder(spec.Bounds, v) : null;
            default:
                return WarpStyles.Envelope(spec);
        }
    }

    private static double[] Even(double a, double b, int n) => Enumerable.Range(0, n + 1).Select(i => a + (b - a) * i / n).ToArray();

    /// <summary>Where content point (<paramref name="x"/>, <paramref name="y"/>) lands.</summary>
    public (double X, double Y) Map(double x, double y)
    {
        int i = Patch(_sliceX, x), j = Patch(_sliceY, y);
        double u = (x - _sliceX[i]) / (_sliceX[i + 1] - _sliceX[i]);
        double v = (y - _sliceY[j]) / (_sliceY[j + 1] - _sliceY[j]);
        Span<double> bu = stackalloc double[4], bv = stackalloc double[4];
        Bernstein(u, bu);
        Bernstein(v, bv);
        double ox = 0, oy = 0;
        int row0 = 3 * j, col0 = 3 * i;
        for (int r = 0; r < 4; r++)
        {
            double wr = bv[r];
            int at = (row0 + r) * _columns + col0;
            for (int c = 0; c < 4; c++)
            {
                double w = wr * bu[c];
                var p = _points[at + c];
                ox += w * p.X;
                oy += w * p.Y;
            }
        }
        return (ox, oy);
    }

    private static int Patch(double[] slices, double t)
    {
        int n = slices.Length - 1;
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

    /// <summary>A single patch whose 16 control points are <paramref name="points"/> (row by row) over the given bounds.</summary>
    public static WarpMesh Single((double Left, double Top, double Right, double Bottom) bounds, IReadOnlyList<(double X, double Y)> points) =>
        new([bounds.Left, bounds.Right], [bounds.Top, bounds.Bottom], points);

    /// <summary>The identity mesh over the bounds (control points evenly spaced).</summary>
    public static (double X, double Y)[] Identity((double Left, double Top, double Right, double Bottom) b) =>
        [.. from r in Enumerable.Range(0, 4) from c in Enumerable.Range(0, 4)
            select (b.Left + (b.Right - b.Left) * c / 3.0, b.Top + (b.Bottom - b.Top) * r / 3.0)];
}
