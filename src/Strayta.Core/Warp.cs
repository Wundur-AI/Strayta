namespace Strayta.Core;

/// <summary>
/// A warp as Photoshop describes it for a smart object (or type): either one of its named styles with a bend and
/// two perspective distortions, a custom mesh of bicubic Bézier patches (one 4×4 patch, or a grid of them split by
/// slices, "quilt" warps), or a cylinder. Coordinates are in the content space given by <see cref="Bounds"/>; the
/// placed layer's corners map that space onto the document afterwards.
/// </summary>
public sealed record WarpSpec
{
    /// <summary>Photoshop's style ID: "warpNone", "warpCustom", "warpArc", "warpFlag", "warpCylinder", ...</summary>
    public string Style { get; init; } = "warpNone";

    /// <summary>Bend, -100..100 (percent).</summary>
    public double Value { get; init; }

    /// <summary>Horizontal distortion, -100..100.</summary>
    public double Perspective { get; init; }

    /// <summary>Vertical distortion, -100..100.</summary>
    public double PerspectiveOther { get; init; }

    /// <summary>The style bends along the vertical axis ("warpRotate" Vrtc) instead of the horizontal.</summary>
    public bool Vertical { get; init; }

    /// <summary>The warped area in content coordinates: left, top, right, bottom.</summary>
    public (double Left, double Top, double Right, double Bottom) Bounds { get; init; }

    /// <summary>Mesh control points per row and column (4×4 for one patch; 3n+1 for n patches).</summary>
    public int Rows { get; init; } = 4;

    public int Columns { get; init; } = 4;

    /// <summary>Control points, row by row (<see cref="Rows"/> × <see cref="Columns"/>), for custom warps.</summary>
    public IReadOnlyList<(double X, double Y)>? Mesh { get; init; }

    /// <summary>Where the patches of a split mesh start and end across (content x), or null for one patch over the bounds.</summary>
    public IReadOnlyList<double>? SlicesX { get; init; }

    /// <summary>Where the patches of a split mesh start and end down (content y), or null for one patch over the bounds.</summary>
    public IReadOnlyList<double>? SlicesY { get; init; }

    /// <summary>Extra parameters of newer styles ("warpValues", e.g. the cylinder's).</summary>
    public IReadOnlyList<double>? Values { get; init; }

    /// <summary>True when the warp changes nothing.</summary>
    public bool IsNone => Style == "warpNone" || Style != "warpCustom" && Style != "warpCylinder" && Value == 0 && Perspective == 0 && PerspectiveOther == 0 && Values is null;
}
