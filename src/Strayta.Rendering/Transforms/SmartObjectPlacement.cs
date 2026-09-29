using Strayta.Core;

namespace Strayta.Rendering.Transforms;

/// <summary>
/// Places a smart object's content in the document the way Photoshop does, measured against the pixels it stores for
/// warped smart objects:
/// <list type="number">
/// <item>The content image is scaled to the placed size ("Sz", the space the corners and the warp are measured in).</item>
/// <item>Without a warp, the rectangle (0, 0)–(width, height) is mapped onto the four corners, a perspective map when
/// they are not a parallelogram.</item>
/// <item>With a warp, the warp's Bézier control points are first fitted into that rectangle (their bounding box is
/// scaled onto it: the transform box frames the warped shape), then each control point is sent through the corner map,
/// and the patches are evaluated in document space from the moved control points. (Evaluating the warp first and
/// mapping every point through the perspective instead differs by tens of pixels in the corpus' perspective
/// mock-ups.)</item>
/// </list>
/// </summary>
public static class SmartObjectPlacement
{
    /// <summary>The map from content pixels (0..<paramref name="contentWidth"/>, 0..<paramref name="contentHeight"/>) to document positions.</summary>
    public static Func<double, double, (double X, double Y)> Map(int contentWidth, int contentHeight, double width, double height,
        IReadOnlyList<(double X, double Y)> corners, WarpMesh? warp)
    {
        var place = Projective.RectToQuad(width, height, corners);
        double kx = width / contentWidth, ky = height / contentHeight;
        if (warp is null) return (x, y) => place.Apply(x * kx, y * ky);
        var moved = PlacedMesh(warp, width, height, place);
        return (x, y) => moved.Map(x * kx, y * ky);
    }

    /// <summary>The warp's control points fitted into the placed rectangle and moved through <paramref name="place"/>.</summary>
    public static WarpMesh PlacedMesh(WarpMesh warp, double width, double height, Projective place)
    {
        ArgumentNullException.ThrowIfNull(warp);
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in warp.Points)
        {
            x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X);
            y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
        }
        double sx = x1 - x0 > 1e-9 ? width / (x1 - x0) : 1, sy = y1 - y0 > 1e-9 ? height / (y1 - y0) : 1;
        var points = warp.Points.Select(p => place.Apply((p.X - x0) * sx, (p.Y - y0) * sy)).ToArray();
        return new WarpMesh(warp.SlicesX, warp.SlicesY, points);
    }

    /// <summary>
    /// Renders <paramref name="content"/> placed at <paramref name="corners"/> with an optional warp; pixels outside
    /// <paramref name="clip"/> are left out.
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds) Render(Raster content, double width, double height, IReadOnlyList<(double X, double Y)> corners,
        WarpSpec? warp, ResampleFilter filter, PixelRect? clip = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var mesh = warp is null || warp.IsNone ? null : WarpMesh.From(warp);
        if (mesh is null)
        {
            var place = Projective.FromAffine(Affine.Scale(width / content.Width, height / content.Height))
                .Then(Projective.RectToQuad(width, height, corners));
            return ProjectiveResampler.TransformRaster(content, PixelRect.FromSize(content.Width, content.Height), place, filter, clip, cancel);
        }
        return MeshResampler.TransformRaster(content, Map(content.Width, content.Height, width, height, corners, mesh), filter, clip, cancel);
    }
}
