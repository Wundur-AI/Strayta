using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Rendering.Tests;

public class MaskStrokeRenderingTests
{
    private static Plane P(int w, int h, byte v) => new(w, h, 8, Enumerable.Repeat(v, w * h).ToArray());

    /// <summary>A green photo with the kind of layer under test above it.</summary>
    private static (Document Doc, LayerNode Target) Scene(string kind, bool hideAll)
    {
        var doc = new Document(256, 256, ColorMode.Rgb, 8);
        doc.Root.Add(new PixelLayer { Bounds = doc.Bounds, Pixels = new Raster(ColorMode.Rgb, [P(256, 256, 40), P(256, 256, 180), P(256, 256, 60)], null) });
        var mask = LayerMasks.Solid(reveal: !hideAll);
        var red = new PixelLayer
        {
            Bounds = new PixelRect(32, 32, 224, 224),
            Pixels = new Raster(ColorMode.Rgb, [P(192, 192, 230), P(192, 192, 20), P(192, 192, 20)], P(192, 192, 255)),
        };
        LayerNode target;
        switch (kind)
        {
            case "pixel":
                red.Mask = mask;
                target = red;
                doc.Root.Add(red);
                break;
            case "adjustment":
                target = new AdjustmentLayer { Adjustment = new InvertAdjustment(), Mask = mask };
                doc.Root.Add(target);
                break;
            case "isolated group":
            case "pass-through group":
                var group = new LayerGroup { Mask = mask, BlendMode = kind == "isolated group" ? BlendMode.Normal : BlendMode.PassThrough };
                group.Add(red);
                group.Add(new AdjustmentLayer { Adjustment = new InvertAdjustment() });
                target = group;
                doc.Root.Add(group);
                break;
            default: throw new ArgumentException(kind);
        }
        return (doc, target);
    }

    [Theory]
    [InlineData("pixel", false, 1)]
    [InlineData("pixel", true, 1)]
    [InlineData("adjustment", false, 1)]
    [InlineData("adjustment", true, 1)]
    [InlineData("isolated group", false, 1)]
    [InlineData("pass-through group", true, 1)]
    [InlineData("pixel", false, 4)]
    [InlineData("adjustment", true, 4)]
    public void Live_mask_stroke_matches_the_baked_mask(string kind, bool hideAll, int factor)
    {
        var (doc, target) = Scene(kind, hideAll);

        // Black hides on a reveal-all mask; white reveals on a hide-all one. Soft brush at 80% for partial values.
        var stroke = PaintStroke.ForMask(target, new BrushSettings(48, 0.4f, 0.8f), hideAll ? 1f : 0f, doc.Bounds);
        stroke.StrokeTo(10, 100);
        stroke.StrokeTo(200, 140);

        byte[] before = Compositor.Render(doc).ToRgba8();
        byte[] live = factor == 1
            ? Compositor.Render(doc, new RenderOptions { ActiveStroke = new StrokeOverlay(stroke, target) }).ToRgba8()
            : RenderPreview(doc, factor, stroke);

        target.SetMask(MaskBaker.Bake(target.GetMask()!, stroke, doc.BitDepth));
        byte[] baked = factor == 1 ? Compositor.Render(doc).ToRgba8() : RenderPreview(doc, factor, null);

        var report = FidelityReport.Compare(live, baked, doc.Width / factor, doc.Height / factor);
        if (factor == 1) Assert.True(report.MaxError <= 2, $"live mask stroke differs from baked by up to {report.MaxError}");
        else Assert.True(report.MeanError < 3, $"preview mask stroke mean error {report.MeanError:F2}");
        // And the stroke did something visible.
        if (factor == 1) Assert.True(FidelityReport.Compare(before, baked, doc.Width, doc.Height).MaxError > 50);
    }

    private static byte[] RenderPreview(Document doc, int factor, PaintStroke? stroke)
    {
        var preview = new PreviewDocument(doc, factor);
        var proxy = preview.Sync();
        return Compositor.Render(proxy, new RenderOptions { ActiveStroke = preview.MapStroke(stroke) }).ToRgba8();
    }

    [Fact]
    public void Painting_black_into_a_mask_hides_the_layer_there()
    {
        var (doc, target) = Scene("pixel", hideAll: false);
        var stroke = PaintStroke.ForMask(target, new BrushSettings(20, 1f, 1f), 0f, doc.Bounds);
        stroke.StrokeTo(128, 128);

        var img = Compositor.Render(doc, new RenderOptions { ActiveStroke = new StrokeOverlay(stroke, target) }).ToRgba8();

        int at = (128 * 256 + 128) * 4, away = (60 * 256 + 60) * 4;
        Assert.Equal([40, 180, 60, 255], img[at..(at + 4)]);    // the photo shows through
        Assert.Equal([230, 20, 20, 255], img[away..(away + 4)]); // red elsewhere
    }

    [Fact]
    public void Disabled_masks_ignore_mask_strokes_live_as_when_baked()
    {
        var (doc, target) = Scene("pixel", hideAll: false);
        target.SetMask(target.GetMask()!.WithDisabled(true));
        var stroke = PaintStroke.ForMask(target, new BrushSettings(20, 1f, 1f), 0f, doc.Bounds);
        stroke.StrokeTo(128, 128);

        Assert.Equal(Compositor.Render(doc).ToRgba8(),
            Compositor.Render(doc, new RenderOptions { ActiveStroke = new StrokeOverlay(stroke, target) }).ToRgba8());
    }
}
