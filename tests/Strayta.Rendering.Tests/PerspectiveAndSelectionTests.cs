using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Rendering.Transforms;

namespace Strayta.Rendering.Tests;

/// <summary>Projective maps and resampling (Perspective Crop), and selections following canvas changes.</summary>
public class PerspectiveAndSelectionTests
{
    private static readonly (double X, double Y)[] Quad = [(30, 20), (170, 40), (165, 150), (20, 130)];

    [Fact]
    public void Rect_to_quad_maps_the_corners_and_inverts()
    {
        var map = Projective.RectToQuad(200, 100, Quad);
        (double X, double Y)[] rect = [(0, 0), (200, 0), (200, 100), (0, 100)];
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = map.Apply(rect[i].X, rect[i].Y);
            Assert.Equal(Quad[i].X, x, 9);
            Assert.Equal(Quad[i].Y, y, 9);
            var (u, v) = map.Invert().Apply(Quad[i].X, Quad[i].Y);
            Assert.Equal(rect[i].X, u, 6);
            Assert.Equal(rect[i].Y, v, 6);
        }
        Assert.False(map.IsAffine);
        var parallelogram = Projective.RectToQuad(10, 10, [(0, 0), (20, 5), (25, 25), (5, 20)]);
        Assert.True(parallelogram.IsAffine);
    }

    [Fact]
    public void Linearize_agrees_to_first_order()
    {
        var map = Projective.RectToQuad(200, 100, Quad);
        var lin = map.Linearize(100, 50);
        var (x0, y0) = map.Apply(100, 50);
        var (lx, ly) = lin.Apply(100, 50);
        Assert.Equal(x0, lx, 9);
        Assert.Equal(y0, ly, 9);
        var (x1, y1) = map.Apply(100.01, 50);
        var (lx1, ly1) = lin.Apply(100.01, 50);
        Assert.Equal(x1, lx1, 6);
        Assert.Equal(y1, ly1, 6);
    }

    /// <summary>A white image with a dark quadrilateral outline-free fill: the shape to straighten.</summary>
    private static Document PhotoOfARectangle()
    {
        const int w = 200, h = 170;
        var doc = new Document(w, h, ColorMode.Rgb, 8);
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(w, h, 8)).ToArray();
        // Inside the quad: a checker of 4×4 cells in the straightened space, so straightness can be checked.
        var toRect = Projective.RectToQuad(8, 8, Quad).Invert();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (u, v) = toRect.Apply(x + 0.5, y + 0.5);
                byte value = u is >= 0 and < 8 && v is >= 0 and < 8 ? (((int)u + (int)v) % 2 == 0 ? (byte)20 : (byte)230) : (byte)128;
                foreach (var p in planes) p.Data[y * w + x] = value;
            }
        doc.Root.Add(new PixelLayer { Name = "Background", Bounds = doc.Bounds, Pixels = new Raster(ColorMode.Rgb, planes, null) });
        return doc;
    }

    [Fact]
    public void Perspective_crop_straightens_the_quadrilateral_onto_an_upright_canvas()
    {
        var doc = PhotoOfARectangle();
        var (w, h) = CanvasOperations.PerspectiveSize(Quad);
        Assert.Equal((146, 110), (w, h)); // the longer of each pair of opposite sides
        var change = CanvasOperations.PerspectiveCrop(doc, Quad, w, h);
        CanvasOperations.Apply(doc, change);

        var bg = (PixelLayer)doc.Root.Children[0];
        Assert.Equal(doc.Bounds, bg.Bounds);
        Assert.Null(bg.Pixels!.Alpha);
        Assert.NotNull(change.Perspective);
        // Cell centers of the 8×8 checker now sit on a regular grid.
        var red = bg.Pixels.ColorPlanes[0].Data;
        for (int cy = 0; cy < 8; cy++)
            for (int cx = 0; cx < 8; cx++)
            {
                int x = (int)((cx + 0.5) * w / 8), y = (int)((cy + 0.5) * h / 8);
                byte expected = (cx + cy) % 2 == 0 ? (byte)20 : (byte)230;
                Assert.InRange(red[y * w + x], expected - 12, expected + 12);
            }
    }

    [Fact]
    public void Projective_resampler_matches_the_affine_one_for_affine_maps()
    {
        var raster = PhotoOfARectangle().Root.Children.OfType<PixelLayer>().Single().Pixels!;
        var a = new Affine(0.8, 0.2, -0.1, 0.9, 12, 7);
        var (p1, b1) = ProjectiveResampler.TransformRaster(raster, PixelRect.FromSize(200, 170), Projective.FromAffine(a), ResampleFilter.Bicubic);
        var (p2, b2) = Resampler.TransformRaster(raster, PixelRect.FromSize(200, 170), a, ResampleFilter.Bicubic);
        Assert.Equal(b2, b1);
        Assert.Equal(p2!.ColorPlanes[0].Data, p1!.ColorPlanes[0].Data);
    }

    [Fact]
    public void Rectangular_selections_stay_rectangles_through_moves_and_scales()
    {
        var sel = SelectionMask.Rectangle(new PixelRect(10, 20, 50, 60), PixelRect.FromSize(100, 100));
        var moved = SelectionTransform.Apply(sel, Affine.Translation(-5, 7), PixelRect.FromSize(80, 80))!;
        Assert.True(moved.IsRectangular);
        Assert.Equal(new PixelRect(5, 27, 45, 67), moved.Bounds);
        var halved = SelectionTransform.Apply(sel, Affine.Scale(0.5, 0.5), PixelRect.FromSize(50, 50))!;
        Assert.Equal(new PixelRect(5, 10, 25, 30), halved.Bounds);
        Assert.Null(SelectionTransform.Apply(sel, Affine.Translation(-200, 0), PixelRect.FromSize(50, 50)));
    }

    [Fact]
    public void Selections_turn_with_a_rotated_crop()
    {
        var canvas = PixelRect.FromSize(100, 100);
        var sel = SelectionMask.Rectangle(new PixelRect(40, 40, 60, 60), canvas);
        var (sin, cos) = Math.SinCos(Math.PI / 4);
        // Turn 45° about the center.
        var map = Affine.Translation(-50, -50).Then(new Affine(cos, -sin, sin, cos, 0, 0)).Then(Affine.Translation(50, 50));
        var turned = SelectionTransform.Apply(sel, map, canvas)!;
        Assert.False(turned.IsRectangular);
        Assert.Equal(255, turned.CoverageAt(50, 50));
        Assert.Equal(0, turned.CoverageAt(41, 41)); // the old corner area is outside the diamond now
        Assert.True(turned.CoverageAt(50, 37) > 128); // the diamond's top point reaches 50 - 14.1
    }

    [Fact]
    public void Perspective_selection_follows_the_crop()
    {
        var doc = PhotoOfARectangle();
        var sel = SelectionMask.Rectangle(doc.Bounds, doc.Bounds);
        var (w, h) = CanvasOperations.PerspectiveSize(Quad);
        var map = Projective.RectToQuad(w, h, Quad).Invert();
        var moved = SelectionTransform.Apply(sel, map, PixelRect.FromSize(w, h))!;
        Assert.Equal(PixelRect.FromSize(w, h), moved.Bounds);
        Assert.Equal(255, moved.CoverageAt(w / 2, h / 2));
    }
}
