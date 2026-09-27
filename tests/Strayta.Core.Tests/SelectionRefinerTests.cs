using System.Diagnostics;
using System.Numerics;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

/// <summary>Select and Mask: edge detection from colors, the Refine Edge brush, and the global refinements.</summary>
public class SelectionRefinerTests(ITestOutputHelper output)
{
    private static readonly (int R, int G, int B) Fg = (230, 200, 120), Bg = (20, 40, 200);

    /// <summary>
    /// Foreground on the left, background on the right, mixed over columns 44..55 with a known alpha (a soft edge
    /// such as fur or motion blur), plus a thin semi-transparent strand at columns 95..98.
    /// </summary>
    private static float TrueAlpha(int x) => x < 44 ? 1f : x < 56 ? (56 - x) / 12f : x is >= 95 and < 99 ? 0.5f : 0f;

    private static SampleImage Image() => Synthetic.Image(W, 60, (x, _) =>
    {
        float a = TrueAlpha(x);
        return ((int)MathF.Round(Fg.R * a + Bg.R * (1 - a)), (int)MathF.Round(Fg.G * a + Bg.G * (1 - a)), (int)MathF.Round(Fg.B * a + Bg.B * (1 - a)));
    });

    private const int W = 120;
    private static readonly PixelRect Canvas = PixelRect.FromSize(W, 60);

    /// <summary>A hard selection of the left half, as a lasso or marquee would give.</summary>
    private static SelectionMask Hard() => SelectionMask.Rectangle(new PixelRect(0, 0, 50, 60), Canvas)!;

    [Fact]
    public void Without_settings_the_selection_is_unchanged()
    {
        var refiner = new SelectionRefiner(Image(), Hard());
        var matte = refiner.Refine(new RefineSettings(), null);
        for (int x = 0; x < W; x++) Assert.Equal(x < 50 ? 255 : 0, matte[30 * W + x]);
    }

    [Fact]
    public void Edge_detection_recovers_the_alpha_of_a_soft_edge()
    {
        var refiner = new SelectionRefiner(Image(), Hard());
        var matte = refiner.Refine(new RefineSettings(Radius: 10), null);
        for (int x = 40; x < 60; x++)
            Assert.InRange(matte[30 * W + x], TrueAlpha(x) * 255 - 8, TrueAlpha(x) * 255 + 8);
        // Beyond the radius nothing changes: the strand is not detected without the brush.
        Assert.Equal(0, matte[30 * W + 96]);
        Assert.Equal(255, matte[30 * W + 20]);
    }

    [Fact]
    public void The_refine_edge_brush_adds_detail_outside_the_radius()
    {
        var refiner = new SelectionRefiner(Image(), Hard());
        var brush = new RefineBrushMask(W, 60);
        brush.Paint(new Vector2(96.5f, 5), new Vector2(96.5f, 55), 14, erase: false);
        var matte = refiner.Refine(new RefineSettings(Radius: 10), brush.Snapshot());
        Assert.InRange(matte[30 * W + 96], 115, 140); // the half-transparent strand, 55 pixels from known foreground
        Assert.InRange(matte[30 * W + 90], 0, 8);     // background between it and the edge

        // Option-painting erases the edge detection there, restoring the original coverage.
        brush.Paint(new Vector2(50, 0), new Vector2(50, 59), 30, erase: true);
        var erased = refiner.Refine(new RefineSettings(Radius: 10), brush.Snapshot());
        Assert.Equal(255, erased[30 * W + 49]);
        Assert.Equal(0, erased[30 * W + 50]);
    }

    [Fact]
    public void Similar_colors_keep_the_original_coverage()
    {
        var flat = Synthetic.Image(W, 60, (_, _) => (128, 128, 128));
        var matte = new SelectionRefiner(flat, Hard()).Refine(new RefineSettings(Radius: 10), null);
        for (int x = 0; x < W; x++) Assert.Equal(x < 50 ? 255 : 0, matte[30 * W + x]);
    }

    [Fact]
    public void Global_refinements_feather_harden_and_shift_the_edge()
    {
        var refiner = new SelectionRefiner(Image(), Hard());
        int At(byte[] m, int x) => m[30 * W + x];

        var feathered = refiner.Refine(new RefineSettings(Feather: 3), null);
        Assert.InRange(At(feathered, 49) + At(feathered, 50), 250, 260);
        Assert.True(At(feathered, 47) is > 128 and < 255 && At(feathered, 52) is > 0 and < 128);

        // Contrast 100% turns the feathered ramp back into a nearly hard edge at the same place.
        var hardened = refiner.Refine(new RefineSettings(Feather: 3, Contrast: 100), null);
        Assert.Equal(255, At(hardened, 48));
        Assert.Equal(0, At(hardened, 51));

        // Shift Edge: +50% moves the edge out by 5 pixels, −30% in by 3.
        var outward = refiner.Refine(new RefineSettings(ShiftEdge: 50), null);
        Assert.Equal(255, At(outward, 54));
        Assert.Equal(0, At(outward, 55));
        var inward = refiner.Refine(new RefineSettings(ShiftEdge: -30), null);
        Assert.Equal(255, At(inward, 46));
        Assert.Equal(0, At(inward, 47));
        // Fractions of a pixel blend.
        Assert.InRange(At(refiner.Refine(new RefineSettings(ShiftEdge: 5), null), 50), 120, 135);

        // Smooth removes a notch like Select › Modify › Smooth.
        var notched = Synthetic.Image(W, 60, (_, _) => (0, 0, 0));
        var withNotch = SelectionMask.Combine(Hard(), SelectionMask.Rectangle(new PixelRect(48, 30, 50, 31), Canvas), SelectionMode.Subtract);
        var smoothed = new SelectionRefiner(notched, withNotch).Refine(new RefineSettings(Smooth: 30), null);
        Assert.True(At(smoothed, 49) >= 128);
    }

    [Fact]
    public void Large_images_refine_interactively()
    {
        const int W = 4000, H = 3000;
        // A disc on a noisy two-color background: the edge band has real work to do.
        var rgba = new byte[W * H * 4];
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                bool inside = (x - 2000) * (x - 2000) + (y - 1500) * (y - 1500) < 1200 * 1200;
                int i = (y * W + x) * 4, n = Synthetic.Noise(x, y, 0, 10);
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = inside ? ((byte)(200 + n), (byte)(150 + n), (byte)90, (byte)255) : ((byte)(30 + n), (byte)60, (byte)(160 + n), (byte)255);
            }
        });
        var image = new SampleImage(W, H, rgba);
        var selection = SelectionMask.Ellipse(new PixelRect(810, 310, 3190, 2690), image.Bounds);
        var refiner = new SelectionRefiner(image, selection);

        var sw = Stopwatch.StartNew();
        refiner.Refine(new RefineSettings(Radius: 10), null);
        output.WriteLine($"Radius 10 (edge detection): {sw.ElapsedMilliseconds} ms");
        foreach (var s in new[] { new RefineSettings(Radius: 10, Feather: 5), new RefineSettings(Radius: 10, Feather: 5, Contrast: 30),
                     new RefineSettings(Radius: 10, Smooth: 20), new RefineSettings(Radius: 10, ShiftEdge: 25), new RefineSettings(Radius: 20) })
        {
            sw.Restart();
            var matte = refiner.Refine(s, null);
            output.WriteLine($"{s}: {sw.ElapsedMilliseconds} ms");
            Assert.True(sw.ElapsedMilliseconds < 3000, $"{s} took {sw.ElapsedMilliseconds} ms");
            Assert.Equal(255, matte[1500 * W + 2000]);
        }
    }
}
