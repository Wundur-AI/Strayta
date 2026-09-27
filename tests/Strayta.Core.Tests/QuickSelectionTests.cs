using System.Numerics;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

public class QuickSelectionTests
{
    /// <summary>A reddish disc on a bluish background, both with strong per-pixel noise.</summary>
    private static SampleImage NoisyDisc(int w, int h, float cx, float cy, float r, int noise = 25) =>
        Synthetic.Image(w, h, (x, y) =>
        {
            bool inside = (x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy) < r * r;
            var (red, green, blue) = inside ? (200, 70, 50) : (70, 100, 170);
            return (red + Synthetic.Noise(x, y, 0, noise), green + Synthetic.Noise(x, y, 1, noise), blue + Synthetic.Noise(x, y, 2, noise));
        });

    private static void Brush(QuickSelectionStroke stroke, params Vector2[] path)
    {
        // Pointer events arrive every few pixels.
        stroke.AddSegment(path[0], path[0]);
        for (int i = 1; i < path.Length; i++)
        {
            var a = path[i - 1];
            var b = path[i];
            int steps = Math.Max(1, (int)(Vector2.Distance(a, b) / 4));
            for (int s = 1; s <= steps; s++) stroke.AddSegment(Vector2.Lerp(a, b, (s - 1f) / steps), Vector2.Lerp(a, b, (float)s / steps));
        }
    }

    [Fact]
    public void Brushing_inside_a_disc_on_a_noisy_background_selects_the_disc()
    {
        var image = NoisyDisc(300, 240, 150, 120, 70);
        var stroke = new QuickSelectionStroke(QuickSelectionImage.Build(image), 20, null, SelectionMode.Replace);
        Brush(stroke, new(110, 120), new(190, 120), new(150, 80), new(150, 160));
        var s = stroke.Finish(autoEnhance: false);
        double iou = Synthetic.IoU(s, image.Bounds, (x, y) => (x + 0.5 - 150) * (x + 0.5 - 150) + (y + 0.5 - 120) * (y + 0.5 - 120) < 70 * 70);
        Assert.True(iou > 0.95, $"IoU {iou:F3}");
    }

    [Fact]
    public void Large_images_work_on_a_reduced_grid_and_refine_the_edge_at_full_resolution()
    {
        // 2400 × 1800: the working grid is a third of that, the result is still accurate to about a pixel.
        var image = NoisyDisc(2400, 1800, 1200, 900, 500, noise: 15);
        var working = QuickSelectionImage.Build(image);
        Assert.Equal(3, working.Factor);
        var stroke = new QuickSelectionStroke(working, 60, null, SelectionMode.Replace);
        Brush(stroke, new(900, 900), new(1500, 900), new(1200, 600), new(1200, 1200));
        var s = stroke.Finish(autoEnhance: false)!;
        double iou = Synthetic.IoU(s, image.Bounds, (x, y) => (x + 0.5 - 1200) * (x + 0.5 - 1200) + (y + 0.5 - 900) * (y + 0.5 - 900) < 500.0 * 500);
        Assert.True(iou > 0.98, $"IoU {iou:F4}");
        // The boundary is found within the working cells: no 3-pixel stair steps along the edge.
        int wrong = 0;
        for (int y = 400; y < 1400; y++)
        {
            double half = Math.Sqrt(Math.Max(0, 500.0 * 500 - (y + 0.5 - 900) * (y + 0.5 - 900)));
            int left = (int)Math.Round(1200 - half);
            for (int x = left - 3; x <= left + 3; x++)
                if ((s.CoverageAt(x, y) >= 128) != (x + 0.5 > 1200 - half) && Math.Abs(x + 0.5 - (1200 - half)) > 1) wrong++;
        }
        Assert.True(wrong < 50, $"{wrong} pixels more than a pixel off the edge");
    }

    [Fact]
    public void A_click_in_a_flat_area_stops_at_its_edge()
    {
        // Left half flat gray, right half flat dark: the region may not cross into the dark half.
        var image = Synthetic.Image(200, 100, (x, _) => x < 100 ? (180, 180, 180) : (60, 60, 60));
        var stroke = new QuickSelectionStroke(QuickSelectionImage.Build(image), 10, null, SelectionMode.Replace);
        stroke.AddSegment(new(50, 50), new(50, 50));
        var s = stroke.Finish(autoEnhance: false)!;
        Assert.True(s.Bounds.Right <= 100, $"{s.Bounds}");
        Assert.True(s.Bounds.Width >= 40, $"grows well beyond the brush: {s.Bounds}");
    }

    [Fact]
    public void Subtracting_removes_the_brushed_object_from_the_selection()
    {
        var image = NoisyDisc(300, 240, 150, 120, 70);
        var all = SelectionMask.All(image.Bounds);
        var stroke = new QuickSelectionStroke(QuickSelectionImage.Build(image), 20, all, SelectionMode.Subtract);
        Brush(stroke, new(110, 120), new(190, 120), new(150, 80), new(150, 160));
        var s = stroke.Finish(autoEnhance: false);
        double iou = Synthetic.IoU(s, image.Bounds, (x, y) => (x + 0.5 - 150) * (x + 0.5 - 150) + (y + 0.5 - 120) * (y + 0.5 - 120) >= 70 * 70);
        Assert.True(iou > 0.95, $"IoU {iou:F3}");
        Assert.Equal(255, s!.CoverageAt(5, 5));
    }

    [Fact]
    public void Adding_keeps_the_previous_selection_and_the_preview_shows_both()
    {
        var image = NoisyDisc(300, 240, 200, 120, 60);
        var before = SelectionMask.Rectangle(new PixelRect(0, 0, 40, 40), image.Bounds);
        var stroke = new QuickSelectionStroke(QuickSelectionImage.Build(image), 20, before, SelectionMode.Add);
        Assert.True(stroke.IsEmpty);
        Brush(stroke, new(170, 120), new(230, 120));
        var loops = stroke.PreviewOutline();
        Assert.Equal(2, loops.Count);
        var s = stroke.Finish(autoEnhance: false)!;
        Assert.Equal(255, s.CoverageAt(10, 10));
        Assert.Equal(255, s.CoverageAt(200, 120));
        Assert.Equal(0, s.CoverageAt(100, 200));
    }

    [Fact]
    public void Auto_enhance_gives_a_soft_edge()
    {
        var image = NoisyDisc(300, 240, 150, 120, 70, noise: 5);
        var stroke = new QuickSelectionStroke(QuickSelectionImage.Build(image), 20, null, SelectionMode.Replace);
        Brush(stroke, new(110, 120), new(190, 120), new(150, 80), new(150, 160));
        var hard = stroke.Finish(autoEnhance: false)!;
        var soft = stroke.Finish(autoEnhance: true)!;
        int partialHard = 0, partialSoft = 0;
        for (int y = 0; y < 240; y++)
            for (int x = 0; x < 300; x++)
            {
                if (hard.CoverageAt(x, y) is > 0 and < 255) partialHard++;
                if (soft.CoverageAt(x, y) is > 0 and < 255) partialSoft++;
            }
        Assert.Equal(0, partialHard);
        Assert.True(partialSoft > 200, $"{partialSoft} soft edge pixels");
        double iou = Synthetic.IoU(soft, image.Bounds, (x, y) => (x + 0.5 - 150) * (x + 0.5 - 150) + (y + 0.5 - 120) * (y + 0.5 - 120) < 70 * 70);
        Assert.True(iou > 0.95, $"IoU {iou:F3}");
    }

    [Fact]
    public void Brushing_outside_the_image_selects_nothing()
    {
        var image = NoisyDisc(100, 80, 50, 40, 20);
        var stroke = new QuickSelectionStroke(QuickSelectionImage.Build(image), 10, null, SelectionMode.Replace);
        Assert.False(stroke.AddSegment(new(-50, -50), new(-40, -60)));
        Assert.Null(stroke.Finish(autoEnhance: false));
    }
}
