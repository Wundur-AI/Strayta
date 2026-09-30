using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>
/// An open Puppet Warp (Edit › Puppet Warp): a triangle mesh over the layer's opaque pixels and the pins placed on it.
/// Clicking the mesh adds a pin (and drags it straight away), dragging a pin deforms the mesh as rigidly as possible
/// around the others (<see cref="ArapSolver"/>), Option-click removes a pin. Positions are document pixels; nothing
/// here touches pixels.
/// </summary>
public sealed class PuppetWarpSession : ObservableObject
{
    private readonly PixelLayer _layer;
    private readonly Plane? _alpha;
    private readonly PixelRect _bounds;
    private PuppetMesh _mesh;
    private ArapSolver _solver;
    private PuppetDensity _density = PuppetDensity.Normal;
    private double _expansion = 2;
    private bool _showMesh = true;
    private readonly List<(int Vertex, double X, double Y)> _pins = [];
    private int _selected = -1, _dragging = -1;
    private (double X, double Y) _grab;

    private PuppetWarpSession(PixelLayer layer, Plane? alpha, PixelRect bounds, PuppetMesh mesh)
    {
        _layer = layer;
        _alpha = alpha;
        _bounds = bounds;
        _mesh = mesh;
        _solver = new ArapSolver(mesh);
    }

    /// <summary>A session on <paramref name="layer"/>'s pixels, or null when it has none opaque.</summary>
    public static PuppetWarpSession? Create(PixelLayer layer)
    {
        if (layer.Pixels is not { } pixels || layer.Bounds.IsEmpty) return null;
        var mesh = PuppetMesh.Build(pixels.Alpha, layer.Bounds, PuppetDensity.Normal, 2);
        return mesh is null ? null : new PuppetWarpSession(layer, pixels.Alpha, layer.Bounds, mesh);
    }

    /// <summary>Raised after every change of the deformation, the pins or the mesh.</summary>
    public event Action? Changed;

    public PixelLayer Layer => _layer;
    public PuppetMesh Mesh => _mesh;

    /// <summary>Deformed vertex positions.</summary>
    public double[] X => _solver.X;
    public double[] Y => _solver.Y;

    /// <summary>Changes every time the deformation does (for caching previews).</summary>
    public int Version { get; private set; }

    /// <summary>Density: Fewer Points, Normal, More Points (the options bar's index 0..2). Rebuilds the mesh, keeping the pins.</summary>
    public int DensityIndex
    {
        get => (int)_density;
        set
        {
            if (value < 0 || value > 2 || value == (int)_density) return;
            _density = (PuppetDensity)value;
            Rebuild();
            OnPropertyChanged();
        }
    }

    /// <summary>Expansion: pixels the mesh reaches beyond the layer's opaque pixels.</summary>
    public double Expansion
    {
        get => _expansion;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (value == _expansion) return;
            _expansion = value;
            Rebuild();
            OnPropertyChanged();
        }
    }

    /// <summary>Show Mesh.</summary>
    public bool ShowMesh
    {
        get => _showMesh;
        set
        {
            if (SetProperty(ref _showMesh, value)) Changed?.Invoke();
        }
    }

    /// <summary>Pins: the mesh vertex each holds and where it is now.</summary>
    public IReadOnlyList<(int Vertex, double X, double Y)> Pins => _pins;

    /// <summary>The selected pin (drawn filled), or -1.</summary>
    public int SelectedPin => _selected;

    public bool HasPins => _pins.Count > 0;

    /// <summary>True when some pin has moved (committing changes the layer).</summary>
    public bool IsDeformed => _pins.Any(p => Math.Abs(p.X - _mesh.X[p.Vertex]) > 1e-6 || Math.Abs(p.Y - _mesh.Y[p.Vertex]) > 1e-6);

    private void Rebuild()
    {
        if (PuppetMesh.Build(_alpha, _bounds, _density, _expansion) is not { } mesh) return;
        // Pins keep their place on the layer (their rest point) and where they have been moved to.
        var rest = _pins.Select(p => (_mesh.X[p.Vertex], _mesh.Y[p.Vertex], p.X, p.Y)).ToList();
        _mesh = mesh;
        _solver = new ArapSolver(mesh);
        _pins.Clear();
        foreach (var (rx, ry, x, y) in rest)
        {
            int v = mesh.NearestVertex(rx, ry, double.MaxValue);
            if (v >= 0 && _pins.All(p => p.Vertex != v)) _pins.Add((v, x, y));
        }
        _selected = Math.Min(_selected, _pins.Count - 1);
        Solve();
    }

    /// <summary>The pin at (x, y) within <paramref name="tolerance"/> pixels, or -1.</summary>
    public int HitPin(double x, double y, double tolerance)
    {
        int best = -1;
        double bestD = tolerance;
        for (int i = 0; i < _pins.Count; i++)
        {
            double d = Math.Sqrt((_pins[i].X - x) * (_pins[i].X - x) + (_pins[i].Y - y) * (_pins[i].Y - y));
            if (d <= bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// A press at (x, y): Option on a pin removes it; on a pin selects it and starts dragging it; on the (deformed) mesh
    /// adds a pin there and starts dragging it. False when the press misses the pins and the mesh.
    /// </summary>
    public bool Press(double x, double y, double tolerance, bool alt)
    {
        int hit = HitPin(x, y, tolerance);
        if (hit >= 0)
        {
            if (alt)
            {
                RemovePin(hit);
                return true;
            }
            _selected = _dragging = hit;
            _grab = (_pins[hit].X - x, _pins[hit].Y - y);
            Changed?.Invoke();
            return true;
        }
        if (alt) return false;
        // The vertex drawn nearest the press (on the deformed mesh), if the press is on it.
        int v = NearestDeformedVertex(x, y, _mesh.Spacing * 1.5);
        if (v < 0 || _pins.Any(p => p.Vertex == v)) return false;
        _pins.Add((v, X[v], Y[v]));
        _selected = _dragging = _pins.Count - 1;
        _grab = (X[v] - x, Y[v] - y);
        Solve();
        return true;
    }

    private int NearestDeformedVertex(double x, double y, double maxDistance)
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

    public bool IsDragging => _dragging >= 0;

    /// <summary>Moves the dragged pin to (x, y) and deforms the mesh.</summary>
    public void DragTo(double x, double y)
    {
        if (_dragging < 0) return;
        var p = _pins[_dragging];
        _pins[_dragging] = (p.Vertex, x + _grab.X, y + _grab.Y);
        Solve();
    }

    /// <summary>Ends a drag and lets the mesh settle (the rigid solution takes more rounds than a drag frame allows).</summary>
    public void EndDrag()
    {
        if (_dragging < 0) return;
        _dragging = -1;
        Settle();
    }

    /// <summary>Runs extra solver rounds so the shape reaches its most rigid form.</summary>
    public void Settle()
    {
        int rounds = _solver.Iterations;
        _solver.Iterations = 80;
        Solve();
        _solver.Iterations = rounds;
    }

    /// <summary>Moves pin <paramref name="index"/> to (x, y) (for scripts and tests).</summary>
    public void MovePin(int index, double x, double y)
    {
        if (index < 0 || index >= _pins.Count) return;
        _pins[index] = (_pins[index].Vertex, x, y);
        Solve();
    }

    /// <summary>Adds a pin on the mesh vertex nearest (x, y) (rest position; for scripts and tests); its index, or -1.</summary>
    public int AddPin(double x, double y)
    {
        int v = _mesh.NearestVertex(x, y, _mesh.Spacing * 2);
        if (v < 0 || _pins.Any(p => p.Vertex == v)) return -1;
        _pins.Add((v, X[v], Y[v]));
        _selected = _pins.Count - 1;
        Solve();
        return _selected;
    }

    public void RemovePin(int index)
    {
        if (index < 0 || index >= _pins.Count) return;
        _pins.RemoveAt(index);
        _selected = _pins.Count == 0 ? -1 : Math.Min(_selected, _pins.Count - 1);
        _dragging = -1;
        if (_pins.Count == 0) _solver.Reset();
        Solve();
    }

    /// <summary>Delete: removes the selected pin.</summary>
    public void RemoveSelectedPin() => RemovePin(_selected);

    /// <summary>The options bar's Remove All Pins: back to the rest shape.</summary>
    public void RemoveAllPins()
    {
        _pins.Clear();
        _selected = _dragging = -1;
        _solver.Reset();
        Solve();
    }

    private void Solve()
    {
        _solver.SetPins(_pins.Select(p => p.Vertex).ToList(), _pins.Select(p => (p.X, p.Y)).ToList());
        _solver.Solve();
        Version++;
        OnPropertyChanged(nameof(HasPins));
        Changed?.Invoke();
    }

    /// <summary>The deformation as triangles for a raster placed at <paramref name="bounds"/> (scaled down by <paramref name="factor"/>).</summary>
    public TriangleMap Map(PixelRect bounds, int factor = 1) => _mesh.Map([.. X], [.. Y], bounds, factor);
}
