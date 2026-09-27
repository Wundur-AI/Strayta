using System.Numerics;
using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

public class SelectionTests
{
    private static readonly PixelRect Canvas = new(0, 0, 100, 80);

    private static int Count(SelectionMask? s, byte min = 128)
    {
        int n = 0;
        for (int y = Canvas.Top; y < Canvas.Bottom; y++)
            for (int x = Canvas.Left; x < Canvas.Right; x++)
                if (s is not null && s.CoverageAt(x, y) >= min) n++;
        return n;
    }

    private static double Area(SelectionMask? s)
    {
        double sum = 0;
        for (int y = Canvas.Top; y < Canvas.Bottom; y++)
            for (int x = Canvas.Left; x < Canvas.Right; x++)
                sum += (s?.CoverageAt(x, y) ?? 0) / 255.0;
        return sum;
    }

    [Fact]
    public void Rectangles_are_hard_edged_and_clipped_to_the_canvas()
    {
        var s = SelectionMask.Rectangle(new PixelRect(90, -5, 120, 10), Canvas)!;
        Assert.Equal(new PixelRect(90, 0, 100, 10), s.Bounds);
        Assert.True(s.IsRectangular);
        Assert.Equal(255, s.CoverageAt(95, 5));
        Assert.Equal(0, s.CoverageAt(89, 5));
        Assert.Null(SelectionMask.Rectangle(new PixelRect(200, 0, 300, 10), Canvas));
    }

    [Fact]
    public void Ellipses_are_antialiased_and_have_the_right_area()
    {
        var s = SelectionMask.Ellipse(new PixelRect(10, 10, 70, 50), Canvas)!;
        Assert.Equal(255, s.CoverageAt(40, 30));
        Assert.Equal(0, s.CoverageAt(11, 11));
        Assert.Equal(Math.PI * 30 * 20, Area(s), 0.01 * Math.PI * 30 * 20);
        Assert.Contains(Enumerable.Range(10, 60), x => s.CoverageAt(x, 12) is > 0 and < 255); // soft edge pixels exist
        // Symmetric left/right and top/bottom.
        Assert.Equal(s.CoverageAt(12, 25), s.CoverageAt(67, 25));
        Assert.Equal(s.CoverageAt(25, 12), s.CoverageAt(25, 47));
    }

    [Fact]
    public void Polygons_on_pixel_corners_match_rectangles_and_triangles_have_the_right_area()
    {
        var square = SelectionMask.Polygon([new(10, 10), new(30, 10), new(30, 20), new(10, 20)], Canvas)!;
        Assert.True(square.IsRectangular);
        Assert.Equal(new PixelRect(10, 10, 30, 20), square.Bounds);

        var triangle = SelectionMask.Polygon([new(0, 0), new(60, 0), new(0, 40)], Canvas);
        Assert.Equal(60 * 40 / 2.0, Area(triangle), 2.0);
        Assert.Equal(255, triangle!.CoverageAt(5, 5));
        Assert.Equal(0, triangle.CoverageAt(50, 30));
    }

    [Fact]
    public void Self_overlapping_lassos_keep_every_loop_selected()
    {
        // A square traced twice in the same direction: winding 2 everywhere inside, still selected.
        Vector2[] loop = [new(10, 10), new(40, 10), new(40, 40), new(10, 40)];
        var s = SelectionMask.Polygon([.. loop, .. loop], Canvas);
        Assert.Equal(30 * 30, Count(s));
    }

    [Fact]
    public void Add_subtract_and_intersect_combine_like_photoshop()
    {
        var a = SelectionMask.Rectangle(new PixelRect(0, 0, 50, 50), Canvas);
        var b = SelectionMask.Rectangle(new PixelRect(25, 25, 75, 75), Canvas);

        Assert.Equal(2 * 2500 - 625, Count(SelectionMask.Combine(a, b, SelectionMode.Add)));
        Assert.Equal(2500 - 625, Count(SelectionMask.Combine(a, b, SelectionMode.Subtract)));
        var both = SelectionMask.Combine(a, b, SelectionMode.Intersect)!;
        Assert.Equal(new PixelRect(25, 25, 50, 50), both.Bounds);
        Assert.True(both.IsRectangular);
        Assert.Same(b, SelectionMask.Combine(a, b, SelectionMode.Replace));

        Assert.Same(b, SelectionMask.Combine(null, b, SelectionMode.Add));
        Assert.Null(SelectionMask.Combine(null, b, SelectionMode.Subtract));
        Assert.Null(SelectionMask.Combine(a, a, SelectionMode.Subtract)); // nothing left means no selection
        Assert.Null(SelectionMask.Combine(a, SelectionMask.Rectangle(new PixelRect(60, 60, 70, 70), Canvas), SelectionMode.Intersect));
    }

    [Fact]
    public void Subtracting_a_soft_shape_leaves_partial_coverage_and_trims_bounds()
    {
        var all = SelectionMask.All(Canvas);
        var hole = SelectionMask.Ellipse(new PixelRect(20, 20, 60, 60), Canvas);
        var s = SelectionMask.Combine(all, hole, SelectionMode.Subtract)!;
        Assert.Equal(0, s.CoverageAt(40, 40));
        Assert.Equal(255, s.CoverageAt(0, 0));
        Assert.Equal(Canvas.Width * Canvas.Height - Math.PI * 400, Area(s), 10.0);

        // Subtracting the left half of a rectangle trims the bounds to what is left and stays rectangular.
        var rect = SelectionMask.Rectangle(new PixelRect(10, 10, 50, 50), Canvas);
        var right = SelectionMask.Combine(rect, SelectionMask.Rectangle(new PixelRect(0, 0, 30, 80), Canvas), SelectionMode.Subtract)!;
        Assert.Equal(new PixelRect(30, 10, 50, 50), right.Bounds);
        Assert.True(right.IsRectangular);
    }

    [Fact]
    public void Invert_selects_the_rest_of_the_canvas()
    {
        var s = SelectionMask.Rectangle(new PixelRect(0, 0, 100, 40), Canvas);
        var inv = SelectionMask.Invert(s, Canvas)!;
        Assert.Equal(new PixelRect(0, 40, 100, 80), inv.Bounds);
        Assert.True(inv.IsRectangular);
        Assert.Null(SelectionMask.Invert(SelectionMask.All(Canvas), Canvas));
        Assert.Equal(Canvas, SelectionMask.Invert(null, Canvas)!.Bounds);

        var ellipse = SelectionMask.Ellipse(new PixelRect(10, 10, 60, 60), Canvas);
        var twice = SelectionMask.Invert(SelectionMask.Invert(ellipse, Canvas), Canvas)!;
        Assert.Equal(ellipse!.Bounds, twice.Bounds);
        Assert.Equal(ellipse.CoverageAt(12, 30), twice.CoverageAt(12, 30));
    }

    [Fact]
    public void CopyRow_reads_coverage_with_zeros_outside()
    {
        var s = SelectionMask.Ellipse(new PixelRect(10, 10, 30, 30), Canvas)!;
        var row = new byte[40];
        s.CopyRow(20, 0, row);
        for (int x = 0; x < 40; x++) Assert.Equal(s.CoverageAt(x, 20), row[x]);
        s.CopyRow(5, 0, row);
        Assert.All(row, v => Assert.Equal(0, v));
    }
}

public class SelectionOutlineTests
{
    private static readonly PixelRect Canvas = new(0, 0, 64, 64);

    [Fact]
    public void A_rectangle_outline_is_its_four_corners()
    {
        var loops = SelectionOutline.Trace(SelectionMask.Rectangle(new PixelRect(4, 5, 20, 30), Canvas));
        var loop = Assert.Single(loops);
        Assert.Equal([new Vector2(4, 5), new(20, 5), new(20, 30), new(4, 30)], loop);
    }

    [Fact]
    public void A_ring_has_an_outer_and_an_inner_outline()
    {
        var ring = SelectionMask.Combine(
            SelectionMask.Rectangle(new PixelRect(0, 0, 40, 40), Canvas),
            SelectionMask.Rectangle(new PixelRect(10, 10, 30, 30), Canvas), SelectionMode.Subtract);
        var loops = SelectionOutline.Trace(ring);
        Assert.Equal(2, loops.Count);
        Assert.All(loops, l => Assert.Equal(4, l.Length));
        Assert.Contains(loops, l => l.Contains(new Vector2(10, 10)));
    }

    [Fact]
    public void Diagonally_touching_pixels_trace_as_separate_squares()
    {
        var a = SelectionMask.Rectangle(new PixelRect(0, 0, 8, 8), Canvas);
        var b = SelectionMask.Rectangle(new PixelRect(8, 8, 16, 16), Canvas);
        var loops = SelectionOutline.Trace(SelectionMask.Combine(a, b, SelectionMode.Add));
        Assert.Equal(2, loops.Count);
        Assert.All(loops, l => Assert.Equal(4, l.Length));
    }

    [Fact]
    public void Ellipse_outlines_close_and_coarser_factors_have_fewer_vertices()
    {
        var s = SelectionMask.Ellipse(new PixelRect(2, 2, 62, 62), Canvas);
        var fine = Assert.Single(SelectionOutline.Trace(s));
        var coarse = Assert.Single(SelectionOutline.Trace(s, 4));
        Assert.True(coarse.Length < fine.Length / 2, $"{coarse.Length} vs {fine.Length}");
        // Every vertex lies on a pixel corner inside the selection's bounds.
        Assert.All(fine, p => Assert.True(p.X >= 2 && p.X <= 62 && p.Y >= 2 && p.Y <= 62 && p.X == MathF.Floor(p.X)));
        // Consecutive vertices are joined by horizontal or vertical runs.
        for (int i = 0; i < fine.Length; i++)
        {
            var p = fine[i];
            var q = fine[(i + 1) % fine.Length];
            Assert.True(p.X == q.X || p.Y == q.Y);
        }
    }

    [Fact]
    public void No_selection_has_no_outline() => Assert.Empty(SelectionOutline.Trace(null));
}

public class SelectionPaintingTests
{
    private static readonly PixelRect Canvas = new(0, 0, 40, 40);

    private static PixelLayer Opaque(byte r, byte g, byte b)
    {
        Plane P(byte v) => new(40, 40, 8, Enumerable.Repeat(v, 1600).ToArray());
        return new PixelLayer { Bounds = Canvas, Pixels = new Raster(ColorMode.Rgb, [P(r), P(g), P(b)], null) };
    }

    [Fact]
    public void Strokes_only_paint_inside_the_selection()
    {
        var clip = SelectionMask.Rectangle(new PixelRect(0, 0, 20, 40), Canvas);
        var layer = new PixelLayer();
        var stroke = new PaintStroke(layer, new BrushSettings(10, 1f, 1f), new RgbColor(1, 0, 0), false, Canvas, clip);
        stroke.StrokeTo(5, 20);
        stroke.StrokeTo(35, 20);

        Assert.Equal(1f, stroke.CoverageAt(10, 20));
        Assert.Equal(0f, stroke.CoverageAt(25, 20));
        Assert.True(stroke.Bounds.Right <= 20, $"stroke bounds {stroke.Bounds} leave the selection");

        var (pixels, bounds) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        Assert.True(bounds.Right <= 20);
        Assert.Equal(255, pixels!.Alpha!.Data[(20 - bounds.Top) * bounds.Width + (10 - bounds.Left)]);
    }

    [Fact]
    public void Soft_selections_scale_stroke_coverage()
    {
        var clip = SelectionMask.Ellipse(new PixelRect(0, 0, 40, 40), Canvas)!;
        var stroke = new PaintStroke(new PixelLayer(), new BrushSettings(80, 1f, 1f), new RgbColor(1, 0, 0), false, Canvas, clip);
        stroke.StrokeTo(20, 20);
        for (int x = 0; x < 40; x++)
            Assert.Equal(clip.CoverageAt(x, 3) / 255f, stroke.CoverageAt(x, 3), 3);
    }

    [Fact]
    public void Fill_blends_the_color_into_the_selection_only()
    {
        var layer = Opaque(0, 0, 255);
        var sel = SelectionMask.Rectangle(new PixelRect(10, 10, 20, 20), Canvas)!;
        var (pixels, bounds) = SelectionPainter.Fill(layer, sel, new RgbColor(1, 0, 0), ColorMode.Rgb, 8);
        Assert.Equal(Canvas, bounds);
        Assert.Null(pixels!.Alpha); // an opaque layer stays opaque
        Assert.Equal(255, pixels.ColorPlanes[0].Data[15 * 40 + 15]);
        Assert.Equal(0, pixels.ColorPlanes[2].Data[15 * 40 + 15]);
        Assert.Equal(0, pixels.ColorPlanes[0].Data[5 * 40 + 5]);
        Assert.Equal(0, layer.Pixels!.ColorPlanes[0].Data[15 * 40 + 15]); // the source raster is untouched

        // Filling an empty layer creates pixels covering just the selection.
        var (created, createdBounds) = SelectionPainter.Fill(new PixelLayer(), sel, new RgbColor(0, 1, 0), ColorMode.Rgb, 8);
        Assert.Equal(sel.Bounds, createdBounds);
        Assert.All(created!.Alpha!.Data, a => Assert.Equal(255, a));
    }

    [Fact]
    public void Clear_makes_the_selection_transparent_or_background_colored()
    {
        var sel = SelectionMask.Rectangle(new PixelRect(10, 10, 20, 20), Canvas)!;
        var background = Opaque(0, 0, 255);
        var (bgPixels, _) = SelectionPainter.Clear(background, sel, new RgbColor(1, 1, 1), ColorMode.Rgb, 8);
        Assert.Null(bgPixels!.Alpha);
        Assert.Equal(255, bgPixels.ColorPlanes[0].Data[15 * 40 + 15]); // filled with white

        var (withAlpha, _) = SelectionPainter.Fill(new PixelLayer(), SelectionMask.All(Canvas), new RgbColor(1, 0, 0), ColorMode.Rgb, 8);
        var layer = new PixelLayer { Bounds = Canvas, Pixels = withAlpha };
        var (cleared, bounds) = SelectionPainter.Clear(layer, sel, default, ColorMode.Rgb, 8);
        Assert.Equal(Canvas, bounds);
        Assert.Equal(0, cleared!.Alpha!.Data[15 * 40 + 15]);
        Assert.Equal(255, cleared.Alpha.Data[5 * 40 + 5]);
    }

    [Fact]
    public void Extract_copies_the_selected_pixels_with_soft_edges_in_alpha()
    {
        var layer = Opaque(10, 20, 30);
        var sel = SelectionMask.Ellipse(new PixelRect(-10, 10, 20, 30), Canvas)!;
        var (pixels, bounds) = SelectionPainter.Extract(layer, sel)!.Value;
        Assert.Equal(sel.Bounds, bounds);
        int w = bounds.Width;
        Assert.Equal(10, pixels.ColorPlanes[0].Data[(20 - bounds.Top) * w + (5 - bounds.Left)]);
        for (int y = bounds.Top; y < bounds.Bottom; y++)
            for (int x = bounds.Left; x < bounds.Right; x++)
                Assert.Equal(sel.CoverageAt(x, y), pixels.Alpha!.Data[(y - bounds.Top) * w + (x - bounds.Left)]);

        Assert.Equal(Canvas, SelectionPainter.Extract(layer, null)!.Value.Bounds);
        Assert.Null(SelectionPainter.Extract(new PixelLayer(), sel));
    }
}
