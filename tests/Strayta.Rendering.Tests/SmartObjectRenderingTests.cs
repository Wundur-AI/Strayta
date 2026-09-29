using Strayta.Core;
using Strayta.Psd;
using Strayta.Rendering.Filters;
using Strayta.Rendering.Transforms;

namespace Strayta.Rendering.Tests;

/// <summary>Smart objects drawn from their content: warps, perspective placement and smart filters.</summary>
public class SmartObjectRenderingTests
{
    private static Raster Checker(int w, int h, int cell)
    {
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, 8)).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)(((x / cell) + (y / cell)) % 2 == 0 ? 230 : 20);
                int i = y * w + x;
                planes[0].Data[i] = v; planes[1].Data[i] = (byte)(255 - v); planes[2].Data[i] = 128; planes[3].Data[i] = 255;
            }
        return new Raster(ColorMode.Rgb, planes[..3], planes[3]);
    }

    private static readonly (double X, double Y)[] Box = [(20, 10), (120, 10), (120, 60), (20, 60)];

    private static byte[] Rgba((Raster? Pixels, PixelRect Bounds) r, PixelRect area)
    {
        var o = new byte[area.Width * area.Height * 4];
        if (r.Pixels is null) return o;
        var src = RgbaConverter.ToRgba8(r.Pixels);
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                if (x < r.Bounds.Left || y < r.Bounds.Top || x >= r.Bounds.Right || y >= r.Bounds.Bottom) continue;
                int s = ((y - r.Bounds.Top) * r.Bounds.Width + x - r.Bounds.Left) * 4, d = ((y - area.Top) * area.Width + x - area.Left) * 4;
                Array.Copy(src, s, o, d, 4);
            }
        return o;
    }

    [Fact]
    public void An_identity_custom_warp_draws_like_no_warp()
    {
        var content = Checker(100, 50, 7);
        var identity = new WarpSpec { Style = "warpCustom", Bounds = (0, 0, 100, 50), Mesh = WarpMesh.Identity((0, 0, 100, 50)) };
        var plain = SmartObjectPlacement.Render(content, 100, 50, Box, null, ResampleFilter.Bicubic);
        var warped = SmartObjectPlacement.Render(content, 100, 50, Box, identity, ResampleFilter.Bicubic);
        var area = new PixelRect(10, 0, 130, 70);
        var a = Rgba(plain, area);
        var b = Rgba(warped, area);
        int worst = a.Zip(b, (x, y) => Math.Abs(x - y)).Max();
        Assert.True(worst <= 2, $"largest difference {worst}");
    }

    [Fact]
    public void A_custom_warp_moves_content_where_its_control_points_go()
    {
        // Pull the middle two top control points up by 30: the top edge bows up, so its middle rises about 22 px.
        var mesh = WarpMesh.Identity((0, 0, 100, 50)).Select((p, i) => i is 1 or 2 ? (p.X, p.Y - 30) : p).ToArray();
        var warp = new WarpSpec { Style = "warpCustom", Bounds = (0, 0, 100, 50), Mesh = mesh };
        var m = WarpMesh.From(warp)!;
        Assert.Equal(-22.5, m.Map(50, 0).Y, 6);
        Assert.Equal((0.0, 0.0), m.Map(0, 0));
        // Placed: the control points' box (y -30..50) fills the corners' box, so the top middle is at the top edge.
        var (pixels, bounds) = SmartObjectPlacement.Render(Checker(100, 50, 5), 100, 50, Box, warp, ResampleFilter.Bilinear);
        Assert.NotNull(pixels);
        Assert.InRange(bounds.Top, 7, 11);
        var placed = SmartObjectPlacement.PlacedMesh(m, 100, 50, Projective.RectToQuad(100, 50, Box));
        Assert.Equal(10 + 50 * 7.5 / 80, placed.Map(50, 0).Y, 6);
    }

    [Fact]
    public void Named_styles_and_the_cylinder_make_meshes_that_bend()
    {
        foreach (var style in WarpStyles.Names)
        {
            var spec = new WarpSpec { Style = style, Value = 50, Bounds = (0, 0, 100, 50) };
            var mesh = WarpMesh.From(spec);
            Assert.NotNull(mesh);
            var identity = WarpMesh.Identity((0, 0, 100, 50));
            double moved = mesh!.Points.Zip(identity, (p, q) => Math.Abs(p.X - q.X) + Math.Abs(p.Y - q.Y)).Max();
            Assert.True(moved > 1, $"{style} moves its control points ({moved:F2})");
        }
        var cylinder = WarpMesh.From(new WarpSpec { Style = "warpCylinder", Bounds = (0, 0, 100, 50), Values = [0, 50, 100, 0, 0.1, 0.2, 1] })!;
        // The front's middle column bulges down by the bottom ellipse's depth (0.2 × radius 50 = 10) at the bottom.
        Assert.Equal(60, cylinder.Map(50, 50).Y, 1);
        Assert.Equal(50, cylinder.Map(0.5, 50).Y, 0);
        // Content near the sides is foreshortened: a tenth of the width in covers less than a tenth across.
        Assert.True(cylinder.Map(10, 25).X < 6);
    }

    [Fact]
    public void Smart_filters_stack_with_blend_modes_opacity_and_a_mask()
    {
        var content = Checker(40, 40, 4);
        var canvas = new PixelRect(0, 0, 60, 60);
        var bounds = new PixelRect(10, 10, 50, 50);
        var blur = new GaussianBlurFilter(2);
        var full = SmartFilterStack.Apply(content, bounds, [new SmartFilterStep(blur)], canvas);
        var direct = FilterEngine.ApplyToLayer(content, bounds, blur, new FilterScope(canvas));
        Assert.Equal(direct.Bounds, full.Bounds);
        Assert.Equal(RgbaConverter.ToRgba8(direct.Pixels!), RgbaConverter.ToRgba8(full.Pixels!));

        // At 50% the result is halfway between the input and the blur.
        var half = SmartFilterStack.Apply(content, bounds, [new SmartFilterStep(blur, BlendMode.Normal, 0.5f)], canvas);
        var area = new PixelRect(16, 16, 44, 44);
        var i0 = Rgba((content, bounds), area);
        var i1 = Rgba(full, area);
        var ih = Rgba(half, area);
        for (int k = 0; k < ih.Length; k += 4)
            Assert.InRange(ih[k], (i0[k] + i1[k]) / 2 - 3, (i0[k] + i1[k]) / 2 + 3);

        // A black filter mask keeps the unfiltered pixels.
        var black = new LayerMask { Bounds = canvas, Pixels = Plane.Create(60, 60, 8), DefaultColor = 0 };
        var masked = SmartFilterStack.Apply(content, bounds, [new SmartFilterStep(blur)], canvas, black);
        Assert.Equal(i0, Rgba(masked, area));

        // Multiply at full strength darkens compared with Normal.
        var mult = Rgba(SmartFilterStack.Apply(content, bounds, [new SmartFilterStep(blur, BlendMode.Multiply)], canvas), area);
        Assert.True(mult.Where((_, k) => k % 4 == 0).Sum(b => b) < i1.Where((_, k) => k % 4 == 0).Sum(b => b));
    }

    // ---- Real files: warped smart objects redrawn from their content match Photoshop's pixels ------------------

    private static readonly string? CorpusDir = Environment.GetEnvironmentVariable("STRAYTA_CORPUS");

    public static TheoryData<string> WarpedFiles()
    {
        var data = new TheoryData<string>();
        if (CorpusDir is not null && Directory.Exists(CorpusDir))
            foreach (var f in Directory.EnumerateFiles(CorpusDir, "*.ps?", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("._")).Order())
            {
                var file = PsdFile.Open(f, new PsdReadOptions { SkipLayerPixels = true, SkipComposite = true });
                if (file.Layers.Any(r => PsdLiveContent.ReadSmartObject(r) is { Warped: true })) data.Add(Path.GetRelativePath(CorpusDir, f));
            }
        if (data.Count == 0) data.Add("");
        return data;
    }

    [Theory]
    [MemberData(nameof(WarpedFiles))]
    public void Warped_smart_objects_redrawn_from_content_match_photoshops_pixels(string relativePath)
    {
        if (relativePath.Length == 0) Assert.Skip("Set STRAYTA_CORPUS to a folder with warped smart objects to run this test.");
        var file = PsdFile.Open(Path.Combine(CorpusDir!, relativePath), new PsdReadOptions { MaxRawBlockBytes = long.MaxValue, SkipComposite = true });
        foreach (var r in file.Layers)
        {
            if (PsdLiveContent.ReadSmartObject(r) is not { Warped: true, HasFilters: false } so || r.Rect.IsEmpty) continue;
            if (PsdLiveContent.FindEmbeddedFile(file, so.UniqueId) is not { } embedded) continue;
            Raster? content;
            if (embedded.Data.AsSpan(0, 4).SequenceEqual("8BPS"u8))
            {
                var inner = PsdFile.Read(new MemoryStream(embedded.Data));
                var innerDoc = inner.ToDocument();
                // Without "Maximize Compatibility" the stored composite is not the image: render the layers.
                content = inner.HasRealMergedData != false && innerDoc.Composite is { } c ? c : Compositor.Render(innerDoc).ToRaster(innerDoc.ColorMode, innerDoc.BitDepth);
            }
            else continue; // standard images are decoded by Strayta.Imaging, which these tests do not use
            if (content is null || content.ColorMode != file.Header.ColorMode || content.BitDepth != file.Header.BitDepth) continue;
            var drawn = SmartObjectPlacement.Render(content, so.Width, so.Height, so.Corners, so.Warp, ResampleFilter.Bicubic);
            var planes = Enumerable.Range(0, 3).Select(c => r.ChannelData[(short)c]).ToList();
            var stored = (Pixels: (Raster?)new Raster(file.Header.ColorMode, planes, r.ChannelData.GetValueOrDefault(PsdChannelId.Transparency)), Bounds: r.Rect);
            var union = new PixelRect(Math.Min(r.Rect.Left, drawn.Bounds.Left), Math.Min(r.Rect.Top, drawn.Bounds.Top),
                Math.Max(r.Rect.Right, drawn.Bounds.Right), Math.Max(r.Rect.Bottom, drawn.Bounds.Bottom));
            var a = Rgba(drawn, union);
            var b = Rgba(stored, union);
            long seen = 0, match = 0;
            for (int k = 0; k < a.Length; k += 4)
            {
                if (a[k + 3] == 0 && b[k + 3] == 0) continue;
                seen++;
                int e = Math.Abs(a[k + 3] - b[k + 3]);
                for (int c = 0; c < 3; c++) e = Math.Max(e, Math.Abs(a[k + c] * a[k + 3] / 255 - b[k + c] * b[k + 3] / 255));
                if (e <= FidelityReport.Tolerance) match++;
            }
            double percent = 100.0 * match / Math.Max(1, seen);
            Assert.True(percent >= 85, $"\"{r.Name}\" ({so.Warp!.Style}) matches Photoshop's pixels at {percent:F1}% (seen {seen}, drawn {drawn.Bounds}, stored {r.Rect}, content {content.Width}x{content.Height} alpha {content.Alpha is not null})");
        }
    }
}
