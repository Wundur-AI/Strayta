using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Rendering.Tests;

public class SelectionRenderingTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 4)]
    public void Live_stroke_clipped_by_a_selection_matches_the_baked_result(bool erase, int factor)
    {
        var doc = new Document(256, 256, ColorMode.Rgb, 8);
        Plane P(byte v) => new(128, 128, 8, Enumerable.Repeat(v, 128 * 128).ToArray());
        var layer = new PixelLayer { Bounds = new PixelRect(64, 64, 192, 192), Pixels = new Raster(ColorMode.Rgb, [P(0), P(200), P(0)], P(255)) };
        doc.Root.Add(layer);

        // A soft-edged selection with a hole, so both hard and partial clipping are exercised.
        var clip = SelectionMask.Combine(
            SelectionMask.Ellipse(new PixelRect(20, 60, 200, 200), doc.Bounds),
            SelectionMask.Rectangle(new PixelRect(90, 100, 120, 160), doc.Bounds), SelectionMode.Subtract);
        var stroke = new PaintStroke(layer, new BrushSettings(40, 0.5f, 0.8f), new RgbColor(1, 0, 0), erase, doc.Bounds, clip);
        stroke.StrokeTo(16, 120);
        stroke.StrokeTo(160, 136);
        stroke.StrokeTo(240, 30);

        byte[] live;
        if (factor == 1)
        {
            live = Compositor.Render(doc, new RenderOptions { ActiveStroke = new StrokeOverlay(stroke, layer) }).ToRgba8();
        }
        else
        {
            var preview = new PreviewDocument(doc, factor);
            var proxy = preview.Sync();
            live = Compositor.Render(proxy, new RenderOptions { ActiveStroke = preview.MapStroke(stroke) }).ToRgba8();
        }

        var before = Compositor.Render(doc).ToRgba8();
        var (pixels, bounds) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        layer.Pixels = pixels;
        layer.Bounds = bounds;
        var baked = factor == 1 ? Compositor.Render(doc).ToRgba8() : Compositor.Render(new PreviewDocument(doc, factor).Sync()).ToRgba8();

        var report = FidelityReport.Compare(live, baked, doc.Width / factor, doc.Height / factor);
        if (factor == 1) Assert.True(report.MaxError <= 2, $"live stroke differs from baked by up to {report.MaxError}");
        else Assert.True(report.MeanError < 3, $"preview stroke mean error {report.MeanError:F2}");

        if (factor == 1)
        {
            // Nothing changed where nothing is selected: inside the hole and outside the ellipse.
            foreach (var (x, y) in new[] { (100, 130), (110, 150), (230, 40), (10, 120) })
            {
                int i = (y * doc.Width + x) * 4;
                Assert.Equal(before[i..(i + 4)], baked[i..(i + 4)]);
            }
        }
    }
}
