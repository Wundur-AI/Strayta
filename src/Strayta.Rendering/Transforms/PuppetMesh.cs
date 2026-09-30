using Strayta.Core;

namespace Strayta.Rendering.Transforms;

/// <summary>How dense Puppet Warp's mesh is (Photoshop's Density: Fewer Points, Normal, More Points).</summary>
public enum PuppetDensity
{
    Fewer,
    Normal,
    More,
}

/// <summary>
/// The triangle mesh Puppet Warp deforms: a grid of cells over the layer's opaque pixels (grown by an expansion), each
/// covered cell cut into two triangles along alternating diagonals. Positions are document pixels. The mesh keeps the
/// layer's outline to within a cell; transparent pixels outside it are not drawn after the warp, as in Photoshop.
/// </summary>
public sealed class PuppetMesh
{
    private readonly int _columns, _rows;
    private readonly double _left, _top, _spacing;
    private readonly int[] _cellTriangle; // first triangle of each cell, -1 when the cell is not covered

    private PuppetMesh(double[] x, double[] y, int[] triangles, int[] vertexOfCorner, int columns, int rows, double left, double top, double spacing, int[] cellTriangle)
    {
        X = x;
        Y = y;
        Triangles = triangles;
        _columns = columns;
        _rows = rows;
        _left = left;
        _top = top;
        _spacing = spacing;
        _cellTriangle = cellTriangle;
        CornerVertex = vertexOfCorner;
        Neighbors = BuildNeighbors(x.Length, triangles);
    }

    /// <summary>Rest positions (document pixels).</summary>
    public double[] X { get; }
    public double[] Y { get; }

    /// <summary>Vertex indices, three per triangle.</summary>
    public int[] Triangles { get; }

    /// <summary>Each vertex's neighbors along triangle edges.</summary>
    public int[][] Neighbors { get; }

    /// <summary>Grid corner (row-major over (columns + 1) × (rows + 1)) to vertex index, -1 when unused.</summary>
    internal int[] CornerVertex { get; }

    /// <summary>Distance between grid lines, in pixels.</summary>
    public double Spacing => _spacing;

    public int VertexCount => X.Length;

    /// <summary>
    /// The mesh over the pixels of <paramref name="alpha"/> (a layer's alpha or coverage, placed at
    /// <paramref name="bounds"/>) above <paramref name="threshold"/> (0..1), grown by <paramref name="expansion"/>
    /// pixels. Null when nothing is opaque.
    /// </summary>
    public static PuppetMesh? Build(Plane? alpha, PixelRect bounds, PuppetDensity density, double expansion, float threshold = 0.02f)
    {
        if (bounds.IsEmpty) return null;
        double size = Math.Max(bounds.Width, bounds.Height);
        double spacing = Math.Max(6, size / density switch { PuppetDensity.Fewer => 12.0, PuppetDensity.More => 36.0, _ => 22.0 });
        double grow = Math.Max(0, expansion);
        double left = bounds.Left - grow - spacing, top = bounds.Top - grow - spacing;
        int columns = (int)Math.Ceiling((bounds.Width + 2 * (grow + spacing)) / spacing);
        int rows = (int)Math.Ceiling((bounds.Height + 2 * (grow + spacing)) / spacing);
        var covered = new bool[columns * rows];
        int w = bounds.Width, h = bounds.Height;
        // Runs of opaque pixels per row, grown by the expansion, mark every cell they touch.
        Parallel.For(0, h, y =>
        {
            double py0 = bounds.Top + y - grow - top, py1 = bounds.Top + y + 1 + grow - top;
            int r0 = Math.Max(0, (int)(py0 / spacing)), r1 = Math.Min(rows - 1, (int)((py1 - 1e-9) / spacing));
            int x = 0;
            while (x < w)
            {
                while (x < w && alpha is not null && alpha.GetNormalized(y * w + x) <= threshold) x++;
                if (x >= w) break;
                int start = x;
                while (x < w && (alpha is null || alpha.GetNormalized(y * w + x) > threshold)) x++;
                double px0 = bounds.Left + start - grow - left, px1 = bounds.Left + x + grow - left;
                int c0 = Math.Max(0, (int)(px0 / spacing)), c1 = Math.Min(columns - 1, (int)((px1 - 1e-9) / spacing));
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++) covered[r * columns + c] = true; // only ever set to true: safe across rows
            }
        });
        return FromCells(covered, columns, rows, left, top, spacing);
    }

    private static PuppetMesh? FromCells(bool[] covered, int columns, int rows, double left, double top, double spacing)
    {
        var vertex = new int[(columns + 1) * (rows + 1)];
        Array.Fill(vertex, -1);
        var xs = new List<double>();
        var ys = new List<double>();
        var tris = new List<int>();
        var cellTriangle = new int[columns * rows];
        Array.Fill(cellTriangle, -1);
        int V(int r, int c)
        {
            int k = r * (columns + 1) + c;
            if (vertex[k] < 0)
            {
                vertex[k] = xs.Count;
                xs.Add(left + c * spacing);
                ys.Add(top + r * spacing);
            }
            return vertex[k];
        }
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                if (!covered[r * columns + c]) continue;
                int a = V(r, c), b = V(r, c + 1), d = V(r + 1, c), e = V(r + 1, c + 1);
                cellTriangle[r * columns + c] = tris.Count / 3;
                // Alternate the diagonal so the mesh bends the same way in every direction.
                if ((r + c) % 2 == 0) tris.AddRange([a, b, e, a, e, d]);
                else tris.AddRange([a, b, d, b, e, d]);
            }
        if (tris.Count == 0) return null;
        return new PuppetMesh([.. xs], [.. ys], [.. tris], vertex, columns, rows, left, top, spacing, cellTriangle);
    }

    private static int[][] BuildNeighbors(int n, int[] tris)
    {
        var sets = new HashSet<int>[n];
        for (int i = 0; i < n; i++) sets[i] = [];
        for (int t = 0; t < tris.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = tris[t + k], b = tris[t + (k + 1) % 3];
                sets[a].Add(b);
                sets[b].Add(a);
            }
        return sets.Select(s => s.Order().ToArray()).ToArray();
    }

    /// <summary>The vertex nearest (x, y) among covered cells within <paramref name="maxDistance"/>, or -1.</summary>
    public int NearestVertex(double x, double y, double maxDistance)
    {
        int best = -1;
        double bestD = maxDistance * maxDistance;
        for (int i = 0; i < X.Length; i++)
        {
            double d = (X[i] - x) * (X[i] - x) + (Y[i] - y) * (Y[i] - y);
            if (d <= bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>True when (x, y) lies in a covered cell (on the mesh).</summary>
    public bool Contains(double x, double y)
    {
        int c = (int)Math.Floor((x - _left) / _spacing), r = (int)Math.Floor((y - _top) / _spacing);
        return c >= 0 && r >= 0 && c < _columns && r < _rows && _cellTriangle[r * _columns + c] >= 0;
    }

    /// <summary>
    /// The piecewise-affine map of the deformation: triangles from rest positions (in the pixels of a raster placed at
    /// <paramref name="bounds"/>, both scaled down by <paramref name="factor"/> for a preview) to the deformed positions.
    /// </summary>
    public TriangleMap Map(double[] deformedX, double[] deformedY, PixelRect bounds, int factor = 1)
    {
        int n = X.Length;
        double[] u = new double[n], v = new double[n], x = new double[n], y = new double[n];
        for (int i = 0; i < n; i++)
        {
            u[i] = X[i] / factor - bounds.Left;
            v[i] = Y[i] / factor - bounds.Top;
            x[i] = deformedX[i] / factor;
            y[i] = deformedY[i] / factor;
        }
        return new TriangleMap(u, v, x, y, Triangles);
    }
}

/// <summary>
/// As-rigid-as-possible deformation of a <see cref="PuppetMesh"/> with pinned vertices, after Sorkine and Alexa,
/// "As-Rigid-As-Possible Surface Modeling" (SGP 2007), in the plane: alternately (1) the best rotation of each vertex's
/// neighborhood (its edges now against its edges at rest) and (2) the positions that best fit all those rotated rest
/// edges, a sparse linear system (the graph Laplacian over the free vertices) whose Cholesky factorization is kept
/// while the pins stay the same, so a drag only costs back-substitutions.
/// <para>
/// Parts of the mesh that no pin holds stay where they are. A single pin moves its part without turning it.
/// </para>
/// </summary>
public sealed class ArapSolver
{
    private readonly PuppetMesh _mesh;
    private readonly double[] _x, _y;
    private int[] _pins = [];
    private double[] _pinX = [], _pinY = [];
    private int[] _freeIndex = []; // vertex → row in the system, or -1 when pinned or held
    private BandCholesky? _factor;
    private int[] _order = []; // system row → vertex

    public ArapSolver(PuppetMesh mesh)
    {
        _mesh = mesh;
        _x = (double[])mesh.X.Clone();
        _y = (double[])mesh.Y.Clone();
    }

    /// <summary>Deformed positions.</summary>
    public double[] X => _x;
    public double[] Y => _y;

    /// <summary>Local/global rounds per <see cref="Solve"/> (more converge further from the rest shape).</summary>
    public int Iterations { get; set; } = 6;

    /// <summary>
    /// Sets the pinned vertices and where they are (document pixels). The factorization is rebuilt only when the set of
    /// pinned vertices changes.
    /// </summary>
    public void SetPins(IReadOnlyList<int> vertices, IReadOnlyList<(double X, double Y)> positions)
    {
        if (vertices.Count != positions.Count) throw new ArgumentException("Every pin needs a position.");
        bool same = vertices.SequenceEqual(_pins);
        _pins = [.. vertices];
        _pinX = positions.Select(p => p.X).ToArray();
        _pinY = positions.Select(p => p.Y).ToArray();
        if (!same) Factor();
        for (int k = 0; k < _pins.Length; k++) (_x[_pins[k]], _y[_pins[k]]) = (_pinX[k], _pinY[k]);
    }

    /// <summary>Back to the rest shape (keeping the pins' vertices, not their positions).</summary>
    public void Reset()
    {
        Array.Copy(_mesh.X, _x, _x.Length);
        Array.Copy(_mesh.Y, _y, _y.Length);
    }

    private void Factor()
    {
        int n = _mesh.VertexCount;
        // Parts without a pin are held at rest: find the connected parts that have one.
        var part = new int[n];
        Array.Fill(part, -1);
        int parts = 0;
        var stack = new Stack<int>();
        for (int s = 0; s < n; s++)
        {
            if (part[s] >= 0) continue;
            part[s] = parts;
            stack.Push(s);
            while (stack.Count > 0)
            {
                int v = stack.Pop();
                foreach (int w in _mesh.Neighbors[v])
                    if (part[w] < 0)
                    {
                        part[w] = parts;
                        stack.Push(w);
                    }
            }
            parts++;
        }
        var pinned = new bool[parts];
        foreach (int p in _pins) pinned[part[p]] = true;
        var isPin = new bool[n];
        foreach (int p in _pins) isPin[p] = true;
        _freeIndex = new int[n];
        var order = new List<int>();
        for (int v = 0; v < n; v++)
        {
            if (isPin[v] || !pinned[part[v]])
            {
                _freeIndex[v] = -1;
                if (!pinned[part[v]]) (_x[v], _y[v]) = (_mesh.X[v], _mesh.Y[v]);
                continue;
            }
            _freeIndex[v] = order.Count;
            order.Add(v);
        }
        _order = [.. order];
        if (_order.Length == 0)
        {
            _factor = null;
            return;
        }
        // Vertices were created row by row, so neighbors are close in this order: a narrow band.
        int band = 0;
        foreach (int v in _order)
            foreach (int w in _mesh.Neighbors[v])
                if (_freeIndex[w] >= 0) band = Math.Max(band, Math.Abs(_freeIndex[w] - _freeIndex[v]));
        var matrix = new BandCholesky(_order.Length, band);
        foreach (int v in _order)
        {
            int i = _freeIndex[v];
            matrix.Add(i, i, _mesh.Neighbors[v].Length);
            foreach (int w in _mesh.Neighbors[v])
                if (_freeIndex[w] is int j and >= 0 && j < i) matrix.Add(i, j, -1);
        }
        matrix.Factor();
        _factor = matrix;
    }

    /// <summary>Runs the local/global rounds from the current positions.</summary>
    public void Solve()
    {
        if (_factor is not { } factor) return;
        int n = _mesh.VertexCount;
        double[] rx = _mesh.X, ry = _mesh.Y;
        var cos = new double[n];
        var sin = new double[n];
        var bx = new double[_order.Length];
        var by = new double[_order.Length];
        for (int it = 0; it < Iterations; it++)
        {
            // Local step: each vertex's best rotation of its rest edges onto its current edges.
            Parallel.For(0, n, i =>
            {
                double dot = 0, cross = 0;
                foreach (int j in _mesh.Neighbors[i])
                {
                    double ex = rx[i] - rx[j], ey = ry[i] - ry[j];
                    double fx = _x[i] - _x[j], fy = _y[i] - _y[j];
                    dot += ex * fx + ey * fy;
                    cross += ex * fy - ey * fx;
                }
                double len = Math.Sqrt(dot * dot + cross * cross);
                (cos[i], sin[i]) = len < 1e-12 ? (1, 0) : (dot / len, cross / len);
            });
            // Global step: L p = Σ (R_i + R_j) / 2 · (rest_i − rest_j), pinned and held vertices on the right.
            for (int k = 0; k < _order.Length; k++)
            {
                int i = _order[k];
                double sx = 0, sy = 0;
                foreach (int j in _mesh.Neighbors[i])
                {
                    double ex = rx[i] - rx[j], ey = ry[i] - ry[j];
                    double c = (cos[i] + cos[j]) / 2, s = (sin[i] + sin[j]) / 2;
                    sx += c * ex - s * ey;
                    sy += s * ex + c * ey;
                    if (_freeIndex[j] < 0)
                    {
                        sx += _x[j];
                        sy += _y[j];
                    }
                }
                bx[k] = sx;
                by[k] = sy;
            }
            factor.Solve(bx);
            factor.Solve(by);
            for (int k = 0; k < _order.Length; k++) (_x[_order[k]], _y[_order[k]]) = (bx[k], by[k]);
        }
    }

    /// <summary>The ARAP energy of the current positions (Σ over edges of the rest edge, turned by the best rotation, against the current edge), for tests.</summary>
    public double Energy()
    {
        double e = 0;
        for (int i = 0; i < _mesh.VertexCount; i++)
        {
            double dot = 0, cross = 0;
            foreach (int j in _mesh.Neighbors[i])
            {
                double ex = _mesh.X[i] - _mesh.X[j], ey = _mesh.Y[i] - _mesh.Y[j];
                double fx = _x[i] - _x[j], fy = _y[i] - _y[j];
                dot += ex * fx + ey * fy;
                cross += ex * fy - ey * fx;
            }
            double len = Math.Sqrt(dot * dot + cross * cross);
            double c = len < 1e-12 ? 1 : dot / len, s = len < 1e-12 ? 0 : cross / len;
            foreach (int j in _mesh.Neighbors[i])
            {
                double ex = _mesh.X[i] - _mesh.X[j], ey = _mesh.Y[i] - _mesh.Y[j];
                double fx = _x[i] - _x[j], fy = _y[i] - _y[j];
                double dx = fx - (c * ex - s * ey), dy = fy - (s * ex + c * ey);
                e += dx * dx + dy * dy;
            }
        }
        return e;
    }
}

/// <summary>A symmetric positive definite band matrix and its Cholesky factor (L·Lᵀ), stored by rows within the band.</summary>
internal sealed class BandCholesky
{
    private readonly int _n, _band;
    private readonly double[] _a; // row i, column j (i − band ≤ j ≤ i) at i * (band + 1) + (j − i + band)

    public BandCholesky(int n, int band)
    {
        _n = n;
        _band = band;
        _a = new double[(long)n * (band + 1) is var size && size < int.MaxValue ? (int)size : throw new InvalidOperationException("The mesh is too large.")];
    }

    private int At(int i, int j) => i * (_band + 1) + (j - i + _band);

    /// <summary>Adds to entry (i, j), j ≤ i.</summary>
    public void Add(int i, int j, double v) => _a[At(i, j)] += v;

    public void Factor()
    {
        for (int i = 0; i < _n; i++)
        {
            int j0 = Math.Max(0, i - _band);
            for (int j = j0; j <= i; j++)
            {
                double sum = _a[At(i, j)];
                int k0 = Math.Max(j0, Math.Max(0, j - _band));
                for (int k = k0; k < j; k++) sum -= _a[At(i, k)] * _a[At(j, k)];
                if (i == j)
                {
                    if (sum <= 1e-12) sum = 1e-12; // not expected: every free part has a pin
                    _a[At(i, i)] = Math.Sqrt(sum);
                }
                else _a[At(i, j)] = sum / _a[At(j, j)];
            }
        }
    }

    /// <summary>Solves A·x = b in place.</summary>
    public void Solve(double[] b)
    {
        for (int i = 0; i < _n; i++)
        {
            double sum = b[i];
            for (int k = Math.Max(0, i - _band); k < i; k++) sum -= _a[At(i, k)] * b[k];
            b[i] = sum / _a[At(i, i)];
        }
        for (int i = _n - 1; i >= 0; i--)
        {
            double sum = b[i];
            for (int k = i + 1; k <= Math.Min(_n - 1, i + _band); k++) sum -= _a[At(k, i)] * b[k];
            b[i] = sum / _a[At(i, i)];
        }
    }
}
