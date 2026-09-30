using Strayta.Core;
using Strayta.Rendering.Export;

namespace Strayta.Rendering.Tests;

public class ArtboardRenderingTests
{
    private static Raster Solid(int w, int h, byte r, byte g, byte b)
    {
        var planes = new[] { r, g, b }.Select(v =>
        {
            var p = Plane.Create(w, h, 8);
            Array.Fill(p.Data, v);
            return p;
        }).ToArray();
        return new Raster(ColorMode.Rgb, planes, null);
    }

    private static (Document Doc, LayerGroup Phone, LayerGroup Web, PixelLayer Red) Build()
    {
        var doc = new Document(300, 200, ColorMode.Rgb, 8);
        var phone = new LayerGroup { Name = "Phone", Artboard = new Artboard { Rect = new PixelRect(10, 10, 110, 190) } };
        // Reaches past the artboard's right edge: the part outside is clipped away.
        var red = new PixelLayer { Name = "red", Bounds = new PixelRect(60, 50, 160, 70), Pixels = Solid(100, 20, 255, 0, 0) };
        phone.Add(red);
        var web = new LayerGroup
        {
            Name = "Web",
            Artboard = new Artboard { Rect = new PixelRect(150, 10, 290, 110), Background = ArtboardBackground.Custom, Color = (0, 0, 80) },
        };
        doc.Root.Add(phone);
        doc.Root.Add(web);
        return (doc, phone, web, red);
    }

    private static byte[] Pixel(byte[] rgba, int w, int x, int y) => rgba[((y * w + x) * 4)..((y * w + x) * 4 + 4)];

    [Fact]
    public void Artboards_draw_their_background_and_clip_their_layers()
    {
        var (doc, _, _, _) = Build();
        var rgba = Compositor.Render(doc).ToRgba8();
        Assert.Equal([255, 255, 255, 255], Pixel(rgba, 300, 20, 20)); // white background
        Assert.Equal([255, 0, 0, 255], Pixel(rgba, 300, 100, 60)); // the layer inside
        Assert.Equal([0, 0, 0, 0], Pixel(rgba, 300, 130, 60)); // clipped at the artboard's edge: pasteboard
        Assert.Equal([0, 0, 80, 255], Pixel(rgba, 300, 200, 50)); // the second artboard's own color
        Assert.Equal([0, 0, 0, 0], Pixel(rgba, 300, 5, 5)); // pasteboard stays transparent
    }

    [Fact]
    public void Hidden_and_transparent_artboards()
    {
        var (doc, phone, web, _) = Build();
        web.Visible = false;
        phone.Artboard = phone.Artboard! with { Background = ArtboardBackground.Transparent };
        var rgba = Compositor.Render(doc).ToRgba8();
        Assert.Equal([0, 0, 0, 0], Pixel(rgba, 300, 20, 20));
        Assert.Equal([0, 0, 0, 0], Pixel(rgba, 300, 200, 50));
        Assert.Equal([255, 0, 0, 255], Pixel(rgba, 300, 100, 60));
    }

    [Fact]
    public void Preview_documents_scale_artboards()
    {
        var (doc, _, _, _) = Build();
        var preview = new PreviewDocument(doc, 2).Sync();
        var rgba = Compositor.Render(preview).ToRgba8();
        Assert.Equal([0, 0, 80, 255], Pixel(rgba, 150, 100, 25));
        Assert.Equal([0, 0, 0, 0], Pixel(rgba, 150, 2, 2));
    }

    [Fact]
    public void Exporting_an_artboard_gives_its_bounds_and_a_layer_gives_just_the_layer()
    {
        var (doc, phone, web, red) = Build();
        var board = ExportRenderer.Render(doc, ExportTarget.For(phone));
        Assert.Equal((100, 180), (board.Width, board.Height));
        Assert.Equal([255, 255, 255, 255], Pixel(board.Pixels, 100, 0, 0));
        Assert.Equal([255, 0, 0, 255], Pixel(board.Pixels, 100, 60, 45));

        var empty = ExportRenderer.Render(doc, ExportTarget.For(web));
        Assert.Equal((140, 100), (empty.Width, empty.Height)); // an artboard with no layers still has its background

        // A layer alone: no artboard background, not clipped, trimmed to its pixels.
        var layer = ExportRenderer.Render(doc, ExportTarget.For(red));
        Assert.Equal((100, 20), (layer.Width, layer.Height));
        var canvasSized = ExportRenderer.Render(doc, ExportTarget.For(red, trimToContent: false));
        Assert.Equal((300, 200), (canvasSized.Width, canvasSized.Height));
        Assert.Equal([0, 0, 0, 0], Pixel(canvasSized.Pixels, 300, 20, 20));
    }

    [Fact]
    public void Canvas_changes_move_artboards()
    {
        var (doc, phone, _, _) = Build();
        var change = Transforms.CanvasOperations.ResizeImage(doc, 150, 100, Transforms.ResampleMethod.Bicubic);
        Transforms.CanvasOperations.Apply(doc, change);
        Assert.Equal(new PixelRect(5, 5, 55, 95), phone.Artboard!.Rect);
    }
}
