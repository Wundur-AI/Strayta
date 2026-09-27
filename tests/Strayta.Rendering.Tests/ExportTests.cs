using SkiaSharp;
using Strayta.Core;
using Strayta.Rendering.Export;

namespace Strayta.Rendering.Tests;

public class ExportTests
{
    /// <summary>A gradient with a transparent strip and a half-transparent band.</summary>
    private static byte[] Image(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                rgba[i] = (byte)(x * 255 / (w - 1));
                rgba[i + 1] = (byte)(y * 255 / (h - 1));
                rgba[i + 2] = (byte)(128 + 100 * Math.Sin(x * 0.1));
                rgba[i + 3] = x < 10 ? (byte)0 : y < 10 ? (byte)128 : (byte)255;
            }
        return rgba;
    }

    private static SKBitmap Decode(byte[] file)
    {
        using var codec = SKCodec.Create(new MemoryStream(file));
        Assert.NotNull(codec);
        var bmp = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(bmp.Info, bmp.GetPixels()));
        return bmp;
    }

    [Fact]
    public void PngRoundTripsExactlyWithTransparency()
    {
        int w = 97, h = 61;
        var rgba = Image(w, h);
        using var ms = new MemoryStream();
        ImageExporter.Encode(ms, (byte[])rgba.Clone(), w, h, new ExportOptions { Format = ExportFormat.Png });
        using var bmp = Decode(ms.ToArray());
        Assert.Equal(w, bmp.Width);
        Assert.Equal(h, bmp.Height);
        var decoded = bmp.GetPixelSpan().ToArray();
        for (int i = 0; i < rgba.Length; i += 4)
        {
            Assert.Equal(rgba[i + 3], decoded[i + 3]);
            if (rgba[i + 3] == 0) continue; // color under fully transparent pixels is irrelevant
            for (int c = 0; c < 3; c++) Assert.Equal(rgba[i + c], decoded[i + c]);
        }
    }

    [Fact]
    public void OpaquePngIsWrittenWithoutAlpha()
    {
        var rgba = Image(40, 30);
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        using var ms = new MemoryStream();
        PngEncoder.Encode(ms, rgba, 40, 30);
        Assert.Equal(2, ms.ToArray()[25]); // IHDR color type 2 = RGB
        using var bmp = Decode(ms.ToArray());
        Assert.Equal(rgba, bmp.GetPixelSpan().ToArray());
    }

    [Theory]
    [InlineData(95)] // chroma at full resolution
    [InlineData(75)] // 4:2:0 chroma
    public void JpegDecodesCloseToTheSourceFlattenedOnTheMatte(int quality)
    {
        int w = 83, h = 47; // not multiples of the block size
        var rgba = Image(w, h);
        var options = new ExportOptions { Format = ExportFormat.Jpeg, Quality = quality, Matte = (0, 0, 255) };
        using var ms = new MemoryStream();
        ImageExporter.Encode(ms, (byte[])rgba.Clone(), w, h, options);
        var file = ms.ToArray();
        Assert.Equal([0xFF, 0xD8], file[..2]);
        Assert.Equal([0xFF, 0xD9], file[^2..]);
        using var bmp = Decode(file);
        Assert.Equal(w, bmp.Width);
        Assert.Equal(h, bmp.Height);

        var expected = (byte[])rgba.Clone();
        ImageExporter.Flatten(expected, options.Matte);
        var decoded = bmp.GetPixelSpan().ToArray();
        double err = 0;
        for (int i = 0; i < expected.Length; i += 4)
            for (int c = 0; c < 3; c++) err += Math.Abs(expected[i + c] - decoded[i + c]);
        double mean = err / (w * h * 3);
        Assert.True(mean < (quality >= 90 ? 2.5 : 5), $"mean error {mean:F2}");
        // The transparent strip became the matte color.
        int p = (20 * w + 3) * 4;
        Assert.InRange(decoded[p + 2], 230, 255);
        Assert.InRange(decoded[p], 0, 25);
    }

    [Fact]
    public void ExportWritesFilesThatDecodeAtDocumentSize()
    {
        var doc = new Document(64, 40, ColorMode.Rgb, 8);
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(30, 20, 8)).ToArray();
        Array.Fill(planes[0].Data, (byte)200);
        Array.Fill(planes[3].Data, (byte)255);
        doc.Root.Add(new PixelLayer { Bounds = new PixelRect(5, 5, 35, 25), Pixels = new Raster(ColorMode.Rgb, planes[..3], planes[3]) });
        string dir = Path.Combine(Path.GetTempPath(), $"strayta-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string png = Path.Combine(dir, "a.png"), jpg = Path.Combine(dir, "a.jpg");
            ImageExporter.Export(doc, png, new ExportOptions { Format = ExportFormat.Png });
            ImageExporter.Export(doc, jpg, new ExportOptions { Format = ExportFormat.Jpeg, Quality = 80 });
            using var a = Decode(File.ReadAllBytes(png));
            using var b = Decode(File.ReadAllBytes(jpg));
            Assert.Equal((64, 40), (a.Width, a.Height));
            Assert.Equal((64, 40), (b.Width, b.Height));
            Assert.Equal(0, a.GetPixel(0, 0).Alpha);
            Assert.Equal(200, a.GetPixel(10, 10).Red);
            Assert.InRange(b.GetPixel(0, 0).Red, 245, 255); // flattened on white
            Assert.Equal(2, Directory.GetFiles(dir).Length); // no temporary files left behind
            Assert.Equal(ExportFormat.Jpeg, ExportOptions.FormatFromPath("x.JPEG"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
