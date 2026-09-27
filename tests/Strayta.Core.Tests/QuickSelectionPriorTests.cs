using System.Numerics;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

/// <summary>Quick Selection steered by a prior (as Object-Aware mode does with SAM), with synthetic priors.</summary>
public class QuickSelectionPriorTests
{
    private const int W = 300, H = 240;
    private const float Cx = 150, Cy = 120, R = 80;

    private static bool InDisc(int x, int y, float r = R) => (x + 0.5f - Cx) * (x + 0.5f - Cx) + (y + 0.5f - Cy) * (y + 0.5f - Cy) < r * r;

    /// <summary>A prior shaped like a disc of radius <paramref name="r"/> around the test disc's center.</summary>
    private static QuickSelectionPrior DiscPrior(QuickSelectionImage image, float r, float uncertainty = 6, float trust = 1) =>
        QuickSelectionPrior.Sample(image, p => r - Vector2.Distance(p, new Vector2(Cx, Cy)), uncertainty, trust);

    /// <summary>
    /// A red ball lit from the left: bright on the left, falling to the dark brown of the background on the right,
    /// where the only edge is the (weak) step in shading, on a noisy background.
    /// </summary>
    private static SampleImage ShadedBall(bool edge = true) => Synthetic.Image(W, H, (x, y) =>
    {
        int n0 = Synthetic.Noise(x, y, 0, 4), n1 = Synthetic.Noise(x, y, 1, 4), n2 = Synthetic.Noise(x, y, 2, 4);
        if (!InDisc(x, y)) return (70 + n0, 40 + n1, 35 + n2);
        float t = Math.Clamp((x + 0.5f - (Cx - R)) / (2 * R), 0, 1); // 0 at the left edge, 1 at the right
        var (r, g, b) = (230 - 150 * t, 90 - 45 * t, 70 - 30 * t);
        if (!edge && t > 0.5f) (r, g, b) = (70, 40, 35);             // the right half is the background's color
        return ((int)r + n0, (int)g + n1, (int)b + n2);
    });

    private static void Brush(QuickSelectionStroke stroke, params Vector2[] path)
    {
        stroke.AddSegment(path[0], path[0]);
        for (int i = 1; i < path.Length; i++)
        {
            int steps = Math.Max(1, (int)(Vector2.Distance(path[i - 1], path[i]) / 4));
            for (int s = 1; s <= steps; s++)
                stroke.AddSegment(Vector2.Lerp(path[i - 1], path[i], (s - 1f) / steps), Vector2.Lerp(path[i - 1], path[i], (float)s / steps));
        }
    }

    // A short stroke over the lit side, as someone starting to paint the ball would.
    private static readonly Vector2[] LitStroke = [new(95, 110), new(120, 125)];

    private static (QuickSelectionStroke Stroke, SelectionMask? Mask) Select(SampleImage image, QuickSelectionPrior? prior = null, bool priorFirst = false)
    {
        var working = QuickSelectionImage.Build(image);
        var stroke = new QuickSelectionStroke(working, 16, null, SelectionMode.Replace);
        if (priorFirst) stroke.SetPrior(prior);
        Brush(stroke, LitStroke);
        if (!priorFirst && prior is not null) stroke.SetPrior(prior);
        return (stroke, stroke.Finish(autoEnhance: false));
    }

    [Fact]
    public void A_prior_fills_a_shaded_object_that_plain_growth_stops_short_on()
    {
        var image = ShadedBall();
        var working = QuickSelectionImage.Build(image);
        double plain = Synthetic.IoU(Select(image).Mask, image.Bounds, (x, y) => InDisc(x, y));
        var (stroke, mask) = Select(image, DiscPrior(working, R));
        double steered = Synthetic.IoU(mask, image.Bounds, (x, y) => InDisc(x, y));
        Assert.True(stroke.HasPrior);
        Assert.True(plain < 0.8, $"plain IoU {plain:F3}: the test image should defeat plain growth");
        Assert.True(steered > 0.97, $"with the prior IoU {steered:F3} (plain {plain:F3})");
    }

    [Fact]
    public void A_prior_set_before_brushing_applies_to_the_first_points()
    {
        var image = ShadedBall();
        var (stroke, mask) = Select(image, DiscPrior(QuickSelectionImage.Build(image), R), priorFirst: true);
        Assert.True(stroke.HasPrior);
        Assert.True(Synthetic.IoU(mask, image.Bounds, (x, y) => InDisc(x, y)) > 0.97);
    }

    [Fact]
    public void An_outline_a_little_too_large_still_snaps_to_the_image_edge()
    {
        // The model's outline is 4 px outside the ball (under the uncertainty of 6): where the ball has an edge (its
        // lit side; the shadow side fades into the background) that edge decides.
        var image = ShadedBall();
        var (_, mask) = Select(image, DiscPrior(QuickSelectionImage.Build(image), R + 4));
        double iou = Synthetic.IoU(mask, image.Bounds, (x, y) => InDisc(x, y));
        Assert.True(iou > 0.97, $"IoU {iou:F3}");
        Assert.Equal(0, Count(mask, (x, y) => x < Cx && !InDisc(x, y, R + 1.5f))); // nothing beyond the lit edge
    }

    [Fact]
    public void An_outline_a_little_too_small_still_reaches_the_image_edge()
    {
        var image = ShadedBall();
        var (_, mask) = Select(image, DiscPrior(QuickSelectionImage.Build(image), R - 3));
        double iou = Synthetic.IoU(mask, image.Bounds, (x, y) => InDisc(x, y));
        Assert.True(iou > 0.95, $"IoU {iou:F3}");
    }

    [Fact]
    public void Where_the_image_shows_no_edge_the_prior_outline_holds_the_growth()
    {
        // The ball's right half is exactly the background's color: without a prior the region runs into the
        // background (if brushed there); with it the region ends within the uncertainty of the outline.
        var image = ShadedBall(edge: false);
        var working = QuickSelectionImage.Build(image);
        Vector2[] path = [new(95, 120), new(190, 120)];
        var plain = new QuickSelectionStroke(working, 16, null, SelectionMode.Replace);
        Brush(plain, path);
        var leaked = plain.Finish(autoEnhance: false)!;
        Assert.True(Count(leaked, (x, y) => !InDisc(x, y)) > 2000, "plain growth leaks");

        var steered = new QuickSelectionStroke(working, 16, null, SelectionMode.Replace);
        Brush(steered, path);
        Assert.True(steered.SetPrior(DiscPrior(working, R)));
        var mask = steered.Finish(autoEnhance: false)!;
        Assert.Equal(0, Count(mask, (x, y) => !InDisc(x, y, R + 6 + 1.5f)));
        double iou = Synthetic.IoU(mask, image.Bounds, (x, y) => InDisc(x, y));
        Assert.True(iou > 0.9, $"IoU {iou:F3}");
    }

    [Fact]
    public void A_prior_that_disagrees_with_the_brushing_is_ignored()
    {
        // The prior is somewhere else entirely: the stroke grows exactly as it would without one.
        var image = ShadedBall();
        var working = QuickSelectionImage.Build(image);
        var elsewhere = QuickSelectionPrior.Sample(working, p => 30 - Vector2.Distance(p, new Vector2(260, 40)), 6);
        var (stroke, mask) = Select(image, elsewhere);
        Assert.False(stroke.HasPrior);
        AssertSameCoverage(Select(image).Mask, mask, image.Bounds);
    }

    [Fact]
    public void Clearing_the_prior_returns_to_plain_growth()
    {
        var image = ShadedBall();
        var working = QuickSelectionImage.Build(image);
        var (stroke, _) = Select(image, DiscPrior(working, R));
        Assert.False(stroke.SetPrior(null));
        Assert.False(stroke.HasPrior);
        // Regrowing uses the final color model everywhere, where the drag used each moment's, so the two may differ
        // by a few cells at the ends of the region.
        var plain = Select(image).Mask;
        var cleared = stroke.Finish(autoEnhance: false);
        double same = Synthetic.IoU(cleared, image.Bounds, (x, y) => plain!.CoverageAt(x, y) >= 128);
        Assert.True(same > 0.95, $"IoU with plain growth {same:F3}");
    }

    [Fact]
    public void An_untrusted_prior_only_keeps_growth_inside_its_outline()
    {
        // One flat gray field; the prior (a wall, not an object: trust 0) covers its left part. Growth inside is
        // plain Quick Selection's (a blob around the click, not the whole area), and it never crosses the outline.
        var image = Synthetic.Image(W, H, (x, y) => (150 + Synthetic.Noise(x, y, 0, 3), 150 + Synthetic.Noise(x, y, 1, 3), 150 + Synthetic.Noise(x, y, 2, 3)));
        var working = QuickSelectionImage.Build(image);
        var wall = QuickSelectionPrior.Sample(working, p => 180 - p.X, 4, trust: 0);
        Assert.Equal(0f, wall.Trust);

        var plain = new QuickSelectionStroke(working, 10, null, SelectionMode.Replace);
        plain.AddSegment(new(150, 120), new(150, 120));
        var plainMask = plain.Finish(autoEnhance: false)!;
        Assert.True(plainMask.Bounds.Right > 190, $"plain growth crosses x = 180: {plainMask.Bounds}");

        var stroke = new QuickSelectionStroke(working, 10, null, SelectionMode.Replace);
        stroke.AddSegment(new(150, 120), new(150, 120));
        Assert.True(stroke.SetPrior(wall));
        var mask = stroke.Finish(autoEnhance: false)!;
        Assert.True(mask.Bounds.Right <= 180 + 4 + 1, $"{mask.Bounds}");
        Assert.True(mask.Bounds.Left == plainMask.Bounds.Left && mask.Bounds.Top == plainMask.Bounds.Top, $"inside grows as before: {mask.Bounds} vs {plainMask.Bounds}");
    }

    [Fact]
    public void Subtracting_with_a_prior_removes_the_object()
    {
        var image = ShadedBall();
        var working = QuickSelectionImage.Build(image);
        var stroke = new QuickSelectionStroke(working, 16, SelectionMask.All(image.Bounds), SelectionMode.Subtract);
        Brush(stroke, LitStroke);
        stroke.SetPrior(DiscPrior(working, R));
        var s = stroke.Finish(autoEnhance: false);
        double iou = Synthetic.IoU(s, image.Bounds, (x, y) => !InDisc(x, y));
        Assert.True(iou > 0.97, $"IoU {iou:F3}");
    }

    [Fact]
    public void A_prior_for_another_working_image_is_refused()
    {
        var small = QuickSelectionImage.Build(Synthetic.Image(50, 40, (_, _) => (0, 0, 0)));
        var stroke = new QuickSelectionStroke(QuickSelectionImage.Build(ShadedBall()), 16, null, SelectionMode.Replace);
        Assert.Throws<ArgumentException>(() => stroke.SetPrior(QuickSelectionPrior.Sample(small, _ => 1, 2)));
        Assert.Throws<ArgumentException>(() => QuickSelectionPrior.FromCells(small, new float[10], 2));
    }

    [Fact]
    public void Priors_are_sampled_at_cell_centers_of_the_working_grid()
    {
        // 2400 px wide: three pixels per cell. The prior must sample cell centers in image coordinates.
        var image = Synthetic.Image(2400, 300, (_, _) => (100, 100, 100));
        var working = QuickSelectionImage.Build(image);
        Assert.Equal(3, working.Factor);
        var seen = new System.Collections.Concurrent.ConcurrentBag<Vector2>();
        var prior = QuickSelectionPrior.Sample(working, p => { seen.Add(p); return 1; }, 1);
        Assert.Equal(working.Width * working.Height, seen.Count);
        Assert.Contains(new Vector2(1.5f, 1.5f), seen);
        Assert.Equal(3f, prior.Uncertainty); // never below a cell
    }

    /// <summary>Selected pixels (coverage ≥ 50%) of the test image where <paramref name="where"/> holds.</summary>
    private static int Count(SelectionMask? s, Func<int, int, bool> where)
    {
        int n = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (s is not null && s.CoverageAt(x, y) >= 128 && where(x, y)) n++;
        return n;
    }

    private static void AssertSameCoverage(SelectionMask? a, SelectionMask? b, PixelRect area)
    {
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                Assert.Equal(a?.CoverageAt(x, y) ?? 0, b?.CoverageAt(x, y) ?? 0);
    }
}
