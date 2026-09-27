using Strayta.Core;

namespace Strayta.Segmentation.Tests;

public class PostprocessingTests
{
    /// <summary>
    /// Logits of a disc as a model would give them at low resolution: positive inside, proportional to the distance
    /// from the edge (in mask cells), clamped like SAM's ±32.
    /// </summary>
    private static MaskLogits DiscLogits(int cells, PixelRect placement, float cx, float cy, float r)
    {
        var v = new float[cells * cells];
        double sx = (double)placement.Width / cells, sy = (double)placement.Height / cells;
        for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
            {
                // Cell center in document coordinates.
                double x = placement.Left + (i + 0.5) * sx, y = placement.Top + (j + 0.5) * sy;
                double d = r - Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                v[j * cells + i] = (float)Math.Clamp(d / sx * 3, -32, 32);
            }
        return new MaskLogits(v, cells, cells, placement);
    }

    [Fact]
    public void Upscaled_disc_matches_the_true_disc()
    {
        // 64 cells over 1280 px: 20 px per cell, the scale of SAM on a 5000-pixel photo.
        var placement = PixelRect.FromSize(1280, 1280);
        var logits = DiscLogits(64, placement, 640, 600, 400);
        var mask = MaskUpscaler.ToSelection(logits, placement);
        Assert.NotNull(mask);
        double iou = TestImages.Iou(mask, placement, (x, y) => TestImages.InDisc(x, y, 640, 600, 400));
        Assert.True(iou > 0.99, $"IoU {iou:F4}");
        // Bounds hug the disc (interpolation error well under a cell).
        Assert.InRange(mask!.Bounds.Left, 240 - 4, 240 + 4);
        Assert.InRange(mask.Bounds.Bottom, 1000 - 4, 1000 + 4);
    }

    [Fact]
    public void Edge_is_anti_aliased_over_about_one_pixel()
    {
        var placement = PixelRect.FromSize(1280, 1280);
        var mask = MaskUpscaler.ToSelection(DiscLogits(64, placement, 640, 640, 400), placement)!;
        // Along the horizontal through the center, coverage goes from 0 to 255 within ~2 pixels of x = 240.
        int partial = 0;
        for (int x = 230; x < 250; x++)
        {
            byte c = mask.CoverageAt(x, 640);
            if (c is > 0 and < 255) partial++;
            if (x < 237) Assert.Equal(0, c);
            if (x > 243) Assert.Equal(255, c);
        }
        Assert.InRange(partial, 1, 2);
        Assert.Equal(255, mask.CoverageAt(640, 640));
        Assert.Equal(0, mask.CoverageAt(5, 5));
    }

    [Fact]
    public void Softness_feathers_the_edge()
    {
        var placement = PixelRect.FromSize(1280, 1280);
        var mask = MaskUpscaler.ToSelection(DiscLogits(64, placement, 640, 640, 400), placement, softness: 8)!;
        int partial = 0;
        for (int x = 220; x < 260; x++)
            if (mask.CoverageAt(x, 640) is > 0 and < 255) partial++;
        Assert.InRange(partial, 7, 9);
    }

    [Fact]
    public void Placement_offsets_and_non_square_scaling_are_respected()
    {
        // A 600×300 layer at (1000, 2000) seen by the model as 32×32 cells: the disc must come back where it is.
        var placement = new PixelRect(1000, 2000, 1600, 2300);
        var v = new float[32 * 32];
        for (int j = 0; j < 32; j++)
            for (int i = 0; i < 32; i++)
            {
                double x = 1000 + (i + 0.5) * 600 / 32.0, y = 2000 + (j + 0.5) * 300 / 32.0;
                v[j * 32 + i] = (float)(100 - Math.Sqrt((x - 1300) * (x - 1300) + (y - 2150) * (y - 2150)));
            }
        var canvas = PixelRect.FromSize(4000, 4000);
        var mask = MaskUpscaler.ToSelection(new MaskLogits(v, 32, 32, placement), canvas);
        Assert.NotNull(mask);
        double iou = TestImages.Iou(mask, placement, (x, y) => TestImages.InDisc(x, y, 1300, 2150, 100));
        Assert.True(iou > 0.97, $"IoU {iou:F4}");
    }

    [Fact]
    public void Selection_is_clipped_to_the_canvas()
    {
        // A layer hanging off the canvas's left edge.
        var placement = new PixelRect(-200, 0, 440, 640);
        var mask = MaskUpscaler.ToSelection(DiscLogits(32, placement, 0, 320, 250), PixelRect.FromSize(1000, 1000));
        Assert.NotNull(mask);
        Assert.Equal(0, mask!.Bounds.Left);
        Assert.InRange(mask.Bounds.Right, 245, 255);
    }

    [Fact]
    public void Nothing_positive_gives_no_selection()
    {
        var logits = new MaskLogits(Enumerable.Repeat(-5f, 16 * 16).ToArray(), 16, 16, PixelRect.FromSize(100, 100));
        Assert.Null(MaskUpscaler.ToSelection(logits, PixelRect.FromSize(100, 100)));
        Assert.True(MaskUpscaler.PositiveRegion(logits).IsEmpty);
    }

    [Fact]
    public void Sample_interpolates_between_cell_centers()
    {
        // Two cells over 20 px: centers at x = 5 and 15.
        var logits = new MaskLogits([0f, 10f, 0f, 10f], 2, 2, PixelRect.FromSize(20, 20));
        Assert.Equal(0f, logits.Sample(5, 5), 1e-4f);
        Assert.Equal(5f, logits.Sample(10, 5), 1e-4f);
        Assert.Equal(10f, logits.Sample(15, 15), 1e-4f);
        Assert.Equal(10f, logits.Sample(19.9, 15), 1e-4f); // held constant past the last center
    }

    [Fact]
    public void Cleanup_fills_pinholes_and_drops_specks()
    {
        const int n = 40;
        var v = new float[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                v[y * n + x] = x is >= 5 and < 30 && y is >= 5 and < 30 ? 6f : -6f;
        v[15 * n + 15] = -3f;  // pinhole in the object
        v[2 * n + 36] = 3f;    // speck far away
        MaskCleanup.Clean(v, n, n, minIslandFraction: 0.1f, maxHoleFraction: 0.02f);
        Assert.True(v[15 * n + 15] > 0);
        Assert.True(v[2 * n + 36] < 0);
        Assert.True(v[20 * n + 20] > 0);
        Assert.True(v[35 * n + 5] < 0);
    }

    [Fact]
    public void Cleanup_keeps_large_holes_and_separate_objects()
    {
        const int n = 40;
        var v = new float[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool ring = x is >= 2 and < 20 && y is >= 2 and < 20 && !(x is >= 6 and < 16 && y is >= 6 and < 16);
                bool second = x is >= 24 and < 38 && y is >= 24 and < 38;
                v[y * n + x] = ring || second ? 6f : -6f;
            }
        MaskCleanup.Clean(v, n, n, minIslandFraction: 0.1f, maxHoleFraction: 0.02f);
        Assert.True(v[10 * n + 10] < 0);  // the ring's large hole stays open
        Assert.True(v[30 * n + 30] > 0);  // the second object stays
    }

    [Fact]
    public void Edge_refinement_snaps_a_coarse_edge_to_the_image_edge()
    {
        // The image has a vertical color edge at x = 205; the model (10 px cells) put the edge at x = 200.
        const int w = 400, h = 200;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = x < 205 ? ((byte)220, (byte)120, (byte)40, (byte)255) : ((byte)240, (byte)235, (byte)215, (byte)255);
            }
        var image = new RgbaImage(px, w, h, PixelRect.FromSize(w, h));
        var cells = new float[40 * 20];
        for (int j = 0; j < 20; j++)
            for (int i = 0; i < 40; i++)
                cells[j * 40 + i] = (200 - (i + 0.5f) * 10) / 10 * 4; // zero crossing at x = 200
        var logits = new MaskLogits(cells, 40, 20, image.Placement);

        var coarse = MaskUpscaler.ToSelection(logits, image.Placement)!;
        var refined = MaskUpscaler.ToSelection(logits, image.Placement, guide: image)!;
        Assert.Equal(0, coarse.CoverageAt(202, 100));
        Assert.Equal(255, refined.CoverageAt(202, 100));
        Assert.InRange(refined.CoverageAt(204, 100), 200, 255); // the last object pixel, softened by the 3×3 pass
        Assert.InRange(refined.CoverageAt(205, 100), 0, 55);
        Assert.Equal(0, refined.CoverageAt(206, 100));
        Assert.Equal(255, refined.CoverageAt(100, 100));
        Assert.Equal(0, refined.CoverageAt(300, 100));
    }

    [Fact]
    public void Edge_refinement_keeps_the_model_edge_where_colors_do_not_differ()
    {
        const int w = 200, h = 100;
        var px = Enumerable.Repeat((byte)128, w * h * 4).ToArray();
        var image = new RgbaImage(px, w, h, PixelRect.FromSize(w, h));
        var cells = new float[20 * 10];
        for (int j = 0; j < 10; j++)
            for (int i = 0; i < 20; i++)
                cells[j * 20 + i] = (100 - (i + 0.5f) * 10) / 10 * 4;
        var logits = new MaskLogits(cells, 20, 10, image.Placement);
        var coarse = MaskUpscaler.ToSelection(logits, image.Placement)!;
        var refined = MaskUpscaler.ToSelection(logits, image.Placement, guide: image)!;
        for (int x = 90; x < 110; x++) Assert.Equal(coarse.CoverageAt(x, 50), refined.CoverageAt(x, 50));
    }
}
