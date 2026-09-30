using SkiaSharp;
using Strayta.Core;
using Strayta.Imaging;
using Strayta.Rendering.Export;

namespace Strayta.Rendering.Tests;

/// <summary>GIF, 8-bit PNG and WebP export, resizing, canvas size and metadata.</summary>
public class ExportFormatTests
{
    static ExportFormatTests()
    {
        ExportCodecs.WebP = (output, image, options, icc) =>
            WebPWriter.Encode(output, image.Pixels, image.Width, image.Height, options.Quality, options.Lossless, icc);
        ExportCodecs.ConvertToSrgb = WebPWriter.ConvertToSrgb;
    }

    /// <summary>Few flat colors, a transparent corner and a half-transparent band.</summary>
    private static byte[] Flat(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (x < w / 2, y < h / 2) switch
                {
                    (true, true) => ((byte)0, (byte)0, (byte)0, (byte)0),
                    (true, false) => ((byte)255, (byte)0, (byte)0, (byte)255),
                    (false, true) => ((byte)0, (byte)128, (byte)255, (byte)255),
                    _ => ((byte)30, (byte)200, (byte)40, (byte)255),
                };
            }
        return rgba;
    }

    /// <summary>A smooth gradient with thousands of colors.</summary>
    private static byte[] Gradient(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                rgba[i] = (byte)(x * 255 / (w - 1));
                rgba[i + 1] = (byte)(y * 255 / (h - 1));
                rgba[i + 2] = (byte)((x + y) * 255 / (w + h - 2));
                rgba[i + 3] = 255;
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

    private static byte[] Encode(byte[] rgba, int w, int h, ExportOptions options)
    {
        using var ms = new MemoryStream();
        ImageExporter.Encode(ms, new RgbaImage(rgba, w, h), options, null);
        return ms.ToArray();
    }

    [Fact]
    public void Gif_keeps_flat_colors_exactly_and_marks_transparency()
    {
        int w = 40, h = 30;
        var rgba = Flat(w, h);
        var file = Encode(rgba, w, h, new ExportOptions { Format = ExportFormat.Gif });
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(file, 0, 6));
        using var bmp = Decode(file);
        var px = bmp.GetPixelSpan().ToArray();
        for (int i = 0; i < rgba.Length; i += 4)
        {
            Assert.Equal(rgba[i + 3] == 0 ? 0 : 255, px[i + 3]);
            if (rgba[i + 3] != 0)
                for (int c = 0; c < 3; c++) Assert.Equal(rgba[i + c], px[i + c]);
        }
    }

    [Fact]
    public void Gif_of_a_gradient_uses_a_close_256_color_palette()
    {
        int w = 256, h = 200;
        var rgba = Gradient(w, h);
        var indexed = ColorQuantizer.Quantize(rgba, w, h, 256);
        Assert.InRange(indexed.ColorCount, 200, 256);
        using var bmp = Decode(Encode(rgba, w, h, new ExportOptions { Format = ExportFormat.Gif }));
        var px = bmp.GetPixelSpan().ToArray();
        double error = 0;
        for (int i = 0; i < rgba.Length; i += 4)
            for (int c = 0; c < 3; c++) error += Math.Abs(rgba[i + c] - px[i + c]);
        Assert.True(error / (w * h * 3) < 6, $"mean error {error / (w * h * 3):F2}"); // median cut on a smooth ramp
        Assert.All(Enumerable.Range(0, w * h), i => Assert.Equal(255, px[i * 4 + 3]));
    }

    [Fact]
    public void Gif_lzw_survives_long_runs_and_table_resets()
    {
        // Noise fills the 4096-code table many times over; every pixel must decode to its index's color.
        int w = 300, h = 300;
        var rng = new Random(7);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int k = rng.Next(16);
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = ((byte)(k * 16), (byte)(255 - k * 16), (byte)(k * 7), (byte)255);
        }
        using var bmp = Decode(Encode(rgba, w, h, new ExportOptions { Format = ExportFormat.Gif }));
        Assert.Equal(rgba, bmp.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Gif_without_transparency_is_flattened_on_the_matte()
    {
        var rgba = Flat(20, 20);
        using var bmp = Decode(Encode(rgba, 20, 20, new ExportOptions { Format = ExportFormat.Gif, Transparency = false, Matte = (0, 0, 255) }));
        var p = bmp.GetPixel(0, 0);
        Assert.Equal((byte)255, p.Alpha);
        Assert.Equal((byte)255, p.Blue);
        Assert.Equal((byte)0, p.Red);
    }

    [Fact]
    public void Smaller_png_is_a_palette_image_with_alpha()
    {
        int w = 40, h = 30;
        var rgba = Flat(w, h);
        // A half-transparent patch: 8-bit PNG keeps partial alpha in its tRNS chunk.
        for (int i = 0; i < 20; i++) rgba[(w * (h - 1) + i) * 4 + 3] = 128;
        var file = Encode(rgba, w, h, new ExportOptions { Format = ExportFormat.Png, SmallerFile = true });
        Assert.Equal(3, file[25]); // IHDR color type 3: palette
        using var bmp = Decode(file);
        var px = bmp.GetPixelSpan().ToArray();
        for (int i = 0; i < rgba.Length; i += 4)
        {
            Assert.Equal(rgba[i + 3], px[i + 3]);
            if (rgba[i + 3] == 255)
                for (int c = 0; c < 3; c++) Assert.Equal(rgba[i + c], px[i + c]);
        }
    }

    [Fact]
    public void WebP_lossless_round_trips_exactly_and_lossy_is_close()
    {
        int w = 64, h = 48;
        var rgba = Gradient(w, h);
        for (int i = 0; i < 64; i++) rgba[i * 4 + 3] = 0; // a transparent first row
        var lossless = Encode((byte[])rgba.Clone(), w, h, new ExportOptions { Format = ExportFormat.WebP, Lossless = true });
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(lossless, 8, 4));
        using (var bmp = Decode(lossless))
        {
            var px = bmp.GetPixelSpan().ToArray();
            for (int i = 0; i < rgba.Length; i += 4)
            {
                Assert.Equal(rgba[i + 3], px[i + 3]);
                if (rgba[i + 3] > 0) for (int c = 0; c < 3; c++) Assert.Equal(rgba[i + c], px[i + c]);
            }
        }

        var lossy = Encode((byte[])rgba.Clone(), w, h, new ExportOptions { Format = ExportFormat.WebP, Quality = 90 });
        using var lossyBmp = Decode(lossy);
        var lp = lossyBmp.GetPixelSpan().ToArray();
        double error = 0;
        int n = 0;
        for (int i = w * 4; i < rgba.Length; i += 4, n++)
            for (int c = 0; c < 3; c++) error += Math.Abs(rgba[i + c] - lp[i + c]);
        Assert.True(error / (n * 3) < 4, $"mean error {error / (n * 3):F2}");
    }

    [Fact]
    public void Scale_size_and_canvas_are_applied_before_encoding()
    {
        var rgba = Flat(40, 30);
        var image = new RgbaImage(rgba, 40, 30);
        var half = ImageExporter.Prepare(image, new ExportOptions { Scale = 0.5 });
        Assert.Equal((20, 15), (half.Width, half.Height));
        // Area-averaged: the red quadrant stays pure red away from its edges.
        int i = (12 * 20 + 2) * 4;
        Assert.Equal([255, 0, 0, 255], half.Pixels[i..(i + 4)]);

        var wide = ImageExporter.Prepare(image, new ExportOptions { Width = 80 });
        Assert.Equal((80, 60), (wide.Width, wide.Height));

        var canvas = ImageExporter.Prepare(image, new ExportOptions { CanvasWidth = 60, CanvasHeight = 20 });
        Assert.Equal((60, 20), (canvas.Width, canvas.Height));
        Assert.Equal(0, canvas.Pixels[3]); // new area on the left is transparent
        Assert.Equal(255, canvas.Pixels[((19 * 60) + 30) * 4 + 3]); // the image's lower half, cropped to 20 rows
    }

    [Fact]
    public void Copyright_metadata_is_written_as_png_text_and_jpeg_comment()
    {
        var rgba = Flat(8, 8);
        var options = new ExportOptions { Metadata = ExportMetadata.CopyrightAndContact, Copyright = "© 2026 Someone", Author = "Someone" };
        var png = Encode((byte[])rgba.Clone(), 8, 8, options);
        var text = System.Text.Encoding.Latin1.GetString(png);
        Assert.Contains("tEXtCopyright\0© 2026 Someone", text);
        Assert.Contains("tEXtAuthor\0Someone", text);
        var jpeg = Encode((byte[])rgba.Clone(), 8, 8, options with { Format = ExportFormat.Jpeg });
        Assert.Contains("© 2026 Someone", System.Text.Encoding.UTF8.GetString(jpeg));
        Assert.DoesNotContain("tEXt", System.Text.Encoding.Latin1.GetString(Encode((byte[])rgba.Clone(), 8, 8, new ExportOptions())));
    }

    [Fact]
    public void Wide_gamut_pixels_are_converted_to_srgb()
    {
        // Display P3's pure red is outside sRGB: converted, green and blue go negative and clip, red stays at 255.
        const string profile = "/System/Library/ColorSync/Profiles/Display P3.icc";
        if (!File.Exists(profile)) Assert.Skip("Needs macOS's Display P3 profile.");
        var icc = File.ReadAllBytes(profile);
        var rgba = new byte[] { 0, 255, 0, 255, 128, 128, 128, 255 };
        Assert.True(WebPWriter.ConvertToSrgb(rgba, 2, 1, icc));
        Assert.True(rgba[0] < 40 && rgba[1] == 255, $"P3 green became {rgba[0]},{rgba[1]},{rgba[2]}");
        Assert.InRange(rgba[4], 126, 130); // gray stays gray
    }
}
