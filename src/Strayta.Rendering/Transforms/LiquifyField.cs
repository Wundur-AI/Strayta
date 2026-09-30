using System.Runtime.Intrinsics;
using Strayta.Core;

namespace Strayta.Rendering.Transforms;

/// <summary>Liquify's tools (Filter › Liquify).</summary>
public enum LiquifyTool
{
    ForwardWarp,
    Reconstruct,
    Smooth,
    TwirlClockwise,
    Pucker,
    Bloat,
    PushLeft,
    FreezeMask,
    ThawMask,
}

/// <summary>A Liquify brush: size (diameter, document pixels), pressure and rate (0..100), density (0..100: how far toward its edge it stays strong).</summary>
public readonly record struct LiquifyBrush(double Size, double Pressure, double Density, double Rate)
{
    public static LiquifyBrush Default => new(100, 50, 50, 80);

    /// <summary>The brush's strength at <paramref name="distance"/> from its center (0..1 times pressure).</summary>
    public double Weight(double distance)
    {
        double r = Size / 2;
        if (distance >= r || r <= 0) return 0;
        double t = distance / r;
        // Density 100 keeps the brush strong almost to its edge; 0 concentrates it at the center.
        double k = 0.6 + (1 - Density / 100) * 3;
        return Math.Pow(1 - t * t, k) * Math.Clamp(Pressure / 100, 0, 1);
    }
}

/// <summary>
/// Liquify's distortion: a displacement field on a regular grid over the document (each node stores where the pixel
/// shown there comes from, as an offset), and a freeze mask that protects areas from the tools. Tools compose new
/// displacements with the existing field (a forward warp at p reads the old field at p minus the push), so strokes
/// build up as in Photoshop. The field is drawn at screen resolution for the preview, and applied at full resolution
/// by cutting the grid into triangles for <see cref="MeshResampler"/> (bicubic, area-filtered where it shrinks).
/// </summary>
public sealed class LiquifyField
{
    private float[] _dx, _dy;
    private readonly float[] _freeze;

    /// <param name="domain">The area the field covers (document pixels; the canvas).</param>
    /// <param name="spacing">Document pixels between grid nodes.</param>
    public LiquifyField(PixelRect domain, double spacing)
    {
        if (domain.IsEmpty || !(spacing > 0)) throw new ArgumentException("Liquify needs an area and a grid.");
        Domain = domain;
        Spacing = spacing;
        Columns = (int)Math.Ceiling(domain.Width / spacing) + 1;
        Rows = (int)Math.Ceiling(domain.Height / spacing) + 1;
        _dx = new float[Columns * Rows];
        _dy = new float[Columns * Rows];
        _freeze = new float[Columns * Rows];
    }

    public PixelRect Domain { get; }
    public double Spacing { get; }

    /// <summary>Grid nodes across and down.</summary>
    public int Columns { get; }
    public int Rows { get; }

    public IReadOnlyList<float> DisplacementX => _dx;
    public IReadOnlyList<float> DisplacementY => _dy;

    /// <summary>The freeze mask at each node (0 thawed .. 1 frozen).</summary>
    public IReadOnlyList<float> Freeze => _freeze;

    /// <summary>True when nothing is displaced.</summary>
    public bool IsIdentity
    {
        get
        {
            for (int i = 0; i < _dx.Length; i++)
                if (Math.Abs(_dx[i]) > 1e-4f || Math.Abs(_dy[i]) > 1e-4f) return false;
            return true;
        }
    }

    /// <summary>A copy of the displacement (for undo inside the dialog).</summary>
    public (float[] Dx, float[] Dy) Snapshot() => ((float[])_dx.Clone(), (float[])_dy.Clone());

    public void Restore((float[] Dx, float[] Dy) snapshot)
    {
        _dx = (float[])snapshot.Dx.Clone();
        _dy = (float[])snapshot.Dy.Clone();
    }

    private double NodeX(int c) => Domain.Left + c * Spacing;
    private double NodeY(int r) => Domain.Top + r * Spacing;

    /// <summary>The displacement at document point (x, y), bilinear between nodes (zero outside the grid).</summary>
    public (double Dx, double Dy) Sample(double x, double y) => Sample(_dx, _dy, x, y);

    private (double, double) Sample(float[] dx, float[] dy, double x, double y)
    {
        double gx = (x - Domain.Left) / Spacing, gy = (y - Domain.Top) / Spacing;
        if (gx < 0 || gy < 0 || gx > Columns - 1 || gy > Rows - 1) return (0, 0);
        int c = Math.Min((int)gx, Columns - 2), r = Math.Min((int)gy, Rows - 2);
        if (c < 0 || r < 0) return (dx[0], dy[0]);
        double tx = gx - c, ty = gy - r;
        int i = r * Columns + c;
        double a = dx[i] + (dx[i + 1] - dx[i]) * tx, b = dx[i + Columns] + (dx[i + Columns + 1] - dx[i + Columns]) * tx;
        double e = dy[i] + (dy[i + 1] - dy[i]) * tx, f = dy[i + Columns] + (dy[i + Columns + 1] - dy[i + Columns]) * tx;
        return (a + (b - a) * ty, e + (f - e) * ty);
    }

    /// <summary>Where the content shown at (x, y) comes from.</summary>
    public (double X, double Y) Source(double x, double y)
    {
        var (dx, dy) = Sample(x, y);
        return (x + dx, y + dy);
    }

    /// <summary>The nodes a brush at (cx, cy) reaches, with their weight (the freeze mask taken off).</summary>
    private void ForNodes(double cx, double cy, LiquifyBrush brush, Action<int, double, double, double> apply)
    {
        double r = brush.Size / 2;
        int c0 = Math.Max(0, (int)Math.Floor((cx - r - Domain.Left) / Spacing)), c1 = Math.Min(Columns - 1, (int)Math.Ceiling((cx + r - Domain.Left) / Spacing));
        int r0 = Math.Max(0, (int)Math.Floor((cy - r - Domain.Top) / Spacing)), r1 = Math.Min(Rows - 1, (int)Math.Ceiling((cy + r - Domain.Top) / Spacing));
        for (int row = r0; row <= r1; row++)
            for (int col = c0; col <= c1; col++)
            {
                double x = NodeX(col), y = NodeY(row);
                double w = brush.Weight(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)));
                int i = row * Columns + col;
                w *= 1 - _freeze[i];
                if (w > 0) apply(i, x, y, w);
            }
    }

    /// <summary>
    /// Moves the content under the brush: each node now shows what was at its position minus the push, so the pixels
    /// travel with the brush. Used by Forward Warp (push along the stroke) and Push Left (push across it).
    /// </summary>
    private void Push(double cx, double cy, double px, double py, LiquifyBrush brush) =>
        Remap(cx, cy, brush, (x, y, w) => (x - w * px, y - w * py));

    /// <summary>
    /// Remaps each node under the brush to show what the field showed at <paramref name="from"/>(node, weight). New
    /// values are all worked out from the old field before any is written.
    /// </summary>
    private void Remap(double cx, double cy, LiquifyBrush brush, Func<double, double, double, (double X, double Y)> from)
    {
        var changes = new List<(int Index, float Dx, float Dy)>();
        ForNodes(cx, cy, brush, (i, x, y, w) =>
        {
            var (sx, sy) = from(x, y, w);
            var (dx, dy) = Sample(_dx, _dy, sx, sy);
            changes.Add((i, (float)(sx + dx - x), (float)(sy + dy - y)));
        });
        foreach (var (i, dx, dy) in changes)
        {
            _dx[i] = dx;
            _dy[i] = dy;
        }
    }

    /// <summary>
    /// One application of <paramref name="tool"/>: a stroke segment from (ax, ay) to (bx, by) for the tools that follow
    /// the pointer, or one tick of the stationary ones at (bx, by). <paramref name="alt"/> reverses Twirl (counterclockwise)
    /// and Push Left (right).
    /// </summary>
    public void Apply(LiquifyTool tool, double ax, double ay, double bx, double by, LiquifyBrush brush, bool alt = false)
    {
        double rate = Math.Clamp(brush.Rate / 100, 0, 1);
        switch (tool)
        {
            case LiquifyTool.ForwardWarp:
                if (ax != bx || ay != by) Push(bx, by, bx - ax, by - ay, brush);
                break;
            case LiquifyTool.PushLeft:
            {
                double dx = bx - ax, dy = by - ay;
                // Left of the stroke's direction (y points down): moving right pushes up.
                if (dx != 0 || dy != 0) Push(bx, by, alt ? -dy : dy, alt ? dx : -dx, brush);
                break;
            }
            case LiquifyTool.TwirlClockwise:
            {
                double max = 0.12 * (0.2 + rate);
                double sign = alt ? -1 : 1;
                Remap(bx, by, brush, (x, y, w) =>
                {
                    // The node shows what was turned back (counterclockwise) from it, so content turns clockwise.
                    var (s, c) = Math.SinCos(-sign * max * w);
                    double rx = x - bx, ry = y - by;
                    return (bx + c * rx - s * ry, by + s * rx + c * ry);
                });
                break;
            }
            case LiquifyTool.Pucker or LiquifyTool.Bloat:
            {
                double k = 0.06 * (0.2 + rate) * (tool == LiquifyTool.Pucker ? 1 : -1);
                // Pucker shows what was farther out (content moves in); Bloat what was nearer.
                Remap(bx, by, brush, (x, y, w) => (bx + (1 + k * w) * (x - bx), by + (1 + k * w) * (y - by)));
                break;
            }
            case LiquifyTool.Reconstruct:
                ForNodes(bx, by, brush, (i, _, _, w) =>
                {
                    float keep = (float)(1 - Math.Min(1, 0.25 * (0.2 + rate) * w * 2));
                    _dx[i] *= keep;
                    _dy[i] *= keep;
                });
                break;
            case LiquifyTool.Smooth:
            {
                var changes = new List<(int Index, float Dx, float Dy)>();
                ForNodes(bx, by, brush, (i, _, _, w) =>
                {
                    int c = i % Columns, r = i / Columns;
                    double sx = 0, sy = 0;
                    int n = 0;
                    for (int dr = -2; dr <= 2; dr++)
                        for (int dc = -2; dc <= 2; dc++)
                        {
                            int rr = r + dr, cc = c + dc;
                            if (rr < 0 || cc < 0 || rr >= Rows || cc >= Columns) continue;
                            sx += _dx[rr * Columns + cc];
                            sy += _dy[rr * Columns + cc];
                            n++;
                        }
                    double t = Math.Min(1, (0.2 + rate) * w);
                    changes.Add((i, (float)(_dx[i] + (sx / n - _dx[i]) * t), (float)(_dy[i] + (sy / n - _dy[i]) * t)));
                });
                foreach (var (i, dx, dy) in changes)
                {
                    _dx[i] = dx;
                    _dy[i] = dy;
                }
                break;
            }
            case LiquifyTool.FreezeMask or LiquifyTool.ThawMask:
            {
                double r = brush.Size / 2;
                int c0 = Math.Max(0, (int)Math.Floor((bx - r - Domain.Left) / Spacing)), c1 = Math.Min(Columns - 1, (int)Math.Ceiling((bx + r - Domain.Left) / Spacing));
                int r0 = Math.Max(0, (int)Math.Floor((by - r - Domain.Top) / Spacing)), r1 = Math.Min(Rows - 1, (int)Math.Ceiling((by + r - Domain.Top) / Spacing));
                for (int row = r0; row <= r1; row++)
                    for (int col = c0; col <= c1; col++)
                    {
                        double x = NodeX(col), y = NodeY(row);
                        double w = brush.Weight(Math.Sqrt((x - bx) * (x - bx) + (y - by) * (y - by)));
                        int i = row * Columns + col;
                        _freeze[i] = (float)Math.Clamp(_freeze[i] + (tool == LiquifyTool.FreezeMask ? w : -w), 0, 1);
                    }
                break;
            }
        }
    }

    /// <summary>Restore All: no displacement (the freeze mask stays).</summary>
    public void RestoreAll()
    {
        Array.Clear(_dx);
        Array.Clear(_dy);
    }

    /// <summary>Clears the freeze mask (Thaw All).</summary>
    public void ThawAll() => Array.Clear(_freeze);

    /// <summary>
    /// The field as triangles for <see cref="MeshResampler"/>: each grid cell's two triangles, from where their corners
    /// read in a raster placed at <paramref name="sourceBounds"/> to the nodes themselves. Positions are scaled down by
    /// <paramref name="factor"/> for a preview.
    /// </summary>
    public TriangleMap ToTriangles(PixelRect sourceBounds, int factor = 1)
    {
        int n = Columns * Rows;
        double[] u = new double[n], v = new double[n], x = new double[n], y = new double[n];
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
            {
                int i = r * Columns + c;
                double nx = NodeX(c), ny = NodeY(r);
                x[i] = nx / factor;
                y[i] = ny / factor;
                u[i] = (nx + _dx[i]) / factor - sourceBounds.Left;
                v[i] = (ny + _dy[i]) / factor - sourceBounds.Top;
            }
        var tris = new int[(Columns - 1) * (Rows - 1) * 6];
        for (int r = 0, t = 0; r < Rows - 1; r++)
            for (int c = 0; c < Columns - 1; c++)
            {
                int k00 = r * Columns + c, k10 = k00 + 1, k01 = k00 + Columns, k11 = k01 + 1;
                tris[t++] = k00; tris[t++] = k10; tris[t++] = k11;
                tris[t++] = k00; tris[t++] = k11; tris[t++] = k01;
            }
        return new TriangleMap(u, v, x, y, tris);
    }

    /// <summary>
    /// Draws the liquified image for the preview: <paramref name="source"/> is the layer as premultiplied RGBA floats,
    /// <paramref name="width"/> × <paramref name="height"/> pixels covering the domain at <paramref name="scale"/>
    /// document pixels each; <paramref name="bgra"/> receives premultiplied BGRA bytes of the same size (bilinear).
    /// </summary>
    public void RenderPreview(float[] source, int width, int height, double scale, byte[] bgra)
    {
        float[] fdx = _dx, fdy = _dy;
        int columns = Columns, rows = Rows;
        double k = scale / Spacing; // field nodes per preview pixel
        Parallel.For(0, height, py =>
        {
            ref float src = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(source);
            double gy = (py + 0.5) * k;
            int r = Math.Clamp((int)gy, 0, rows - 2);
            float ty = (float)Math.Clamp(gy - r, 0, 1);
            for (int px = 0; px < width; px++)
            {
                // The displacement here (bilinear between nodes), in preview pixels.
                double gx = (px + 0.5) * k;
                int c = Math.Clamp((int)gx, 0, columns - 2);
                float tx = (float)Math.Clamp(gx - c, 0, 1);
                int i = r * columns + c;
                float dx = (fdx[i] * (1 - tx) + fdx[i + 1] * tx) * (1 - ty) + (fdx[i + columns] * (1 - tx) + fdx[i + columns + 1] * tx) * ty;
                float dy = (fdy[i] * (1 - tx) + fdy[i + 1] * tx) * (1 - ty) + (fdy[i + columns] * (1 - tx) + fdy[i + columns + 1] * tx) * ty;
                double sx = px + dx / scale, sy = py + dy / scale; // in source pixel indices (centers at whole numbers)

                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                float fx = (float)(sx - x0), fy = (float)(sy - y0);
                var sum = Vector128<float>.Zero;
                if (x0 >= 0 && y0 >= 0 && x0 + 1 < width && y0 + 1 < height)
                {
                    nuint o0 = (nuint)((y0 * width + x0) * 4), o1 = o0 + (nuint)(width * 4);
                    sum = (Vector128.LoadUnsafe(ref src, o0) * (1 - fx) + Vector128.LoadUnsafe(ref src, o0 + 4) * fx) * (1 - fy)
                        + (Vector128.LoadUnsafe(ref src, o1) * (1 - fx) + Vector128.LoadUnsafe(ref src, o1 + 4) * fx) * fy;
                }
                else
                {
                    for (int j = 0; j < 2; j++)
                        for (int q = 0; q < 2; q++)
                        {
                            int xx = x0 + q, yy = y0 + j;
                            if (xx < 0 || yy < 0 || xx >= width || yy >= height) continue;
                            float w = (q == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                            sum += Vector128.LoadUnsafe(ref src, (nuint)((yy * width + xx) * 4)) * w;
                        }
                }
                int o = (py * width + px) * 4;
                bgra[o] = (byte)Math.Clamp(sum.GetElement(2) * 255 + 0.5f, 0, 255);
                bgra[o + 1] = (byte)Math.Clamp(sum.GetElement(1) * 255 + 0.5f, 0, 255);
                bgra[o + 2] = (byte)Math.Clamp(sum.GetElement(0) * 255 + 0.5f, 0, 255);
                bgra[o + 3] = (byte)Math.Clamp(sum.GetElement(3) * 255 + 0.5f, 0, 255);
            }
        });
    }
}
