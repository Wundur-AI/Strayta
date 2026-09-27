namespace Strayta.Core.Selection;

/// <summary>
/// Photoshop's Select › Modify commands (Expand, Contract, Feather, Smooth, Border) and Select › Feather. Each takes
/// the current selection and returns a new one (null when nothing is left selected); the input is never changed.
/// </summary>
/// <remarks>
/// <para>Expand and Contract are flat grayscale dilation and erosion by a disc, so a soft or feathered edge moves as a
/// whole and keeps its softness, corners grow round when expanding, and a rectangle contracts to a smaller rectangle.
/// Feather is a Gaussian blur of the coverage with σ equal to the radius. Smooth is a majority vote over the
/// neighborhood: a Gaussian with the variance of a (2r+1)-pixel box, thresholded at 50% with a one-pixel
/// anti-aliased ramp, so specks and notches smaller than the radius disappear and the outline rounds off. Border
/// selects a band centered on the edge (half inside, half outside) with softened sides.</para>
/// <para>The canvas-bounds option matches Photoshop's "Apply effect at canvas bounds": off, the area beyond the canvas
/// counts as continuing the selection, so a selection touching the canvas edge stays put along it.</para>
/// </remarks>
public static class SelectionModify
{
    /// <summary>Largest radius Photoshop accepts for Expand, Contract and Smooth.</summary>
    public const int MaxRadius = 500;
    public const int MaxBorder = 200;
    public const float MaxFeather = 1000;

    /// <summary>Select › Modify › Expand: grows the selection by <paramref name="radius"/> pixels in every direction.</summary>
    public static SelectionMask? Expand(SelectionMask? selection, int radius, PixelRect canvas)
    {
        if (selection is null || radius <= 0) return selection;
        radius = Math.Min(radius, MaxRadius);
        var region = MaskFilters.Inflate(selection.Bounds, radius).Intersect(canvas);
        int w = region.Width, h = region.Height;
        var grid = MaskFilters.Dilate(MaskFilters.Extract(selection, region), w, h, radius, GridEdges.All(0));
        return SelectionMask.FromCoverage(region, grid, canvas);
    }

    /// <summary>Select › Modify › Contract: shrinks the selection by <paramref name="radius"/> pixels.</summary>
    public static SelectionMask? Contract(SelectionMask? selection, int radius, PixelRect canvas, bool applyAtCanvasBounds = false)
    {
        if (selection is null || radius <= 0) return selection;
        radius = Math.Min(radius, MaxRadius);
        var region = selection.Bounds.Intersect(canvas);
        if (region.IsEmpty) return null;
        if (selection.IsRectangular)
        {
            // Eroding a rectangle by a disc gives the rectangle inset by the radius, except along canvas edges kept.
            var edges = GridEdges.For(region, canvas, applyAtCanvasBounds ? (byte)0 : (byte)255);
            var inset = new PixelRect(
                region.Left + (edges.Left == 255 ? 0 : radius), region.Top + (edges.Top == 255 ? 0 : radius),
                region.Right - (edges.Right == 255 ? 0 : radius), region.Bottom - (edges.Bottom == 255 ? 0 : radius));
            return inset.IsEmpty ? null : SelectionMask.Rectangle(inset, canvas);
        }
        int w = region.Width, h = region.Height;
        var grid = MaskFilters.Erode(MaskFilters.Extract(selection, region), w, h, radius,
            GridEdges.For(region, canvas, applyAtCanvasBounds ? (byte)0 : (byte)255));
        return SelectionMask.FromCoverage(region, grid, canvas);
    }

    /// <summary>Select › Modify › Feather (⇧F6): blurs the selection's edge with a Gaussian of σ = <paramref name="radius"/> pixels.</summary>
    public static SelectionMask? Feather(SelectionMask? selection, float radius, PixelRect canvas, bool applyAtCanvasBounds = false)
    {
        if (selection is null || radius < 0.1f) return selection;
        radius = Math.Min(radius, MaxFeather);
        var region = MaskFilters.Inflate(selection.Bounds, (int)Math.Ceiling(radius * 3) + 1).Intersect(canvas);
        int w = region.Width, h = region.Height;
        var blurred = MaskFilters.Blur(MaskFilters.ToFloat(MaskFilters.Extract(selection, region)), w, h, radius, BeyondCanvas(region, canvas, applyAtCanvasBounds));
        return SelectionMask.FromCoverage(region, MaskFilters.ToBytes(blurred), canvas);
    }

    /// <summary>
    /// Select › Modify › Smooth: each pixel becomes selected when most of its neighborhood of <paramref name="radius"/>
    /// pixels is, removing stray pixels and jagged notches.
    /// </summary>
    public static SelectionMask? Smooth(SelectionMask? selection, int radius, PixelRect canvas, bool applyAtCanvasBounds = false)
    {
        if (selection is null || radius <= 0) return selection;
        radius = Math.Min(radius, MaxRadius);
        float sigma = BoxSigma(radius);
        var region = MaskFilters.Inflate(selection.Bounds, (int)Math.Ceiling(sigma * 3) + 1).Intersect(canvas);
        int w = region.Width, h = region.Height;
        var values = MaskFilters.Blur(MaskFilters.ToFloat(MaskFilters.Extract(selection, region)), w, h, sigma, BeyondCanvas(region, canvas, applyAtCanvasBounds));
        return SelectionMask.FromCoverage(region, Threshold(values, sigma), canvas);
    }

    /// <summary>
    /// Select › Modify › Border: a band <paramref name="width"/> pixels wide centered on the selection's edge, with
    /// soft sides as in Photoshop.
    /// </summary>
    public static SelectionMask? Border(SelectionMask? selection, int width, PixelRect canvas, bool applyAtCanvasBounds = false)
    {
        if (selection is null || width <= 0) return selection;
        width = Math.Min(width, MaxBorder);
        int outside = (width + 1) / 2, inside = width / 2;
        float softness = Math.Max(0.5f, width / 6f);
        var region = MaskFilters.Inflate(selection.Bounds, outside + (int)Math.Ceiling(softness * 3) + 1).Intersect(canvas);
        int w = region.Width, h = region.Height;
        var grid = MaskFilters.Extract(selection, region);
        var outer = MaskFilters.Dilate(grid, w, h, outside, GridEdges.All(0));
        var inner = MaskFilters.Erode(grid, w, h, inside, GridEdges.For(region, canvas, applyAtCanvasBounds ? (byte)0 : (byte)255));
        var band = new float[grid.Length];
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, end = i + w; i < end; i++) band[i] = outer[i] * (255 - inner[i]) / 255f;
        });
        // Beyond the canvas the band continues only if it was applied at the canvas bounds; repeating the edge is
        // right either way (it is zero along kept edges).
        var soft = MaskFilters.Blur(band, w, h, softness, GridEdges.All(0) with { Repeat = true });
        return SelectionMask.FromCoverage(region, MaskFilters.ToBytes(soft), canvas);
    }

    /// <summary>σ of a Gaussian with the variance of a (2r+1)-pixel box: r(r+1)/3.</summary>
    internal static float BoxSigma(float radius) => MathF.Sqrt(radius * (radius + 1) / 3f);

    /// <summary>
    /// Thresholds blurred coverage at 50% with a one-pixel anti-aliased ramp: a straight edge blurred by σ rises with
    /// slope 1/(σ√(2π)) at its middle, so scaling by σ√(2π) around the midpoint restores a one-pixel transition.
    /// </summary>
    internal static byte[] Threshold(float[] values, float sigma)
    {
        float k = Math.Max(1f, sigma * MathF.Sqrt(2 * MathF.PI));
        var result = new byte[values.Length];
        Parallel.For(0, (values.Length + 65535) / 65536, c =>
        {
            int start = c * 65536, end = Math.Min(start + 65536, values.Length);
            for (int i = start; i < end; i++) result[i] = MaskFilters.ClampByte((values[i] - 127.5f) * k + 127.5f);
        });
        return result;
    }

    /// <summary>Beyond the canvas: the edge repeated (the selection carries on), or unselected when applying at the canvas bounds.</summary>
    private static GridEdges BeyondCanvas(PixelRect region, PixelRect canvas, bool applyAtCanvasBounds) =>
        applyAtCanvasBounds ? GridEdges.All(0) : GridEdges.All(0) with { Repeat = true };
}
