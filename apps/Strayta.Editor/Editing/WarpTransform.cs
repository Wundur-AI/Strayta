using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>
/// The warp of an open transform (Edit › Transform › Warp): one of Photoshop's styles with its bend and distortions,
/// or a custom Bézier mesh whose control points are dragged. It lives in a frame (0, 0)–(<see cref="Width"/>,
/// <see cref="Height"/>): the layer's box for pixels, the content's placed size for a smart object, so it can be
/// written into the smart object's warp descriptor as it is. The frame reaches the document through a base
/// placement (the Free Transform's map) supplied by the caller: control points are sent through it and the patches
/// evaluated there, as Photoshop places warped smart objects (<see cref="SmartObjectPlacement"/>).
/// <para>
/// Styles are framed like Photoshop's: the envelope's bounding box is scaled onto the frame. Dragging the mesh (a
/// control point, or a point of the surface) turns a style into a custom warp first.
/// </para>
/// </summary>
public sealed class WarpTransform : ObservableObject
{
    /// <summary>The styles offered, in Photoshop's menu order (IDs as in files; the UI shows <see cref="StyleNames"/>).</summary>
    public static IReadOnlyList<string> Styles { get; } =
    [
        "warpNone", "warpCustom", "warpArc", "warpArcLower", "warpArcUpper", "warpArch", "warpBulge", "warpShellLower", "warpShellUpper",
        "warpFlag", "warpWave", "warpFish", "warpRise", "warpFisheye", "warpInflate", "warpSqueeze", "warpTwist",
    ];

    public static IReadOnlyList<string> StyleNames { get; } =
    [
        "None", "Custom", "Arc", "Arc Lower", "Arc Upper", "Arch", "Bulge", "Shell Lower", "Shell Upper",
        "Flag", "Wave", "Fish", "Rise", "Fisheye", "Inflate", "Squeeze", "Twist",
    ];

    /// <summary>Grid choices: patches per side (1 is Photoshop's default single patch).</summary>
    public static IReadOnlyList<int> GridSizes { get; } = [1, 3, 4, 5];

    private string _style = "warpNone";
    private double _bend, _horizontal, _vertical;
    private bool _verticalOrientation;
    private WarpMesh _custom;
    private WarpMesh? _cached;

    // Drag state.
    private int _dragPoint = -1;
    private (double U, double V)? _dragSurface;
    private WarpMesh? _dragStart;
    private (double X, double Y) _grabOffset;

    public WarpTransform(double width, double height, WarpSpec? initial = null)
    {
        if (!(width > 0) || !(height > 0)) throw new ArgumentException("A warp needs a frame.");
        Width = width;
        Height = height;
        _custom = WarpEditing.Identity(width, height, 1, 1);
        if (initial is { IsNone: false } spec) Load(spec);
    }

    public double Width { get; }
    public double Height { get; }

    /// <summary>
    /// Styles are framed in the box (their envelope's bounding box scaled onto it), as Photoshop draws warped smart
    /// objects and pixels; type bends over its bounds without framing.
    /// </summary>
    public bool FitStyles { get; init; } = true;

    /// <summary>Only the named styles (Warp Text on type): no custom mesh.</summary>
    public bool StylesOnly { get; init; }

    /// <summary>Raised after every change.</summary>
    public event Action? Changed;

    /// <summary>Photoshop's style ID ("warpNone", "warpCustom", "warpArc", ...).</summary>
    public string Style
    {
        get => _style;
        set
        {
            if (_style == value || !Styles.Contains(value)) return;
            // Choosing Custom after a style keeps its shape as an editable mesh, as Photoshop does.
            if (value == "warpCustom" && StylesOnly) return;
            if (value == "warpCustom" && StyleMesh() is { } m) _custom = FitStyles ? WithPoints(m, WarpEditing.Fitted(m, Width, Height)) : m;
            _style = value;
            if (value is not "warpNone" and not "warpCustom" && _bend == 0) _bend = 50; // a style starts bent, as in Photoshop
            Update();
            OnPropertyChanged(nameof(StyleIndex));
            OnPropertyChanged(nameof(Bend));
            OnPropertyChanged(nameof(IsCustom));
            OnPropertyChanged(nameof(IsStyle));
        }
    }

    /// <summary>The style's index in <see cref="Styles"/> (for the options bar's list).</summary>
    public int StyleIndex
    {
        get => Math.Max(0, Styles.ToList().IndexOf(_style));
        set
        {
            if (value >= 0 && value < Styles.Count) Style = Styles[value];
        }
    }

    public bool IsCustom => _style == "warpCustom";
    public bool IsStyle => _style is not "warpNone" and not "warpCustom";

    /// <summary>Bend, -100..100 %.</summary>
    public double Bend { get => _bend; set => SetValue(ref _bend, Math.Clamp(value, -100, 100)); }

    /// <summary>Horizontal distortion, -100..100 %.</summary>
    public double HorizontalDistortion { get => _horizontal; set => SetValue(ref _horizontal, Math.Clamp(value, -100, 100)); }

    /// <summary>Vertical distortion, -100..100 %.</summary>
    public double VerticalDistortion { get => _vertical; set => SetValue(ref _vertical, Math.Clamp(value, -100, 100)); }

    /// <summary>The style bends along the vertical axis (the options bar's orientation button).</summary>
    public bool VerticalOrientation
    {
        get => _verticalOrientation;
        set
        {
            if (_verticalOrientation == value) return;
            _verticalOrientation = value;
            Update();
            OnPropertyChanged();
        }
    }

    /// <summary>Patches per side of the custom mesh (1, 3, 4 or 5).</summary>
    public int GridSize
    {
        get => _custom.SlicesX.Count - 1;
        set
        {
            if (value < 1 || StylesOnly || value == GridSize && value == _custom.SlicesY.Count - 1) return;
            var surface = LocalMesh();
            _custom = WarpEditing.Resplit(surface, Width, Height, value, value);
            _style = "warpCustom";
            Update();
            OnPropertyChanged();
            OnPropertyChanged(nameof(GridIndex));
            OnPropertyChanged(nameof(Style));
            OnPropertyChanged(nameof(StyleIndex));
            OnPropertyChanged(nameof(IsCustom));
            OnPropertyChanged(nameof(IsStyle));
        }
    }

    /// <summary>The grid size's index in <see cref="GridSizes"/>.</summary>
    public int GridIndex
    {
        get => Math.Max(0, GridSizes.ToList().IndexOf(GridSize));
        set
        {
            if (value >= 0 && value < GridSizes.Count) GridSize = GridSizes[value];
        }
    }

    /// <summary>True when the warp leaves the frame as it is.</summary>
    public bool IsIdentity => LocalMesh() is not { } m || IsIdentityMesh(m);

    private bool IsIdentityMesh(WarpMesh m)
    {
        var id = WarpEditing.Identity(Width, Height, m.SlicesX.Count - 1, m.SlicesY.Count - 1);
        double tol = 1e-6 * Math.Max(Width, Height);
        return m.Points.Zip(id.Points).All(p => Math.Abs(p.First.X - p.Second.X) < tol && Math.Abs(p.First.Y - p.Second.Y) < tol);
    }

    /// <summary>
    /// The warp in its frame: frame positions to warped frame positions (a style's envelope fitted into the frame, or
    /// the custom mesh); null for no warp.
    /// </summary>
    public WarpMesh? LocalMesh()
    {
        if (_cached is not null) return _cached;
        _cached = _style switch
        {
            "warpNone" => null,
            "warpCustom" => _custom,
            _ => StyleMesh() is { } m ? FitStyles ? WithPoints(m, WarpEditing.Fitted(m, Width, Height)) : m : null,
        };
        return _cached;
    }

    private WarpMesh? StyleMesh() => IsStyle
        ? WarpMesh.From(new WarpSpec
        {
            Style = _style, Value = _bend, Perspective = _horizontal, PerspectiveOther = _vertical, Vertical = _verticalOrientation,
            Bounds = (0, 0, Width, Height),
        })
        : null;

    private static WarpMesh WithPoints(WarpMesh m, IReadOnlyList<(double X, double Y)> points) => new(m.SlicesX, m.SlicesY, points);

    /// <summary>The warp in document space: frame positions to document positions through <paramref name="place"/>.</summary>
    public WarpMesh DocumentMesh(Projective place)
    {
        var local = LocalMesh() ?? WarpEditing.Identity(Width, Height, 1, 1);
        return new WarpMesh(local.SlicesX, local.SlicesY, local.Points.Select(p => place.Apply(p.X, p.Y)).ToArray());
    }

    /// <summary>The warp as it is written in a file: in the frame (0, 0)–(Width, Height).</summary>
    public WarpSpec ToSpec()
    {
        var bounds = (0.0, 0.0, Width, Height);
        if (IsStyle)
            return new WarpSpec
            {
                Style = _style, Value = _bend, Perspective = _horizontal, PerspectiveOther = _vertical, Vertical = _verticalOrientation, Bounds = bounds,
            };
        if (_style == "warpNone") return new WarpSpec { Bounds = bounds };
        bool split = _custom.SlicesX.Count > 2 || _custom.SlicesY.Count > 2;
        return new WarpSpec
        {
            Style = "warpCustom", Bounds = bounds, Mesh = [.. _custom.Points],
            Rows = 3 * (_custom.SlicesY.Count - 1) + 1, Columns = 3 * (_custom.SlicesX.Count - 1) + 1,
            SlicesX = split ? [.. _custom.SlicesX] : null, SlicesY = split ? [.. _custom.SlicesY] : null,
        };
    }

    /// <summary>Takes an existing warp (a smart object's), with its points fitted into the frame as they are drawn.</summary>
    private void Load(WarpSpec spec)
    {
        if (spec.Style == "warpCustom" || !Styles.Contains(spec.Style))
        {
            if (WarpMesh.From(spec) is not { } mesh) return;
            // Bring the mesh into this frame: its slices from the spec's bounds, its points framed as drawn.
            var (l, t, r, b) = spec.Bounds;
            double kx = Width / Math.Max(1e-9, r - l), ky = Height / Math.Max(1e-9, b - t);
            var slicesX = mesh.SlicesX.Select(s => (s - l) * kx).ToArray();
            var slicesY = mesh.SlicesY.Select(s => (s - t) * ky).ToArray();
            slicesX[0] = 0; slicesX[^1] = Width; slicesY[0] = 0; slicesY[^1] = Height;
            _custom = new WarpMesh(slicesX, slicesY, WarpEditing.Fitted(mesh, Width, Height));
            _style = "warpCustom";
            return;
        }
        _style = spec.Style;
        _bend = spec.Value;
        _horizontal = spec.Perspective;
        _vertical = spec.PerspectiveOther;
        _verticalOrientation = spec.Vertical;
    }

    /// <summary>Back to no warp (the options bar's reset).</summary>
    public void Reset()
    {
        _style = "warpNone";
        _bend = _horizontal = _vertical = 0;
        _custom = WarpEditing.Identity(Width, Height, 1, 1);
        Update();
        foreach (var p in new[] { nameof(Style), nameof(StyleIndex), nameof(Bend), nameof(HorizontalDistortion), nameof(VerticalDistortion), nameof(GridSize), nameof(GridIndex), nameof(IsCustom), nameof(IsStyle) })
            OnPropertyChanged(p);
    }

    // ---- Dragging the mesh ----------------------------------------------------------------------

    /// <summary>
    /// Index of the custom mesh's control point at document point (x, y) within <paramref name="tolerance"/>, or -1.
    /// Anchors (patch corners) win over the handles next to them.
    /// </summary>
    public int HitPoint(Projective place, double x, double y, double tolerance)
    {
        if (!IsCustom) return -1;
        var points = DocumentMesh(place).Points;
        int best = -1;
        double bestD = tolerance;
        int columns = _custom.SlicesX.Count * 3 - 2;
        for (int k = 0; k < points.Count; k++)
        {
            double d = Math.Max(Math.Abs(points[k].X - x), Math.Abs(points[k].Y - y));
            bool anchor = IsAnchor(k, columns);
            if (d < bestD || d <= bestD + 1e-9 && anchor && best >= 0 && !IsAnchor(best, columns))
            {
                bestD = d;
                best = k;
            }
        }
        return best;
    }

    private static bool IsAnchor(int k, int columns) => k / columns % 3 == 0 && k % columns % 3 == 0;

    /// <summary>
    /// Starts a drag at document point (x, y): a control point when one is there, else the warped surface itself
    /// (dragging inside the mesh bends it). A style becomes a custom warp. False when the press misses the warp.
    /// </summary>
    public bool BeginDrag(Projective place, double x, double y, double tolerance)
    {
        EndDrag();
        if (StylesOnly) return false;
        if (!IsCustom)
        {
            var surface = LocalMesh();
            _custom = surface is null ? WarpEditing.Identity(Width, Height, 1, 1) : surface;
            _style = "warpCustom";
            _cached = null;
            OnPropertyChanged(nameof(Style));
            OnPropertyChanged(nameof(StyleIndex));
            OnPropertyChanged(nameof(IsCustom));
            OnPropertyChanged(nameof(IsStyle));
        }
        _dragStart = _custom;
        int k = HitPoint(place, x, y, tolerance);
        if (k >= 0)
        {
            _dragPoint = k;
            var p = DocumentMesh(place).Points[k];
            _grabOffset = (p.X - x, p.Y - y);
            return true;
        }
        var inverse = place.Invert();
        var (fx, fy) = inverse.Apply(x, y);
        if (WarpEditing.Locate(_custom, fx, fy, tolerance: Math.Max(Width, Height) * 0.02 + ScaleOf(inverse, x, y) * tolerance) is { } uv)
        {
            _dragSurface = uv;
            return true;
        }
        _dragStart = null;
        return false;
    }

    private static double ScaleOf(Projective p, double x, double y)
    {
        var a = p.Linearize(x, y);
        return Math.Sqrt(Math.Abs(a.Determinant));
    }

    public bool IsDragging => _dragStart is not null;

    public void DragTo(Projective place, double x, double y)
    {
        if (_dragStart is not { } start) return;
        var inverse = place.Invert();
        if (_dragPoint >= 0)
        {
            var (fx, fy) = inverse.Apply(x + _grabOffset.X, y + _grabOffset.Y);
            var points = start.Points.ToArray();
            var old = points[_dragPoint];
            double dx = fx - old.X, dy = fy - old.Y;
            points[_dragPoint] = (fx, fy);
            // An anchor carries its handles along, as in Photoshop.
            int columns = start.SlicesX.Count * 3 - 2, rows = start.SlicesY.Count * 3 - 2;
            if (IsAnchor(_dragPoint, columns))
            {
                int r = _dragPoint / columns, c = _dragPoint % columns;
                foreach (var (nr, nc) in new[] { (r - 1, c), (r + 1, c), (r, c - 1), (r, c + 1) })
                    if (nr >= 0 && nr < rows && nc >= 0 && nc < columns)
                    {
                        int n = nr * columns + nc;
                        points[n] = (start.Points[n].X + dx, start.Points[n].Y + dy);
                    }
            }
            _custom = new WarpMesh(start.SlicesX, start.SlicesY, points);
        }
        else if (_dragSurface is { } uv)
        {
            var (fx, fy) = inverse.Apply(x, y);
            _custom = new WarpMesh(start.SlicesX, start.SlicesY, WarpEditing.DragSurface(start, uv.U, uv.V, fx, fy));
        }
        Update();
    }

    public void EndDrag()
    {
        _dragPoint = -1;
        _dragSurface = null;
        _dragStart = null;
    }

    /// <summary>Sets custom control points directly (frame coordinates; for scripts and tests).</summary>
    public void SetCustomPoints(IReadOnlyList<(double X, double Y)> points)
    {
        _custom = new WarpMesh(_custom.SlicesX, _custom.SlicesY, points);
        _style = "warpCustom";
        Update();
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(nameof(StyleIndex));
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(IsStyle));
    }

    /// <summary>The custom mesh's control points in frame coordinates.</summary>
    public IReadOnlyList<(double X, double Y)> CustomPoints => _custom.Points;

    private void SetValue(ref double field, double value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        Update();
        OnPropertyChanged(name);
    }

    private void Update()
    {
        _cached = null;
        Changed?.Invoke();
    }
}
