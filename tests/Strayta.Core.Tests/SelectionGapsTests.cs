using System.Numerics;
using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

/// <summary>
/// Magic Wand Sample Size and layer masks, the distance transform behind Smart Radius, Smart Radius itself, and
/// Decontaminate Colors.
/// </summary>
public class SelectionGapsTests
{
    // ---- Magic Wand --------------------------------------------------------------------------------------

    [Fact]
    public void Sample_size_averages_the_square_around_the_click()
    {
        // A checkerboard of 0 and 200: a point sample matches one color only, a 3×3 average is about 100 or 111.
        var image = Synthetic.Image(20, 20, (x, y) => (x + y) % 2 == 0 ? (200, 200, 200) : (0, 0, 0));
        uint point = MagicWand.SeedColor(image, 10, 10, 1);
        uint average = MagicWand.SeedColor(image, 10, 10, 3);
        Assert.Equal(200u, point & 0xFF);
        Assert.InRange((int)(average & 0xFF), 105, 118); // 5 of 9 are 200
        Assert.Equal(255u, average >> 24);

        // Tolerance 60 from the average takes in neither color; from the point, every 200 pixel non-contiguously.
        var fromPoint = MagicWand.Select(image, 10, 10, new MagicWandOptions(60, AntiAlias: false, Contiguous: false));
        var fromAverage = MagicWand.Select(image, 10, 10, new MagicWandOptions(60, AntiAlias: false, Contiguous: false, SampleSize: 3));
        Assert.Equal(200, Synthetic.Count(fromPoint, image.Bounds));
        Assert.Null(fromAverage);
    }

    [Fact]
    public void Sample_size_is_clipped_at_the_image_edge()
    {
        var image = Synthetic.Image(10, 10, (x, _) => x < 5 ? (100, 0, 0) : (0, 0, 0));
        uint corner = MagicWand.SeedColor(image, 0, 0, 101);
        Assert.Equal(50u, corner & 0xFF); // the whole image: half 100, half 0
    }

    [Fact]
    public void Current_layer_sampling_honors_the_layer_mask()
    {
        var layer = Synthetic.Layer(new PixelRect(0, 0, 20, 10), (_, _) => (200, 50, 50, 255));
        var maskPixels = new Plane(10, 10, 8, Enumerable.Repeat((byte)0, 100).ToArray());
        var mask = new LayerMask { Bounds = new PixelRect(10, 0, 20, 10), Pixels = maskPixels, DefaultColor = 255 };
        var plain = SampleImage.FromPixels(layer.Pixels, layer.Bounds, 20, 10);
        var masked = SampleImage.FromPixels(layer.Pixels, layer.Bounds, 20, 10, mask: mask);
        Assert.Equal(255, plain.Rgba[(5 * 20 + 15) * 4 + 3]);
        Assert.Equal(0, masked.Rgba[(5 * 20 + 15) * 4 + 3]);   // hidden by the mask: transparent
        Assert.Equal(255, masked.Rgba[(5 * 20 + 5) * 4 + 3]);  // outside the mask's pixels its default (white) shows
        // The wand clicked on the visible part stops where the mask hides the layer.
        var wand = MagicWand.Select(masked, 2, 5, new MagicWandOptions(10, AntiAlias: false));
        Assert.Equal(100, Synthetic.Count(wand, masked.Bounds));
        // A disabled mask is ignored.
        var disabled = SampleImage.FromPixels(layer.Pixels, layer.Bounds, 20, 10, mask: mask.WithDisabled(true));
        Assert.Equal(255, disabled.Rgba[(5 * 20 + 15) * 4 + 3]);
    }

    [Fact]
    public void A_layer_mask_converts_back_to_the_selection_it_came_from()
    {
        var canvas = PixelRect.FromSize(60, 40);
        var selection = SelectionMask.Ellipse(new PixelRect(10, 5, 50, 35), canvas)!;
        foreach (int depth in new[] { 8, 16 })
        {
            var back = SelectionLayerMask.ToSelection(SelectionLayerMask.Create(selection, reveal: true, depth), canvas);
            for (int y = 0; y < 40; y++)
                for (int x = 0; x < 60; x++)
                    Assert.Equal(selection.CoverageAt(x, y), back!.CoverageAt(x, y));
        }
        // A hiding mask's selection is the inverse; a mask that hides everything is no selection.
        var hidden = SelectionLayerMask.ToSelection(SelectionLayerMask.Create(selection, reveal: false, 8), canvas);
        Assert.Equal(255, hidden!.CoverageAt(1, 1));
        Assert.Equal(0, hidden.CoverageAt(30, 20));
        Assert.Null(SelectionLayerMask.ToSelection(LayerMasks.Solid(false), canvas));
    }

    // ---- Distance transform ---------------------------------------------------------------------------------

    [Fact]
    public void Distance_transform_matches_brute_force()
    {
        int w = 37, h = 23;
        var sites = new byte[w * h];
        var rng = new Random(5);
        for (int i = 0; i < 12; i++) sites[rng.Next(w * h)] = 1;
        var (nearest, dist) = DistanceTransform.Compute(sites, w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float best = float.PositiveInfinity;
                for (int s = 0; s < sites.Length; s++)
                    if (sites[s] != 0)
                    {
                        float dx = s % w - x, dy = s / w - y;
                        best = MathF.Min(best, dx * dx + dy * dy);
                    }
                int i = y * w + x;
                Assert.Equal(best, dist[i]);
                int n = nearest[i];
                float ndx = n % w - x, ndy = n / w - y;
                Assert.True(sites[n] != 0 && ndx * ndx + ndy * ndy == best);
            }
    }

    [Fact]
    public void Distance_transform_without_sites_reports_none()
    {
        var (nearest, dist) = DistanceTransform.Compute(new byte[12], 4, 3);
        Assert.All(nearest, n => Assert.Equal(-1, n));
        Assert.All(dist, d => Assert.True(float.IsPositiveInfinity(d)));
    }

    // ---- Smart Radius -------------------------------------------------------------------------------------

    [Fact]
    public void Smart_radius_narrows_the_band_at_hard_edges_and_keeps_it_at_soft_ones()
    {
        // Top half: a hard step at x = 40. Bottom half: a 16-pixel ramp centered at x = 40 (a soft edge).
        const int w = 100, h = 60;
        var image = Synthetic.Image(w, h, (x, y) =>
        {
            float a = y < 30 ? (x < 40 ? 1f : 0f) : Math.Clamp((48 - x) / 16f, 0f, 1f);
            int v = (int)MathF.Round(20 + 200 * a);
            return (v, v, v);
        });
        // The selection's edge sits 6 pixels off the image's edge: a rough lasso.
        var selection = SelectionMask.Rectangle(new PixelRect(0, 0, 46, h), PixelRect.FromSize(w, h));
        var refiner = new SelectionRefiner(image, selection);
        var plain = refiner.Refine(new RefineSettings(Radius: 12), null);
        var smart = refiner.Refine(new RefineSettings(Radius: 12, SmartRadius: true), null);
        // Plain radius 12 reaches the hard edge 6 pixels away and snaps to it.
        Assert.InRange(plain[15 * w + 41], 0, 20);
        // Smart radius at a hard edge shrinks to a quarter (3 px): pixels 4 away keep their coverage.
        Assert.Equal(255, smart[15 * w + 41]);
        // At the soft edge the full radius stays: the ramp's alpha is recovered.
        Assert.InRange(smart[45 * w + 44], 40, 110); // the ramp's true alpha there is 0.25
        Assert.InRange(smart[45 * w + 44], plain[45 * w + 44] - 8, plain[45 * w + 44] + 8);
    }

    // ---- Decontaminate Colors ---------------------------------------------------------------------------------

    [Fact]
    public void Decontamination_replaces_fringe_colors_with_nearby_foreground()
    {
        const int w = 64, h = 16;
        // Foreground orange on the left; fringe columns 30..33 mix in blue background; background blue on the right.
        var rgba = new byte[w * h * 4];
        var coverage = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float a = x < 30 ? 1f : x < 34 ? (34 - x) / 5f : 0f;
                int i = y * w + x, p = i * 4;
                rgba[p] = (byte)(240 * a + 20 * (1 - a));
                rgba[p + 1] = (byte)(140 * a + 60 * (1 - a));
                rgba[p + 2] = (byte)(20 * a + 220 * (1 - a));
                rgba[p + 3] = 255;
                coverage[i] = (byte)MathF.Round(a * 255);
            }
        var full = ColorDecontamination.Apply(rgba, w, h, coverage, 1f);
        var half = ColorDecontamination.Apply(rgba, w, h, coverage, 0.5f);
        int fringe = (8 * w + 32) * 4; // coverage 0.4: fully replaced at amount 100%
        Assert.InRange((int)full[fringe], 235, 245);
        Assert.InRange((int)full[fringe + 2], 15, 25);
        Assert.InRange((int)half[fringe + 2], (rgba[fringe + 2] + 20) / 2 - 6, (rgba[fringe + 2] + 20) / 2 + 6);
        int solid = (8 * w + 10) * 4;
        Assert.Equal(rgba[solid + 2], full[solid + 2]); // fully selected pixels keep their color
        Assert.Equal(rgba[fringe + 3], full[fringe + 3]); // alpha untouched
    }

    // ---- History Brush ----------------------------------------------------------------------------------------

    [Fact]
    public void History_brush_restores_earlier_pixels_including_transparency()
    {
        var bounds = new PixelRect(0, 0, 40, 20);
        var before = Synthetic.Layer(bounds, (x, _) => x < 20 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));
        var now = Synthetic.Layer(bounds, (_, _) => ((byte)0, (byte)0, (byte)255, (byte)255)); // painted blue everywhere since
        var stroke = new PaintStroke(now, new BrushSettings(12, 1f, 1f), default, erase: false, bounds);
        stroke.StrokeTo(4, 10);
        stroke.StrokeTo(36, 10);
        var (pixels, result) = HistoryBrushBaker.Bake(now, stroke, before.Pixels, before.Bounds, ColorMode.Rgb, 8);
        Assert.Equal(bounds, result);
        int red = 10 * 40 + 10, clear = 10 * 40 + 30, untouched = 1 * 40 + 30;
        Assert.Equal(255, pixels!.ColorPlanes[0].Data[red]);
        Assert.Equal(0, pixels.ColorPlanes[2].Data[red]);
        Assert.Equal(0, pixels.Alpha!.Data[clear]);          // the transparent past comes back
        Assert.Equal(255, pixels.ColorPlanes[2].Data[untouched]); // outside the stroke nothing changes
        Assert.Equal(255, pixels.Alpha.Data[untouched]);

        // Half opacity mixes the two states.
        var soft = new PaintStroke(now, new BrushSettings(12, 1f, 0.5f), default, erase: false, bounds);
        soft.StrokeTo(10, 10);
        var (mixed, _) = HistoryBrushBaker.Bake(now, soft, before.Pixels, before.Bounds, ColorMode.Rgb, 8);
        Assert.InRange((int)mixed!.ColorPlanes[0].Data[red], 125, 130);
        Assert.InRange((int)mixed.ColorPlanes[2].Data[red], 125, 130);
    }
}
