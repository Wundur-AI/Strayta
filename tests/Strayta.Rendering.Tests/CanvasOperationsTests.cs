using Strayta.Core;
using Strayta.Psd;
using Strayta.Rendering.Transforms;

namespace Strayta.Rendering.Tests;

public class CanvasOperationsTests
{
    /// <summary>An 8-bit RGB raster whose red is x, green is y and blue a fixed value, so positions can be read back.</summary>
    private static Raster Coords(int w, int h, byte blue = 90, bool alpha = false)
    {
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, 8)).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                planes[0].Data[i] = (byte)x;
                planes[1].Data[i] = (byte)y;
                planes[2].Data[i] = blue;
                planes[3].Data[i] = 255;
            }
        return new Raster(ColorMode.Rgb, planes[..3], alpha ? planes[3] : null);
    }

    private static Plane MaskPlane(int w, int h)
    {
        var p = Plane.Create(w, h, 8);
        for (int i = 0; i < p.Data.Length; i++) p.Data[i] = (byte)(i % w * 3);
        return p;
    }

    /// <summary>A 200×100 document: opaque Background, a layer partly off the canvas with a mask, a group with a mask, an adjustment and a text layer.</summary>
    private static (Document Doc, PixelLayer Background, PixelLayer Layer, LayerGroup Group, PixelLayer Text) Sample()
    {
        var doc = new Document(200, 100, ColorMode.Rgb, 8);
        var background = new PixelLayer { Name = "Background", Bounds = doc.Bounds, Pixels = Coords(200, 100) };
        var layer = new PixelLayer
        {
            Name = "Layer 1", Bounds = new PixelRect(-20, 10, 60, 50), Pixels = Coords(80, 40, 200, alpha: true),
            Mask = new LayerMask { Bounds = new PixelRect(-20, 10, 60, 50), Pixels = MaskPlane(80, 40), DefaultColor = 0 },
        };
        var group = new LayerGroup { Name = "Group", Mask = new LayerMask { Bounds = new PixelRect(100, 0, 150, 100), Pixels = MaskPlane(50, 100), DefaultColor = 255 } };
        var adjustment = new AdjustmentLayer { Name = "Invert", Adjustment = new InvertAdjustment(), Mask = LayerMasks.Solid(reveal: true) };
        var text = new PixelLayer { Name = "Title", Bounds = new PixelRect(120, 20, 180, 40), Pixels = Coords(60, 20, 30, alpha: true) };
        text.Tags.Add("text");
        doc.Root.Add(background);
        doc.Root.Add(layer);
        doc.Root.Add(group);
        group.Add(adjustment);
        group.Add(text);
        doc.Composite = Coords(200, 100, 7);
        return (doc, background, layer, group, text);
    }

    private static byte At(Raster r, PixelRect bounds, int plane, int x, int y) =>
        (plane < r.ColorPlanes.Count ? r.ColorPlanes[plane] : r.Alpha!).Data[(y - bounds.Top) * bounds.Width + (x - bounds.Left)];

    [Fact]
    public void Crop_with_delete_cuts_every_layer_and_mask_to_the_new_canvas()
    {
        var (doc, background, layer, group, text) = Sample();
        var change = CanvasOperations.Crop(doc, new PixelRect(10, 20, 130, 80), deleteCroppedPixels: true);
        Assert.True(change.IsWholePixelMove);
        Assert.Empty(change.ResampledLiveLayers);
        CanvasOperations.Apply(doc, change);

        Assert.Equal((120, 60), (doc.Width, doc.Height));
        Assert.Equal(doc.Bounds, background.Bounds);
        Assert.Null(background.Pixels!.Alpha);
        Assert.Equal(10 + 5, At(background.Pixels, background.Bounds, 0, 5, 7)); // old x = 15
        Assert.Equal(20 + 7, At(background.Pixels, background.Bounds, 1, 5, 7)); // old y = 27

        // Layer 1 covered (-20,10)-(60,50): now (-30,-10)-(50,30), cut to (0,0)-(50,30).
        Assert.Equal(new PixelRect(0, 0, 50, 30), layer.Bounds);
        Assert.Equal(30, At(layer.Pixels!, layer.Bounds, 0, 0, 0)); // layer-local x of old doc x 10
        Assert.Equal(new PixelRect(0, 0, 50, 30), layer.Mask!.Bounds);
        Assert.Equal((byte)(30 * 3), layer.Mask.Pixels!.Data[0]);

        Assert.Equal(new PixelRect(90, 0, 120, 60), group.Mask!.Bounds);
        // Type is never cut, only moved (its live data would redraw it anyway).
        Assert.Equal(new PixelRect(110, 0, 170, 20), text.Bounds);
        Assert.Equal(60, text.Pixels!.Width);

        Assert.Equal((120, 60), (doc.Composite!.Width, doc.Composite.Height));
        Assert.Equal(10, doc.Composite.ColorPlanes[0].Data[0]);
    }

    [Fact]
    public void Crop_without_delete_keeps_hidden_pixels_and_turns_the_background_into_a_layer()
    {
        var (doc, background, layer, _, _) = Sample();
        var pixels = layer.Pixels;
        CanvasOperations.Apply(doc, CanvasOperations.Crop(doc, new PixelRect(10, 20, 130, 80), deleteCroppedPixels: false));

        Assert.Same(pixels, layer.Pixels); // a pure move shares the pixels
        Assert.Equal(new PixelRect(-30, -10, 50, 30), layer.Bounds);
        Assert.Equal(new PixelRect(-10, -20, 190, 80), background.Bounds);
        Assert.Equal("Layer 0", background.Name);
        Assert.NotNull(background.Pixels!.Alpha);
        Assert.All(background.Pixels.Alpha!.Data, a => Assert.Equal(255, a));
    }

    [Fact]
    public void Crop_past_the_canvas_fills_the_background_with_the_fill_color()
    {
        var (doc, background, _, _, _) = Sample();
        CanvasOperations.Apply(doc, CanvasOperations.Crop(doc, new PixelRect(-10, -5, 50, 40), deleteCroppedPixels: true, fill: [1f, 0f, 0f]));
        Assert.Equal(new PixelRect(0, 0, 60, 45), background.Bounds);
        Assert.Null(background.Pixels!.Alpha);
        Assert.Equal((255, 0, 0), (At(background.Pixels, background.Bounds, 0, 2, 2), At(background.Pixels, background.Bounds, 1, 2, 2), At(background.Pixels, background.Bounds, 2, 2, 2)));
        Assert.Equal((5, 3, 90), (At(background.Pixels, background.Bounds, 0, 15, 8), At(background.Pixels, background.Bounds, 1, 15, 8), At(background.Pixels, background.Bounds, 2, 15, 8)));
    }

    [Fact]
    public void Rotated_crop_resamples_layers_and_reports_live_layers()
    {
        var (doc, background, layer, _, text) = Sample();
        // A quarter turn: old (x, y) -> (99 - y + 1, x) ... i.e. the whole canvas rotated clockwise onto 100×200.
        var map = new Affine(0, -1, 1, 0, 100, 0);
        var change = CanvasOperations.Crop(doc, map, 100, 200, deleteCroppedPixels: true);
        Assert.False(change.IsWholePixelMove);
        Assert.Contains(text, change.ResampledLiveLayers);
        CanvasOperations.Apply(doc, change);

        Assert.Equal(new PixelRect(0, 0, 100, 200), background.Bounds);
        Assert.Null(background.Pixels!.Alpha); // still a Background: fully covered, no transparency
        // New pixel (nx, ny) comes from old (ny, 99 - nx).
        Assert.Equal(37, At(background.Pixels, background.Bounds, 0, 10, 37));
        Assert.Equal(89, At(background.Pixels, background.Bounds, 1, 10, 37));
        Assert.Equal(layer.Mask!.Bounds, layer.Bounds);
    }

    [Fact]
    public void Rotating_by_a_small_angle_keeps_opaque_content_opaque_and_masks_follow()
    {
        var (doc, background, layer, _, _) = Sample();
        var map = Affine.Translation(-100, -50).Then(Affine.Rotation(5 * Math.PI / 180)).Then(Affine.Translation(60, 30));
        CanvasOperations.Apply(doc, CanvasOperations.Crop(doc, map, 120, 60, deleteCroppedPixels: true, fill: [0f, 0f, 0f]));
        Assert.Equal(doc.Bounds, background.Bounds);
        Assert.Null(background.Pixels!.Alpha);
        Assert.NotNull(layer.Mask!.Pixels);
        Assert.True(layer.Mask.Bounds.Width > 0 && layer.Mask.Bounds.Right <= 120);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(20, 10)]
    [InlineData(40, 20)]
    public void Canvas_size_extends_the_background_and_moves_other_layers(int ox, int oy)
    {
        var (doc, background, layer, group, _) = Sample();
        var pixels = layer.Pixels;
        CanvasOperations.Apply(doc, CanvasOperations.ResizeCanvas(doc, 240, 120, ox, oy, [0f, 0f, 1f]));
        Assert.Equal((240, 120), (doc.Width, doc.Height));
        Assert.Equal(doc.Bounds, background.Bounds);
        Assert.Null(background.Pixels!.Alpha);
        Assert.Equal("Background", background.Name);
        int ex = ox == 0 ? 239 : 0, ey = oy == 0 ? 119 : 0; // a pixel of the new area
        Assert.Equal((0, 0, 255), (At(background.Pixels, doc.Bounds, 0, ex, ey), At(background.Pixels, doc.Bounds, 1, ex, ey), At(background.Pixels, doc.Bounds, 2, ex, ey)));
        Assert.Equal((50, 60), (At(background.Pixels, doc.Bounds, 0, ox + 50, oy + 60), At(background.Pixels, doc.Bounds, 1, ox + 50, oy + 60)));
        Assert.Same(pixels, layer.Pixels);
        Assert.Equal(new PixelRect(-20 + ox, 10 + oy, 60 + ox, 50 + oy), layer.Bounds);
        Assert.Equal(new PixelRect(100 + ox, oy, 150 + ox, 100 + oy), group.Mask!.Bounds);
    }

    [Fact]
    public void Smaller_canvas_cuts_the_background_but_keeps_layer_pixels()
    {
        var (doc, background, layer, _, _) = Sample();
        CanvasOperations.Apply(doc, CanvasOperations.ResizeCanvas(doc, 100, 50, -50, -25));
        Assert.Equal(doc.Bounds, background.Bounds);
        Assert.Equal(50, At(background.Pixels!, doc.Bounds, 0, 0, 0));
        Assert.Equal(new PixelRect(-70, -15, 10, 25), layer.Bounds);
        Assert.Equal(80, layer.Pixels!.Width);
    }

    [Theory]
    [InlineData(ResampleMethod.Bicubic)]
    [InlineData(ResampleMethod.Bilinear)]
    [InlineData(ResampleMethod.NearestNeighbor)]
    public void Image_size_gives_exact_dimensions_and_scales_everything(ResampleMethod method)
    {
        var (doc, background, layer, group, text) = Sample();
        var change = CanvasOperations.ResizeImage(doc, 67, 33, method);
        Assert.Contains(text, change.ResampledLiveLayers);
        CanvasOperations.Apply(doc, change);
        Assert.Equal((67, 33), (doc.Width, doc.Height));
        Assert.Equal(doc.Bounds, background.Bounds);
        Assert.Equal((67, 33), (background.Pixels!.Width, background.Pixels.Height));
        Assert.Null(background.Pixels.Alpha);
        Assert.Equal((67, 33), (doc.Composite!.Width, doc.Composite.Height));
        // Layer 1 was (-20,10)-(60,50) in 200×100: about (-6.7,3.3)-(20.1,16.5) now.
        Assert.InRange(layer.Bounds.Left, -8, -6);
        Assert.InRange(layer.Bounds.Right, 20, 21);
        Assert.InRange(group.Mask!.Bounds.Left, 33, 34);
        Assert.InRange(group.Mask.Bounds.Right, 50, 51);
    }

    [Theory]
    [InlineData(0.5, 0.5, 0, 0)]
    [InlineData(0.37, 0.61, 3.25, -1.5)]
    [InlineData(1.7, 1.3, 0.5, 2)]
    [InlineData(0.2, 2.5, -4, 0)]
    public void Separable_scaling_matches_the_general_resampler(double sx, double sy, double dx, double dy)
    {
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(61, 47, 8)).ToArray();
        for (int y = 0; y < 47; y++)
            for (int x = 0; x < 61; x++)
            {
                int i = y * 61 + x;
                planes[0].Data[i] = (byte)(x * 4);
                planes[1].Data[i] = (byte)(128 + 100 * Math.Sin(x * 0.4 + y * 0.3));
                planes[2].Data[i] = (byte)((x ^ y) * 4);
                planes[3].Data[i] = (byte)(40 + (x + y) * 2);
            }
        var raster = new Raster(ColorMode.Rgb, planes[..3], planes[3]);
        var bounds = new PixelRect(-5, 7, 56, 54);
        var map = new Affine(sx, 0, 0, sy, dx, dy);
        foreach (var filter in new[] { ResampleFilter.Bicubic, ResampleFilter.Bilinear })
        {
            var (a, ab) = SeparableScale.Raster(raster, bounds, map, filter, null, default);
            var (b, bb) = Resampler.TransformRaster(ResampleSource.FromRaster(raster), bounds, map, filter);
            Assert.Equal(bb, ab);
            for (int p = 0; p < 3; p++)
                for (int i = 0; i < a!.ColorPlanes[p].Data.Length; i++)
                    if (b!.Alpha!.Data[i] > 8) Assert.InRange(a.ColorPlanes[p].Data[i] - b.ColorPlanes[p].Data[i], -1, 1);
            for (int i = 0; i < a!.Alpha!.Data.Length; i++) Assert.InRange(a.Alpha.Data[i] - b!.Alpha!.Data[i], -1, 1);

            var mask = new LayerMask { Bounds = bounds, Pixels = planes[1], DefaultColor = 255 };
            var ma = SeparableScale.Mask(mask, planes[1], map, filter, null, default);
            var mb = Resampler.TransformMask(mask, ResampleSource.FromPlane(planes[1]), map, filter);
            Assert.Equal(mb.Bounds, ma.Bounds);
            for (int i = 0; i < ma.Pixels!.Data.Length; i++) Assert.InRange(ma.Pixels.Data[i] - mb.Pixels!.Data[i], -1, 1);
        }
    }

    [Fact]
    public void Nearest_neighbor_doubling_copies_pixels_exactly()
    {
        var (doc, background, _, _, _) = Sample();
        CanvasOperations.Apply(doc, CanvasOperations.ResizeImage(doc, 400, 200, ResampleMethod.NearestNeighbor));
        for (int y = 0; y < 200; y += 13)
            for (int x = 0; x < 400; x += 7)
                Assert.Equal(x / 2, At(background.Pixels!, doc.Bounds, 0, x, y));
    }

    [Trait("Category", "Performance")] // timed: runs in the separate performance pass (Directory.Build.props)
    [Fact]
    public void Image_size_of_a_large_layered_document_is_fast()
    {
        var doc = new Document(4000, 3000, ColorMode.Rgb, 8);
        var planes = Enumerable.Range(0, 3).Select(_ => Plane.Create(4000, 3000, 8)).ToArray();
        doc.Root.Add(new PixelLayer { Name = "Background", Bounds = doc.Bounds, Pixels = new Raster(ColorMode.Rgb, planes, null) });
        doc.Root.Add(new PixelLayer { Name = "Photo", Bounds = new PixelRect(400, 300, 3600, 2700), Pixels = Coords(3200, 2400, alpha: true) });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CanvasOperations.Apply(doc, CanvasOperations.ResizeImage(doc, 2000, 1500, ResampleMethod.Bicubic));
        Assert.Equal((2000, 1500), (doc.Width, doc.Height));
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Trim_finds_the_non_transparent_or_non_corner_colored_area()
    {
        int w = 20, h = 10;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = ((byte)10, (byte)20, (byte)30, (byte)255);
        for (int y = 3; y < 6; y++)
            for (int x = 4; x < 9; x++) rgba[(y * w + x) * 4] = 200;
        Assert.Equal(new PixelRect(4, 3, 9, 6), CanvasOperations.TrimBounds(rgba, w, h, TrimBasis.TopLeftColor));
        Assert.Equal(new PixelRect(0, 3, 20, 6), CanvasOperations.TrimBounds(rgba, w, h, TrimBasis.BottomRightColor, left: false, right: false));
        Assert.Equal(new PixelRect(0, 0, 20, 10), CanvasOperations.TrimBounds(rgba, w, h, TrimBasis.Transparent));
    }

    [Fact]
    public void Psd_round_trip_after_crop_keeps_hidden_pixels_and_resolution()
    {
        var (doc, background, layer, _, _) = Sample();
        doc.Root.Remove(doc.Root.Children[2]); // the group holds an adjustment layer without file data; not needed here
        doc.Resolution = 300;
        CanvasOperations.Apply(doc, CanvasOperations.Crop(doc, new PixelRect(10, 20, 130, 80), deleteCroppedPixels: false));

        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var file = PsdFile.Read(ms, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
        var again = file.ToDocument();
        Assert.Equal((120, 60), (again.Width, again.Height));
        Assert.Equal(300, again.Resolution);
        var saved = again.Root.Children.OfType<PixelLayer>().Single(l => l.Name == "Layer 1");
        Assert.Equal(new PixelRect(-30, -10, 50, 30), saved.Bounds);
        Assert.Equal(layer.Pixels!.ColorPlanes[0].Data, saved.Pixels!.ColorPlanes[0].Data);
        Assert.Equal(layer.Mask!.Bounds, saved.Mask!.Bounds);
        var floated = again.Root.Children.OfType<PixelLayer>().Single(l => l.Name == "Layer 0");
        Assert.Equal(background.Bounds, floated.Bounds);
        Assert.Equal(background.Pixels!.ColorPlanes[1].Data, floated.Pixels!.ColorPlanes[1].Data);
    }
}
