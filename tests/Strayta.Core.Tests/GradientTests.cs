using System.Numerics;
using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

public class GradientTests
{
    private static readonly RgbColor Black = new(0, 0, 0), White = new(1, 1, 1), Red = new(1, 0, 0);

    private static GradientSpec Spec(GradientType type, Vector2 start, Vector2 end, bool reverse = false) =>
        new(type, start, end, Black, 1f, White, 1f) { Reverse = reverse };

    [Fact]
    public void Linear_runs_along_the_drag_and_holds_the_end_colors_beyond_it()
    {
        var g = Spec(GradientType.Linear, new(10, 0), new(30, 0));
        Assert.Equal(0f, g.PositionAt(0, 5));
        Assert.Equal(0f, g.PositionAt(10, 99));
        Assert.Equal(0.5f, g.PositionAt(20, -40), 4);
        Assert.Equal(1f, g.PositionAt(30, 3));
        Assert.Equal(1f, g.PositionAt(100, 3));
        Assert.Equal(0.75f, Spec(GradientType.Linear, new(10, 0), new(30, 0), reverse: true).PositionAt(15, 0), 4);
    }

    [Fact]
    public void Radial_is_the_distance_from_the_start_over_the_drag_length()
    {
        var g = Spec(GradientType.Radial, new(0, 0), new(10, 0));
        Assert.Equal(0f, g.PositionAt(0, 0));
        Assert.Equal(0.5f, g.PositionAt(0, 5), 4);
        Assert.Equal(0.5f, g.PositionAt(-3, 4), 4);
        Assert.Equal(1f, g.PositionAt(0, -20));
    }

    [Fact]
    public void Reflected_mirrors_the_linear_gradient_at_the_start()
    {
        var g = Spec(GradientType.Reflected, new(50, 0), new(60, 0));
        Assert.Equal(g.PositionAt(55, 7), g.PositionAt(45, 7), 5);
        Assert.Equal(0.5f, g.PositionAt(45, 0), 4);
        Assert.Equal(1f, g.PositionAt(20, 0));
    }

    [Fact]
    public void Diamond_has_a_corner_at_the_end_point()
    {
        var g = Spec(GradientType.Diamond, new(0, 0), new(10, 0));
        Assert.Equal(1f, g.PositionAt(10, 0), 4);
        Assert.Equal(1f, g.PositionAt(0, 10), 4);
        Assert.Equal(1f, g.PositionAt(5, 5), 4);  // on the edge between two corners
        Assert.Equal(0.5f, g.PositionAt(-5, 0), 4);
        Assert.True(g.PositionAt(4, 4) < 1f && g.PositionAt(6, 6) >= 1f);
    }

    [Fact]
    public void Angle_sweeps_once_around_the_start_counterclockwise_on_screen()
    {
        var g = Spec(GradientType.Angle, new(0, 0), new(10, 0));
        Assert.Equal(0f, g.PositionAt(5, 0), 4);          // on the drag line
        Assert.Equal(0.25f, g.PositionAt(0, -5), 4);      // straight up on screen (y down)
        Assert.Equal(0.5f, g.PositionAt(-5, 0), 4);
        Assert.Equal(0.75f, g.PositionAt(0, 5), 4);
        Assert.True(g.PositionAt(5, 0.01f) > 0.99f);      // just below the line: the end of the sweep
    }

    [Fact]
    public void Colors_and_opacity_interpolate_between_the_stops()
    {
        var g = new GradientSpec(GradientType.Linear, new(0, 0), new(10, 0), Red, 1f, Red, 0f) { Opacity = 0.5f };
        var (r, gr, b, a) = g.ColorAt(0.5f);
        Assert.Equal((1f, 0f, 0f), (r, gr, b));
        Assert.Equal(0.25f, a, 5);
    }

    [Fact]
    public void Dither_is_deterministic_and_within_half_a_level()
    {
        var values = new List<float>();
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                float d = GradientSpec.DitherAt(x, y);
                Assert.Equal(d, GradientSpec.DitherAt(x, y));
                Assert.InRange(d, -0.49f, 0.49f);
                values.Add(d);
            }
        Assert.InRange(values.Average(), -0.05, 0.05);
        Assert.True(values.Distinct().Count() > 4000, "neighbors should not repeat");
    }

    [Fact]
    public void Painting_a_gradient_fills_the_canvas_and_dithered_output_repeats_exactly()
    {
        var canvas = PixelRect.FromSize(256, 4);
        var spec = Spec(GradientType.Linear, new(0, 0), new(256, 0)) with { Dither = true };
        var (a, boundsA) = GradientPainter.Apply(null, PixelRect.Empty, spec, null, canvas, ColorMode.Rgb, 8);
        var (b, _) = GradientPainter.Apply(null, PixelRect.Empty, spec, null, canvas, ColorMode.Rgb, 8);
        Assert.Equal(canvas, boundsA);
        Assert.Equal(a!.ColorPlanes[0].Data, b!.ColorPlanes[0].Data);
        Assert.All(a.Alpha!.Data, v => Assert.Equal(255, v));

        var (plain, _) = GradientPainter.Apply(null, PixelRect.Empty, spec with { Dither = false }, null, canvas, ColorMode.Rgb, 8);
        var d = a.ColorPlanes[0].Data;
        var p = plain!.ColorPlanes[0].Data;
        Assert.All(Enumerable.Range(0, d.Length), i => Assert.InRange(d[i] - p[i], -1, 1));
        Assert.Contains(Enumerable.Range(0, d.Length), i => d[i] != p[i]);
        // Without dither the ramp is exact: pixel x has position (x + 0.5) / 256.
        Assert.Equal((byte)MathF.Round(100.5f / 256 * 255), p[100]);
    }

    [Fact]
    public void A_transparent_stop_lets_the_layer_show_and_a_Background_stays_opaque()
    {
        var canvas = PixelRect.FromSize(10, 1);
        var background = Synthetic.Layer(canvas, (_, _) => (0, 0, 255, 255));
        var opaque = new Raster(ColorMode.Rgb, background.Pixels!.ColorPlanes, alpha: null);
        var spec = new GradientSpec(GradientType.Linear, new(0, 0), new(10, 0), Red, 1f, Red, 0f);
        var (px, bounds) = GradientPainter.Apply(opaque, canvas, spec, null, canvas, ColorMode.Rgb, 8);
        Assert.Equal(canvas, bounds);
        Assert.Null(px!.Alpha);
        Assert.True(px.ColorPlanes[0].Data[0] > 240 && px.ColorPlanes[2].Data[0] < 15, "start is red");
        Assert.True(px.ColorPlanes[0].Data[9] < 15 && px.ColorPlanes[2].Data[9] > 240, "end shows the blue layer");
    }

    [Fact]
    public void A_selection_limits_the_gradient_and_a_preview_samples_it_at_scale()
    {
        var canvas = PixelRect.FromSize(40, 40);
        var selection = SelectionMask.Rectangle(new PixelRect(10, 10, 20, 20), canvas);
        var spec = Spec(GradientType.Linear, new(0, 0), new(40, 0));
        var (px, bounds) = GradientPainter.Apply(null, PixelRect.Empty, spec, selection, canvas, ColorMode.Rgb, 8);
        Assert.Equal(new PixelRect(10, 10, 20, 20), bounds);
        Assert.All(px!.Alpha!.Data, v => Assert.Equal(255, v));

        // A 1/4 preview: the canvas is 10×10; a selection of 10..17 reaches preview pixels 2..4, and each preview pixel
        // takes the selection at its center (10, 14, 18), so column 4 stays empty.
        var narrow = SelectionMask.Rectangle(new PixelRect(10, 10, 17, 17), canvas);
        var (preview, previewBounds) = GradientPainter.Apply(null, PixelRect.Empty, spec.Scaled(0.25f), narrow, PixelRect.FromSize(10, 10),
            ColorMode.Rgb, 8, selectionFactor: 4);
        Assert.Equal(new PixelRect(2, 2, 5, 5), previewBounds);
        Assert.Equal(255, preview!.Alpha!.Data[0]);
        Assert.Equal(255, preview.Alpha.Data[1]);
        Assert.Equal(0, preview.Alpha.Data[2]);
    }

    [Fact]
    public void Mask_gradients_paint_gray_and_grow_the_mask()
    {
        var canvas = PixelRect.FromSize(20, 2);
        var mask = LayerMasks.Solid(reveal: true);
        var result = GradientPainter.ApplyToMask(mask, Spec(GradientType.Linear, new(0, 0), new(20, 0)), null, canvas, 8);
        Assert.Equal(canvas, result.Bounds);
        Assert.True(result.Pixels!.Data[0] < 10 && result.Pixels.Data[19] > 245);
        Assert.Equal(255, result.DefaultColor);
    }
}
