using System.Diagnostics;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

/// <summary>Select › Modify: Expand, Contract, Feather, Smooth, Border.</summary>
public class SelectionModifyTests(ITestOutputHelper output)
{
    private static readonly PixelRect Canvas = PixelRect.FromSize(40, 40);

    /// <summary>A selection from a predicate on pixel positions (255 where true), over the test canvas.</summary>
    private static SelectionMask? Shape(Func<int, int, byte> coverage, PixelRect? canvas = null)
    {
        var c = canvas ?? Canvas;
        var grid = new byte[c.Width * c.Height];
        for (int y = 0; y < c.Height; y++)
            for (int x = 0; x < c.Width; x++) grid[y * c.Width + x] = coverage(x, y);
        return SelectionMask.FromCoverage(c, grid);
    }

    private static int Count(SelectionMask? s, byte min = 128) => Synthetic.Count(s, Canvas, min);

    [Fact]
    public void Expand_grows_by_a_disc()
    {
        var one = SelectionMask.Rectangle(new PixelRect(20, 20, 21, 21), Canvas);
        // Radius 1: offsets with dx² + dy² ≤ 1.5², the full 3×3 square. Radius 2: the 5×5 square without its corners.
        Assert.Equal(new PixelRect(19, 19, 22, 22), SelectionModify.Expand(one, 1, Canvas)!.Bounds);
        Assert.True(SelectionModify.Expand(one, 1, Canvas)!.IsRectangular);
        var two = SelectionModify.Expand(one, 2, Canvas)!;
        Assert.Equal(21, Count(two));
        Assert.Equal(0, two.CoverageAt(18, 18));
        Assert.Equal(255, two.CoverageAt(19, 18));

        // A rectangle keeps its sides and gets rounded corners.
        var rect = SelectionModify.Expand(SelectionMask.Rectangle(new PixelRect(10, 10, 20, 20), Canvas), 3, Canvas)!;
        Assert.Equal(new PixelRect(7, 7, 23, 23), rect.Bounds);
        Assert.Equal(255, rect.CoverageAt(7, 15));
        Assert.Equal(0, rect.CoverageAt(7, 7));   // offset (3, 3): 18 > 12
        Assert.Equal(255, rect.CoverageAt(8, 8)); // offset (2, 2): 8 ≤ 12
        Assert.Equal(0, rect.CoverageAt(7, 8));   // offset (3, 2): 13 > 12
    }

    [Fact]
    public void Expand_moves_soft_edges_without_hardening_them()
    {
        // Columns 10..19 selected, with a half-selected column 20.
        var soft = Shape((x, y) => y is >= 10 and < 30 ? x is >= 10 and < 20 ? (byte)255 : x == 20 ? (byte)128 : (byte)0 : (byte)0);
        var grown = SelectionModify.Expand(soft, 2, Canvas)!;
        // Two pixels right of the last solid column (19) is solid; the half-selected column moves out with it.
        Assert.Equal(255, grown.CoverageAt(21, 20));
        Assert.Equal(128, grown.CoverageAt(22, 20));
        Assert.Equal(0, grown.CoverageAt(23, 20));
        Assert.Equal(255, grown.CoverageAt(15, 31));
        Assert.Equal(0, grown.CoverageAt(15, 32));
    }

    [Fact]
    public void Contract_insets_a_rectangle_and_respects_canvas_bounds()
    {
        var rect = SelectionMask.Rectangle(new PixelRect(10, 10, 30, 30), Canvas);
        var inset = SelectionModify.Contract(rect, 3, Canvas)!;
        Assert.True(inset.IsRectangular);
        Assert.Equal(new PixelRect(13, 13, 27, 27), inset.Bounds);
        Assert.Null(SelectionModify.Contract(rect, 10, Canvas));

        // Touching the canvas: kept along the canvas edges unless the effect applies at the canvas bounds.
        var corner = SelectionMask.Rectangle(new PixelRect(0, 0, 20, 20), Canvas);
        Assert.Equal(new PixelRect(0, 0, 17, 17), SelectionModify.Contract(corner, 3, Canvas)!.Bounds);
        Assert.Equal(new PixelRect(3, 3, 17, 17), SelectionModify.Contract(corner, 3, Canvas, applyAtCanvasBounds: true)!.Bounds);
    }

    [Fact]
    public void Contract_erodes_arbitrary_shapes_by_a_disc()
    {
        // An L: a 20×10 bar with a 10×20 leg.
        var l = Shape((x, y) => (x is >= 10 and < 30 && y is >= 10 and < 20) || (x is >= 10 and < 20 && y is >= 10 and < 30) ? (byte)255 : (byte)0);
        Assert.False(l!.IsRectangular);
        var eroded = SelectionModify.Contract(l, 2, Canvas)!;
        Assert.Equal(new PixelRect(12, 12, 28, 28), eroded.Bounds);
        Assert.Equal(255, eroded.CoverageAt(27, 17));
        Assert.Equal(0, eroded.CoverageAt(27, 18));
        Assert.Equal(255, eroded.CoverageAt(20, 17)); // the inner corner stays filled up to 2 pixels from both edges
        Assert.Equal(255, eroded.CoverageAt(17, 20));
        Assert.Equal(255, eroded.CoverageAt(18, 18)); // the disc cannot reach (20, 20) from here
        Assert.Equal(0, eroded.CoverageAt(19, 19));
        // Expanding back does not restore the inner corner's sharpness, as with any opening, but restores the sides.
        var reopened = SelectionModify.Expand(eroded, 2, Canvas)!;
        Assert.Equal(new PixelRect(10, 10, 30, 30), reopened.Bounds);
    }

    [Fact]
    public void Feather_blurs_symmetrically_and_keeps_the_amount_selected()
    {
        var rect = SelectionMask.Rectangle(new PixelRect(12, 12, 28, 28), Canvas)!;
        var feathered = SelectionModify.Feather(rect, 2, Canvas)!;
        Assert.Equal(255, feathered.CoverageAt(20, 20));
        // Either side of the edge adds up to one fully selected pixel, and the ramp is monotonic.
        Assert.InRange(feathered.CoverageAt(11, 20) + feathered.CoverageAt(12, 20), 253, 257);
        Assert.InRange(feathered.CoverageAt(12, 20), 150, 200);
        for (int x = 5; x < 20; x++) Assert.True(feathered.CoverageAt(x, 20) <= feathered.CoverageAt(x + 1, 20), $"monotonic at {x}");
        long before = Sum(rect), after = Sum(feathered);
        Assert.InRange(after, before - before / 100, before + before / 100);
        // Large radii use box passes; the midpoint stays at the edge.
        var wide = SelectionModify.Feather(SelectionMask.Rectangle(new PixelRect(0, 0, 200, 100), PixelRect.FromSize(400, 100)), 20, PixelRect.FromSize(400, 100))!;
        Assert.InRange(wide.CoverageAt(199, 50) + wide.CoverageAt(200, 50), 250, 260);
        Assert.Equal(255, wide.CoverageAt(0, 0)); // not applied at the canvas bounds: the edges along the canvas stay
        var applied = SelectionModify.Feather(SelectionMask.Rectangle(new PixelRect(0, 0, 200, 100), PixelRect.FromSize(400, 100)), 20, PixelRect.FromSize(400, 100), applyAtCanvasBounds: true)!;
        Assert.InRange((int)applied.CoverageAt(0, 50), 100, 160);

        static long Sum(SelectionMask s)
        {
            long n = 0;
            for (int y = 0; y < 40; y++)
                for (int x = 0; x < 40; x++) n += s.CoverageAt(x, y);
            return n;
        }
    }

    [Fact]
    public void Smooth_removes_specks_and_notches_but_keeps_straight_edges()
    {
        // A square with a one-pixel notch in its top edge, plus a stray pixel.
        var s = Shape((x, y) => (x is >= 10 and < 30 && y is >= 10 and < 30 && !(x == 20 && y == 10)) || (x == 3 && y == 3) ? (byte)255 : (byte)0);
        var smooth = SelectionModify.Smooth(s, 2, Canvas)!;
        Assert.Equal(0, smooth.CoverageAt(3, 3));
        Assert.True(smooth.CoverageAt(20, 10) >= 128, "notch filled");
        Assert.True(smooth.CoverageAt(20, 9) < 128 && smooth.CoverageAt(10, 20) >= 128 && smooth.CoverageAt(9, 20) < 128, "straight edges stay put");
        Assert.Equal(255, smooth.CoverageAt(20, 20));
        Assert.True(smooth.CoverageAt(10, 10) < 128, "corners round off");
    }

    [Fact]
    public void Border_selects_a_band_centered_on_the_edge()
    {
        var canvas = PixelRect.FromSize(100, 100);
        var rect = SelectionMask.Rectangle(new PixelRect(20, 20, 80, 80), canvas);
        var border = SelectionModify.Border(rect, 10, canvas)!;
        Assert.True(border.CoverageAt(19, 50) >= 250 && border.CoverageAt(20, 50) >= 250, "the band's middle is on the edge");
        Assert.True(border.CoverageAt(30, 50) < 128 && border.CoverageAt(29, 50) < 200 && border.CoverageAt(10, 50) < 128);
        Assert.Equal(0, border.CoverageAt(50, 50));
        Assert.Equal(0, border.CoverageAt(0, 50));
        Assert.True(border.CoverageAt(16, 50) is > 0 and < 255 || border.CoverageAt(14, 50) is > 0 and < 255, "soft sides");

        // Along the canvas edge no border appears unless applied at the canvas bounds.
        var left = SelectionMask.Rectangle(new PixelRect(0, 20, 50, 80), canvas);
        Assert.Equal(0, SelectionModify.Border(left, 10, canvas)!.CoverageAt(0, 50));
        Assert.True(SelectionModify.Border(left, 10, canvas, applyAtCanvasBounds: true)!.CoverageAt(1, 50) > 200);
    }

    [Fact]
    public void Nothing_selected_stays_nothing()
    {
        Assert.Null(SelectionModify.Expand(null, 5, Canvas));
        Assert.Null(SelectionModify.Contract(null, 5, Canvas));
        Assert.Null(SelectionModify.Feather(null, 5, Canvas));
        Assert.Null(SelectionModify.Smooth(null, 5, Canvas));
        Assert.Null(SelectionModify.Border(null, 5, Canvas));
    }

    [Trait("Category", "Performance")] // timed: runs in the separate performance pass (Directory.Build.props)
    [Fact]
    public void Large_selections_modify_quickly()
    {
        var canvas = PixelRect.FromSize(4000, 3000);
        var ellipse = SelectionMask.Ellipse(new PixelRect(300, 200, 3700, 2800), canvas);
        var ops = new (string Name, Func<SelectionMask?> Run)[]
        {
            ("Expand 20", () => SelectionModify.Expand(ellipse, 20, canvas)),
            ("Expand 100", () => SelectionModify.Expand(ellipse, 100, canvas)),
            ("Contract 20", () => SelectionModify.Contract(ellipse, 20, canvas)),
            ("Feather 2", () => SelectionModify.Feather(ellipse, 2, canvas)),
            ("Feather 50", () => SelectionModify.Feather(ellipse, 50, canvas)),
            ("Smooth 10", () => SelectionModify.Smooth(ellipse, 10, canvas)),
            ("Border 20", () => SelectionModify.Border(ellipse, 20, canvas)),
            // Photoshop's largest amounts.
            ("Expand 500", () => SelectionModify.Expand(ellipse, 500, canvas)),
            ("Contract 500", () => SelectionModify.Contract(ellipse, 500, canvas)),
            ("Smooth 500", () => SelectionModify.Smooth(ellipse, 500, canvas)),
            ("Feather 1000", () => SelectionModify.Feather(ellipse, 1000, canvas)),
            ("Border 200", () => SelectionModify.Border(ellipse, 200, canvas)),
        };
        foreach (var (name, run) in ops)
        {
            run(); // warm up
            var times = new List<long>();
            for (int i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                Assert.NotNull(run());
                times.Add(sw.ElapsedMilliseconds);
            }
            times.Sort();
            output.WriteLine($"{name}: median {times[1]} ms (min {times[0]}, max {times[2]})");
            Assert.True(times[1] < 2000, $"{name} took {times[1]} ms");
        }
    }
}
