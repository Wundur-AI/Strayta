using SkiaSharp;
using Strayta.Core;
using Strayta.Rendering.Export;

namespace Strayta.Rendering.Tests;

public class ImageAssetTests
{
    [Fact]
    public void Plain_file_names_are_assets_and_other_names_are_not()
    {
        var spec = Assert.Single(ImageAssetNames.Parse("icon.png"));
        Assert.Equal(new AssetSpec("icon.png", ExportFormat.Png, null, null, null, null), spec);
        Assert.Empty(ImageAssetNames.Parse("Layer 1"));
        Assert.Empty(ImageAssetNames.Parse("icon.png copy"));
        Assert.Empty(ImageAssetNames.Parse("readme.txt"));
        Assert.Equal(ExportFormat.Jpeg, ImageAssetNames.Parse("Photo.JPEG").Single().Format);
        Assert.Equal("Photo.jpg", ImageAssetNames.Parse("Photo.JPEG").Single().FileName);
    }

    [Fact]
    public void Scale_quality_and_several_files_are_read()
    {
        var two = ImageAssetNames.Parse("200% button@2x.png");
        Assert.Equal(2.0, two.Single().Scale);
        Assert.Equal("button@2x.png", two.Single().FileName);

        Assert.Equal(80, ImageAssetNames.Parse("logo.jpg80%").Single().Quality);
        Assert.Equal(60, ImageAssetNames.Parse("logo.jpg6").Single().Quality); // 1–10 is tenths
        Assert.Equal(8, ImageAssetNames.Parse("icon.png8").Single().Quality);
        Assert.Equal(70, ImageAssetNames.Parse("hero.webp70%").Single().Quality);

        var pair = ImageAssetNames.Parse("hero.png, 200% hero@2x.png");
        Assert.Equal(["hero.png", "hero@2x.png"], pair.Select(s => s.FileName));
        Assert.Equal([null, 2.0], pair.Select(s => s.Scale));
        Assert.Equal(2, ImageAssetNames.Parse("a.png + b.gif").Count);
    }

    [Fact]
    public void Sizes_units_and_folders_are_read()
    {
        var sized = ImageAssetNames.Parse("300x200 thumb.jpg").Single();
        Assert.Equal(new AssetLength(300, "px"), sized.Width);
        Assert.Equal(new AssetLength(200, "px"), sized.Height);
        var tall = ImageAssetNames.Parse("?x100 logo.png").Single();
        Assert.Null(tall.Width);
        Assert.Equal(100, tall.Height!.Value.Value);
        var print = ImageAssetNames.Parse("2in x 1in print.png").Single();
        Assert.Equal(new AssetLength(2, "in"), print.Width);
        Assert.Equal(144, print.Width!.Value.ToPixels(72));

        Assert.Equal("assets/icons/home.png", ImageAssetNames.Parse("assets/icons/home.png").Single().FileName);
        Assert.Equal("etc/passwd.png", ImageAssetNames.Parse("../../etc/passwd.png").Single().FileName); // no escaping the folder
        Assert.Equal("a_b.png", ImageAssetNames.Parse("a:b.png").Single().FileName);
    }

    [Fact]
    public void Default_layers_add_variants()
    {
        Assert.Empty(ImageAssetNames.Parse("default 200% @2x"));
        var defaults = ImageAssetNames.ParseDefaults("default 200% @2x, 300% @3x, 50% small/");
        Assert.Equal(3, defaults.Count);
        Assert.Equal(new AssetDefault(2, null, null, "", "@2x"), defaults[0]);
        Assert.Equal(new AssetDefault(0.5, null, null, "small", ""), defaults[2]);
        Assert.Empty(ImageAssetNames.ParseDefaults("defaults.png"));
    }

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

    [Fact]
    public void Generate_writes_each_asset_trimmed_and_scaled()
    {
        var doc = new Document(200, 100, ColorMode.Rgb, 8);
        doc.Root.Add(new PixelLayer { Name = "Background", Bounds = doc.Bounds, Pixels = Solid(200, 100, 255, 255, 255) });
        doc.Root.Add(new PixelLayer { Name = "icon.png, 200% icons/icon@2x.png", Bounds = new PixelRect(10, 20, 40, 40), Pixels = Solid(30, 20, 255, 0, 0) });
        doc.Root.Add(new PixelLayer { Name = "photo.jpg50%", Bounds = new PixelRect(100, 0, 150, 50), Pixels = Solid(50, 50, 0, 0, 255), Visible = false });
        doc.Root.Add(new PixelLayer { Name = "default 50% @half" });
        string dir = Path.Combine(Path.GetTempPath(), "export2-assets-" + Guid.NewGuid().ToString("N"));
        try
        {
            var results = ImageAssetGenerator.Generate(doc, dir);
            Assert.All(results, r => Assert.Null(r.Error));
            Assert.Equal(6, results.Count); // three files, each with its @half variant

            using (var icon = SKBitmap.Decode(Path.Combine(dir, "icon.png")))
            {
                Assert.Equal((30, 20), (icon.Width, icon.Height)); // trimmed to the layer, not the white background
                Assert.Equal(new SKColor(255, 0, 0), icon.GetPixel(5, 5));
            }
            using (var retina = SKBitmap.Decode(Path.Combine(dir, "icons", "icon@2x.png")))
                Assert.Equal((60, 40), (retina.Width, retina.Height));
            using (var half = SKBitmap.Decode(Path.Combine(dir, "icon@half.png")))
                Assert.Equal((15, 10), (half.Width, half.Height));
            using (var photo = SKBitmap.Decode(Path.Combine(dir, "photo.jpg"))) // hidden layers are assets too
            {
                Assert.Equal((50, 50), (photo.Width, photo.Height));
                Assert.True(photo.GetPixel(25, 25).Blue > 230);
            }
            // A rooted path on every system ("/work" becomes "D:\work" on Windows).
            string work = Path.Combine(Path.GetTempPath(), "work");
            Assert.Equal(Path.Combine(work, "Poster-assets"), ImageAssetGenerator.FolderFor(Path.Combine(work, "Poster.psd")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
