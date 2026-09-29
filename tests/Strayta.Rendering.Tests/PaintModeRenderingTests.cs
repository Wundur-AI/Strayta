using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Rendering.Tests;

/// <summary>The live stroke overlay with the brush options agrees with the committed stroke; cloned previews are filtered.</summary>
public class PaintModeRenderingTests
{
    private static (Document Doc, PixelLayer Layer) Scene()
    {
        var doc = new Document(200, 200, ColorMode.Rgb, 8);
        Plane P(byte v) => new(100, 100, 8, Enumerable.Repeat(v, 100 * 100).ToArray());
        var layer = new PixelLayer { Bounds = new PixelRect(50, 50, 150, 150), Pixels = new Raster(ColorMode.Rgb, [P(40), P(160), P(220)], P(255)) };
        doc.Root.Add(layer);
        return (doc, layer);
    }

    [Theory]
    [InlineData(PaintMode.Normal, 0.4f)]
    [InlineData(PaintMode.Multiply, 1f)]
    [InlineData(PaintMode.Behind, 1f)]
    [InlineData(PaintMode.Clear, 1f)]
    [InlineData(PaintMode.Color, 1f)]
    [InlineData(PaintMode.Dissolve, 1f)]
    [InlineData(PaintMode.Screen, 0.5f)]
    public void Live_stroke_in_a_mode_matches_the_baked_result(PaintMode mode, float flow)
    {
        var (doc, layer) = Scene();
        var stroke = new PaintStroke(layer, new BrushSettings(50, 0.3f, 0.8f) { Mode = mode, Flow = flow }, new RgbColor(0.9f, 0.3f, 0.1f), false, doc.Bounds);
        stroke.StrokeTo(20, 100); // starts outside the layer, so Behind and the blend modes paint transparent pixels too
        stroke.StrokeTo(170, 110);
        stroke.Airbrush();

        var live = Compositor.Render(doc, new RenderOptions { ActiveStroke = new StrokeOverlay(stroke, layer) }).ToRgba8();
        var (pixels, bounds) = StrokeBaker.Bake(layer, stroke, ColorMode.Rgb, 8);
        layer.Pixels = pixels;
        layer.Bounds = bounds;
        var baked = Compositor.Render(doc).ToRgba8();
        var report = FidelityReport.Compare(live, baked, doc.Width, doc.Height);
        Assert.True(report.MaxError <= 2, $"{mode}: live stroke differs from baked by up to {report.MaxError}");
    }

    [Fact]
    public void Cloned_detail_in_a_downscaled_preview_is_averaged_not_aliased()
    {
        // The source is a one-pixel checkerboard; at 1/4 scale point samples would show pure black or white.
        var doc = new Document(256, 256, ColorMode.Rgb, 8);
        var data = new byte[256 * 256];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++) data[y * 256 + x] = (byte)((x + y) % 2 * 255);
        Plane P() => new(256, 256, 8, (byte[])data.Clone());
        var layer = new PixelLayer { Bounds = doc.Bounds, Pixels = new Raster(ColorMode.Rgb, [P(), P(), P()], null) };
        doc.Root.Add(layer);
        var source = new CloneSource(1, 0, PixelSource.FromRaster(layer.Pixels, layer.Bounds)); // an odd offset flips the pattern
        var stroke = PaintStroke.Cloning(layer, false, new BrushSettings(120, 1f, 1f), source, doc.Bounds);
        stroke.StrokeTo(128, 128);

        var preview = new PreviewDocument(doc, 4);
        var proxy = preview.Sync();
        var rgba = Compositor.Render(proxy, new RenderOptions { ActiveStroke = preview.MapStroke(stroke) }).ToRgba8();
        for (int y = 26; y < 38; y++)
            for (int x = 26; x < 38; x++)
            {
                byte v = rgba[(y * 64 + x) * 4];
                Assert.InRange(v, 118, 138);
            }
    }
}
