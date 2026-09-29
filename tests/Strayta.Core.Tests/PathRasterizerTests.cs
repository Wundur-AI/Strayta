using System.Diagnostics;
using Strayta.Core;
using Strayta.Core.Paths;

namespace Strayta.Core.Tests;

/// <summary>
/// The vector path rasterizer: exact area coverage, the fill rules, Photoshop's path operations in order, and the
/// geometry of the shape tools.
/// </summary>
public class PathRasterizerTests
{
    private static VectorPath Path(params Subpath[] s) => new(s);

    private static double Sum(byte[] c) => c.Sum(v => v / 255.0);

    [Fact]
    public void A_rectangle_on_pixel_edges_is_solid_and_one_on_half_pixels_is_half_covered()
    {
        var area = new PixelRect(0, 0, 10, 10);
        var solid = PathRasterizer.Rasterize(Path(ShapeGeometry.Rectangle(2, 2, 6, 5)), area);
        Assert.Equal(255, solid[3 * 10 + 3]);
        Assert.Equal(0, solid[1 * 10 + 3]);
        Assert.Equal(12, Sum(solid), 3);

        var half = PathRasterizer.Rasterize(Path(ShapeGeometry.Rectangle(2.5, 2, 6, 5)), area);
        Assert.Equal(128, half[3 * 10 + 2]); // half a pixel wide
        Assert.Equal(255, half[3 * 10 + 3]);
        var quarter = PathRasterizer.Rasterize(Path(ShapeGeometry.Rectangle(2.5, 2.5, 6, 5)), area);
        Assert.Equal(64, quarter[2 * 10 + 2]); // a quarter of the corner pixel
    }

    [Fact]
    public void A_slanted_edge_covers_exactly_its_area()
    {
        // A triangle with vertices on pixel corners: 0.5 area per diagonal pixel.
        var tri = new Subpath([PathKnot.Corner(0, 0), PathKnot.Corner(8, 0), PathKnot.Corner(0, 8)], true);
        var c = PathRasterizer.Rasterize(Path(tri), new PixelRect(0, 0, 8, 8));
        Assert.Equal(32, Sum(c), 1);
        Assert.Equal(128, c[0 * 8 + 7]);
        Assert.Equal(255, c[0 * 8 + 6]);
        Assert.Equal(0, c[1 * 8 + 7]);
    }

    [Fact]
    public void An_ellipse_covers_pi_a_b()
    {
        var c = PathRasterizer.Rasterize(Path(ShapeGeometry.Ellipse(10, 10, 90, 50)), new PixelRect(0, 0, 100, 60));
        Assert.Equal(Math.PI * 40 * 20, Sum(c), 0); // four-arc Béziers deviate from a true ellipse by < 0.03%
    }

    [Fact]
    public void Coverage_outside_the_area_is_clipped_correctly()
    {
        // Only the right half of a shape that starts left of the area: columns stay fully covered up to its edge.
        var c = PathRasterizer.Rasterize(Path(new Subpath([PathKnot.Corner(-50, 0), PathKnot.Corner(4.5, 0), PathKnot.Corner(4.5, 3), PathKnot.Corner(-50, 3)], true)),
            new PixelRect(0, 0, 8, 3));
        Assert.Equal([255, 255, 255, 255, 128, 0, 0, 0], c.Take(8).ToArray());
    }

    [Fact]
    public void Path_operations_apply_in_order()
    {
        var area = new PixelRect(0, 0, 20, 10);
        var a = ShapeGeometry.Rectangle(0, 0, 10, 10);
        Subpath B(PathOperation op) => ShapeGeometry.Rectangle(5, 0, 15, 10, op);
        Assert.Equal(150, Sum(PathRasterizer.Rasterize(Path(a, B(PathOperation.Combine)), area)), 3);
        Assert.Equal(50, Sum(PathRasterizer.Rasterize(Path(a, B(PathOperation.Subtract)), area)), 3);
        Assert.Equal(50, Sum(PathRasterizer.Rasterize(Path(a, B(PathOperation.Intersect)), area)), 3);
        Assert.Equal(100, Sum(PathRasterizer.Rasterize(Path(a, B(PathOperation.Exclude)), area)), 3);
        // Each operation applies to everything before it: subtracting then adding back restores, adding then
        // subtracting removes from both.
        var cut = ShapeGeometry.Rectangle(0, 0, 4, 10, PathOperation.Subtract);
        var add = ShapeGeometry.Rectangle(12, 0, 16, 10);
        Assert.Equal(100, Sum(PathRasterizer.Rasterize(Path(a, cut, ShapeGeometry.Rectangle(0, 0, 4, 10)), area)), 3);
        Assert.Equal(100, Sum(PathRasterizer.Rasterize(Path(a, add, cut with { Knots = ShapeGeometry.Rectangle(8, 0, 14, 10).Knots }), area)), 3);
    }

    [Fact]
    public void Touching_shapes_that_combine_leave_no_seam()
    {
        // Two rectangles meeting at x = 5.5, inside a pixel: a coverage union (a + b − ab) would leave 0.75 there.
        var c = PathRasterizer.Rasterize(Path(ShapeGeometry.Rectangle(0, 0, 5.5, 4), ShapeGeometry.Rectangle(5.5, 0, 10, 4)), new PixelRect(0, 0, 10, 4));
        Assert.All(c, v => Assert.Equal(255, v));
    }

    [Fact]
    public void A_component_with_a_hole_winds_it_the_other_way()
    {
        var ring = ShapeGeometry.CustomShape("Ring", 0, 0, 40, 40);
        Assert.Equal(2, ring.Count);
        Assert.True(PathRasterizer.ContinuesComponent(ring[1]));
        var c = PathRasterizer.Rasterize(Path([.. ring]), new PixelRect(0, 0, 40, 40));
        Assert.Equal(0, c[20 * 40 + 20]); // the hole
        Assert.Equal(255, c[20 * 40 + 5]);
        Assert.Equal(Math.PI * (400 - 100), Sum(c), 0);
    }

    [Fact]
    public void Even_odd_and_nonzero_differ_where_contours_overlap()
    {
        var sq = new List<PathPoint> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        var inner = new List<PathPoint> { new(2, 2), new(8, 2), new(8, 8), new(2, 8) }; // same direction
        var area = new PixelRect(0, 0, 10, 10);
        var nz = PathRasterizer.Coverage([sq, inner], FillRule.NonZero, area);
        var eo = PathRasterizer.Coverage([sq, inner], FillRule.EvenOdd, area);
        Assert.Equal(100, nz.Sum(), 3);
        Assert.Equal(64, eo.Sum(), 3);
    }

    [Fact]
    public void An_inverted_path_starts_filled()
    {
        var p = Path(ShapeGeometry.Rectangle(2, 2, 8, 8, PathOperation.Subtract)) with { InitialFillAll = true };
        var c = PathRasterizer.Rasterize(p, new PixelRect(0, 0, 10, 10));
        Assert.Equal(255, c[0]);
        Assert.Equal(0, c[5 * 10 + 5]);
    }

    [Fact]
    public void A_centered_stroke_covers_perimeter_times_width()
    {
        var p = Path(ShapeGeometry.Rectangle(10, 10, 50, 40));
        var polys = PathStroker.Outline(p, new StrokeGeometry(4, Join: LineJoin.Miter));
        var c = PathRasterizer.Coverage(polys, FillRule.NonZero, new PixelRect(0, 0, 60, 50));
        // Outer 44×34 minus inner 36×26.
        Assert.Equal(44 * 34 - 36 * 26, c.Sum(), 1);
        var bevel = PathRasterizer.Coverage(PathStroker.Outline(p, new StrokeGeometry(4, Join: LineJoin.Bevel)), FillRule.NonZero, new PixelRect(0, 0, 60, 50));
        Assert.Equal(44 * 34 - 36 * 26 - 4 * 2, bevel.Sum(), 1); // each corner loses half a 2×2 square
    }

    [Fact]
    public void A_curved_stroke_has_no_seams()
    {
        var p = Path(ShapeGeometry.Ellipse(20, 20, 180, 180));
        var c = PathRasterizer.Coverage(PathStroker.Outline(p, new StrokeGeometry(10)), FillRule.NonZero, new PixelRect(0, 0, 200, 200));
        Assert.Equal(Math.PI * (85 * 85 - 75 * 75), c.Sum(), 0);
        Assert.True(c.Max() <= 1.0001f);
        // Every pixel well inside the band is fully covered.
        for (int a = 0; a < 360; a += 7)
        {
            double t = a * Math.PI / 180;
            int x = (int)(100 + 80 * Math.Cos(t)), y = (int)(100 + 80 * Math.Sin(t));
            Assert.True(c[y * 200 + x] > 0.99f, $"gap at {a}°");
        }
    }

    [Fact]
    public void Caps_and_dashes_on_an_open_line()
    {
        var line = Path(new Subpath([PathKnot.Corner(10, 10), PathKnot.Corner(50, 10)], Closed: false));
        var area = new PixelRect(0, 0, 60, 20);
        double Area(StrokeGeometry g) => PathRasterizer.Coverage(PathStroker.Outline(line, g), FillRule.NonZero, area).Sum();
        Assert.Equal(40 * 4, Area(new StrokeGeometry(4)), 1);
        Assert.Equal(44 * 4, Area(new StrokeGeometry(4, LineCap.Square)), 1);
        Assert.Equal(40 * 4 + Math.PI * 4, Area(new StrokeGeometry(4, LineCap.Round)), 0.5);
        // Dash 2 widths on, 1 off: 8 px dashes every 12 px over 40 px: 8 + 8 + 8 + 4 px.
        Assert.Equal(28 * 4, Area(new StrokeGeometry(4, Dashes: [2, 1])), 1);
    }

    [Fact]
    public void Shape_tools_build_photoshops_outlines()
    {
        var rr = ShapeGeometry.RoundedRectangle(0, 0, 20, 20, CornerRadii.All(1.25));
        Assert.Equal(8, rr.Knots.Count);
        Assert.Equal(new PathPoint(1.25, 0), rr.Knots[0].Anchor);
        Assert.Equal(1.25 - 1.25 * ShapeGeometry.Kappa, rr.Knots[0].In.X, 9);
        var e = ShapeGeometry.Ellipse(0, 0, 10, 6);
        Assert.Equal(new PathPoint(5, 0), e.Knots[0].Anchor); // starts at the top, clockwise
        Assert.Equal(new PathPoint(10, 3), e.Knots[1].Anchor);
        var poly = ShapeGeometry.Polygon(0, 0, 100, 100, 6);
        Assert.Equal(6, poly.Knots.Count);
        Assert.Equal(0, poly.Knots.Min(k => k.Anchor.X), 9);
        Assert.Equal(100, poly.Knots.Max(k => k.Anchor.Y), 9);
        Assert.Equal(10, ShapeGeometry.Polygon(0, 0, 100, 100, 5, 0.5).Knots.Count);
        var tri = ShapeGeometry.Triangle(0, 0, 10, 10);
        Assert.Equal(50, PathRasterizer.Rasterize(Path(tri), new PixelRect(0, 0, 10, 10)).Sum(v => v / 255.0), 1);
        var line = ShapeGeometry.Line(new(0, 5), new(20, 5), 2);
        Assert.Equal(40, PathRasterizer.Rasterize(Path(line), new PixelRect(0, 0, 20, 10)).Sum(v => v / 255.0), 1);
        var arrow = ShapeGeometry.Line(new(0, 20), new(60, 20), 2, end: new ArrowHead());
        var ac = PathRasterizer.Rasterize(Path(arrow), new PixelRect(0, 0, 60, 40));
        // Shaft 40×2 plus a head 20 long and 10 wide.
        Assert.Equal(40 * 2 + 20 * 10 / 2.0, ac.Sum(v => v / 255.0), 1);
        foreach (var name in ShapeGeometry.CustomShapeNames)
            Assert.True(PathRasterizer.Rasterize(Path([.. ShapeGeometry.CustomShape(name, 0, 0, 50, 50)]), new PixelRect(0, 0, 50, 50)).Any(v => v > 0), name);
    }

    [Fact]
    public void Rounded_corners_follow_the_radius()
    {
        double r = 10;
        var rr = ShapeGeometry.RoundedRectangle(0, 0, 100, 60, CornerRadii.All(r));
        var c = PathRasterizer.Rasterize(Path(rr), new PixelRect(0, 0, 100, 60));
        Assert.Equal(100 * 60 - 4 * (r * r - Math.PI * r * r / 4), c.Sum(v => v / 255.0), 0);
        var tri = ShapeGeometry.Triangle(0, 0, 100, 100, cornerRadius: 5);
        Assert.Equal(6, tri.Knots.Count);
    }

    [Fact]
    public void A_canvas_sized_shape_rasterizes_fast()
    {
        // 4000×3000: an ellipse filling the canvas and a star, as live editing redraws them. Loose bound: the
        // machine may be busy; typical is a few milliseconds.
        var area = new PixelRect(0, 0, 4000, 3000);
        var path = Path(ShapeGeometry.Ellipse(10, 10, 3990, 2990), ShapeGeometry.Polygon(500, 500, 3500, 2500, 24, 0.4, 0, PathOperation.Exclude));
        var output = new byte[4000 * 3000];
        PathRasterizer.Rasterize(PathRasterizer.Groups(path), false, area, output); // warm up
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 3; i++) PathRasterizer.Rasterize(PathRasterizer.Groups(path), false, area, output);
        double ms = sw.Elapsed.TotalMilliseconds / 3;
        TestContext.Current.SendDiagnosticMessage($"4000x3000 ellipse + star: {ms:F1} ms");
        Assert.True(ms < 1000, $"{ms} ms");
    }

    [Fact]
    public void A_canvas_sized_shape_layer_draws_fast()
    {
        var canvas = new PixelRect(0, 0, 4000, 3000);
        var data = new ShapeLayerData
        {
            Path = Path(ShapeGeometry.Ellipse(20, 20, 3980, 2980)),
            Fill = ShapeContent.Solid(new RgbColor(1, 0, 0)),
            Stroke = new ShapeStroke { Enabled = true, Width = 12, Content = ShapeContent.Solid(new RgbColor(0, 0, 1)) },
        };
        ShapeRenderer.Render(data, canvas, canvas, ColorMode.Rgb, 8);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 3; i++) ShapeRenderer.Render(data, canvas, canvas, ColorMode.Rgb, 8);
        double ms = sw.Elapsed.TotalMilliseconds / 3;
        TestContext.Current.SendDiagnosticMessage($"4000x3000 ellipse with a 12 px inside stroke, drawn: {ms:F1} ms");
        Assert.True(ms < 2000, $"{ms} ms");
    }
}
