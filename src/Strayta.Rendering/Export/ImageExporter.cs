using Strayta.Core;

namespace Strayta.Rendering.Export;

public enum ExportFormat
{
    Png,
    Jpeg,
    Gif,
    WebP,
}

/// <summary>What Export As writes besides the pixels.</summary>
public enum ExportMetadata
{
    None,
    /// <summary>The document's copyright notice and author (PNG text chunks, a JPEG or GIF comment).</summary>
    CopyrightAndContact,
}

/// <summary>Settings for File › Export As and the other exports.</summary>
public sealed record ExportOptions
{
    public ExportFormat Format { get; init; } = ExportFormat.Png;

    /// <summary>JPEG and WebP quality, 1–100.</summary>
    public int Quality { get; init; } = 90;

    /// <summary>PNG, GIF and WebP: keep transparency. When false (and always for JPEG) the image is flattened onto <see cref="Matte"/>.</summary>
    public bool Transparency { get; init; } = true;

    /// <summary>Color that transparent areas are flattened onto (GIF: what partly transparent edges are blended with).</summary>
    public (byte R, byte G, byte B) Matte { get; init; } = (255, 255, 255);

    /// <summary>PNG: an 8-bit palette image ("Smaller File"), reduced with median cut.</summary>
    public bool SmallerFile { get; init; }

    /// <summary>WebP: lossless instead of lossy at <see cref="Quality"/>.</summary>
    public bool Lossless { get; init; }

    /// <summary>Image size as a factor of the source (1 = 100%). Ignored when <see cref="Width"/> or <see cref="Height"/> is set.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>Output width in pixels; with only one of width and height set, the other keeps the proportions.</summary>
    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>
    /// Canvas size after scaling: the image is centered on a canvas of this size, cropped where it is smaller and
    /// transparent (or matte) where it is larger. Null keeps the image's own size.
    /// </summary>
    public int? CanvasWidth { get; init; }

    public int? CanvasHeight { get; init; }

    /// <summary>
    /// Convert the pixels from the document's color profile to sRGB (as the web expects). When no converter is
    /// installed (<see cref="ExportCodecs.ConvertToSrgb"/>) the document's profile is embedded instead.
    /// </summary>
    public bool ConvertToSrgb { get; init; } = true;

    public ExportMetadata Metadata { get; init; } = ExportMetadata.None;

    /// <summary>The copyright notice written with <see cref="ExportMetadata.CopyrightAndContact"/>.</summary>
    public string? Copyright { get; init; }

    /// <summary>The author or contact written with <see cref="ExportMetadata.CopyrightAndContact"/>.</summary>
    public string? Author { get; init; }

    /// <summary>The format implied by a file name's extension, or null if it is not an export format.</summary>
    public static ExportFormat? FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ExportFormat.Png,
        ".jpg" or ".jpeg" or ".jpe" => ExportFormat.Jpeg,
        ".gif" => ExportFormat.Gif,
        ".webp" => ExportFormat.WebP,
        _ => null,
    };

    /// <summary>The usual file extension, with the dot.</summary>
    public static string ExtensionOf(ExportFormat format) => format switch
    {
        ExportFormat.Jpeg => ".jpg",
        ExportFormat.Gif => ".gif",
        ExportFormat.WebP => ".webp",
        _ => ".png",
    };

    /// <summary>The output size for a source of <paramref name="width"/>×<paramref name="height"/> pixels.</summary>
    public (int Width, int Height) OutputSize(int width, int height)
    {
        if (Width is { } w && Height is { } h) return (Math.Max(1, w), Math.Max(1, h));
        if (Width is { } w2) return (Math.Max(1, w2), Math.Max(1, (int)Math.Round(height * (double)w2 / width)));
        if (Height is { } h2) return (Math.Max(1, (int)Math.Round(width * (double)h2 / height)), Math.Max(1, h2));
        return (Math.Max(1, (int)Math.Round(width * Scale)), Math.Max(1, (int)Math.Round(height * Scale)));
    }
}

/// <summary>A straight-alpha RGBA8 image.</summary>
public sealed record RgbaImage(byte[] Pixels, int Width, int Height);

/// <summary>An encoder installed from outside the renderer (WebP comes from SkiaSharp via Strayta.Imaging).</summary>
public delegate void ImageEncoder(Stream output, RgbaImage image, ExportOptions options, byte[]? iccProfile);

/// <summary>
/// Hooks for what the renderer, which depends only on Core, cannot do itself. <c>Strayta.Imaging.ExportSupport.Install()</c>
/// fills them in; without them WebP export is unavailable and colors are kept in the document's profile.
/// </summary>
public static class ExportCodecs
{
    public static ImageEncoder? WebP { get; set; }

    /// <summary>Converts straight-alpha RGBA8 pixels from the given ICC profile to sRGB in place; false if it cannot.</summary>
    public static Func<byte[], int, int, byte[], bool>? ConvertToSrgb { get; set; }

    public static bool Supports(ExportFormat format) => format != ExportFormat.WebP || WebP is not null;
}

/// <summary>Renders a document at full resolution and writes it as PNG, JPEG, GIF or WebP.</summary>
public static class ImageExporter
{
    /// <summary>Renders <paramref name="document"/> and writes it to <paramref name="path"/>, replacing any existing file only once the new one is complete.</summary>
    public static void Export(Document document, string path, ExportOptions options, CancellationToken cancel = default)
    {
        var image = ExportRenderer.Render(document, ExportTarget.Document, cancel);
        WriteFile(path, image, options, document.ColorMode == ColorMode.Rgb && document.BitDepth != 32 ? document.IccProfile : null, cancel);
    }

    /// <summary>Scales, places on its canvas, converts and encodes <paramref name="image"/> into a file, atomically.</summary>
    /// <param name="documentProfile">The ICC profile the pixels are in (null: sRGB).</param>
    public static void WriteFile(string path, RgbaImage image, ExportOptions options, byte[]? documentProfile, CancellationToken cancel = default)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = File.Create(temp)) Encode(file, image, options, documentProfile, cancel);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// The whole export pipeline for one image: resize (bicubic, area-averaged when reducing), canvas size, color
    /// conversion, matte, then the encoder. <paramref name="image"/> is not changed.
    /// </summary>
    public static void Encode(Stream output, RgbaImage image, ExportOptions options, byte[]? documentProfile, CancellationToken cancel = default)
    {
        var prepared = Prepare(image, options, cancel);
        byte[]? icc = documentProfile;
        if (options.ConvertToSrgb && documentProfile is not null && ExportCodecs.ConvertToSrgb is { } convert
            && convert(prepared.Pixels, prepared.Width, prepared.Height, documentProfile))
            icc = null; // now sRGB, which the encoders mark (PNG's sRGB chunk) or assume for untagged files
        // Unconverted pixels always keep their profile, so their colors are right wherever they are shown.
        Encode(output, prepared.Pixels, prepared.Width, prepared.Height, options, icc);
    }

    /// <summary>Resizes and places the image on its canvas as <paramref name="options"/> ask; a copy.</summary>
    public static RgbaImage Prepare(RgbaImage image, ExportOptions options, CancellationToken cancel = default)
    {
        var (w, h) = options.OutputSize(image.Width, image.Height);
        var pixels = w == image.Width && h == image.Height ? (byte[])image.Pixels.Clone() : RgbaResize.Resize(image.Pixels, image.Width, image.Height, w, h, cancel);
        var result = new RgbaImage(pixels, w, h);
        if (options.CanvasWidth is { } cw && options.CanvasHeight is { } ch && (cw != w || ch != h))
            result = Recanvas(result, Math.Max(1, cw), Math.Max(1, ch));
        return result;
    }

    /// <summary>Centers the image on a transparent canvas of the given size (cropping where it is smaller).</summary>
    public static RgbaImage Recanvas(RgbaImage image, int width, int height)
    {
        var pixels = new byte[(long)width * height * 4];
        int ox = (width - image.Width) / 2, oy = (height - image.Height) / 2;
        for (int y = 0; y < height; y++)
        {
            int sy = y - oy;
            if (sy < 0 || sy >= image.Height) continue;
            int x0 = Math.Max(0, ox), x1 = Math.Min(width, ox + image.Width);
            if (x1 <= x0) continue;
            Array.Copy(image.Pixels, ((long)sy * image.Width + (x0 - ox)) * 4, pixels, ((long)y * width + x0) * 4, (x1 - x0) * 4);
        }
        return new RgbaImage(pixels, width, height);
    }

    /// <summary>Encodes straight-alpha RGBA pixels; flattens them in place first when the format or options need it.</summary>
    public static void Encode(Stream output, byte[] rgba, int width, int height, ExportOptions options, byte[]? iccProfile = null)
    {
        bool flatten = options.Format == ExportFormat.Jpeg || !options.Transparency;
        if (flatten) Flatten(rgba, options.Matte);
        string? comment = Comment(options);
        switch (options.Format)
        {
            case ExportFormat.Jpeg:
                JpegEncoder.Encode(output, rgba, width, height, options.Quality, iccProfile, comment);
                break;
            case ExportFormat.Gif:
                // GIF has one transparent color: partly transparent edges are blended onto the matte, the rest stays clear.
                if (!flatten) FlattenPartial(rgba, options.Matte);
                GifEncoder.Encode(output, rgba, width, height, transparency: !flatten, comment);
                break;
            case ExportFormat.WebP:
                if (ExportCodecs.WebP is not { } webp) throw new NotSupportedException("WebP export needs Strayta.Imaging (ExportSupport.Install).");
                webp(output, new RgbaImage(rgba, width, height), options, iccProfile);
                break;
            default:
                var text = TextChunks(options);
                if (options.SmallerFile) PngEncoder.EncodeIndexed(output, ColorQuantizer.Quantize(rgba, width, height, 256, keepAlpha: !flatten), iccProfile, text);
                else PngEncoder.Encode(output, rgba, width, height, keepAlpha: !flatten, iccProfile, text);
                break;
        }
    }

    private static string? Comment(ExportOptions o) =>
        o.Metadata != ExportMetadata.CopyrightAndContact ? null
        : string.Join("; ", new[] { o.Copyright, o.Author is { Length: > 0 } a ? $"Author: {a}" : null }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } c ? c : null;

    private static List<(string, string)>? TextChunks(ExportOptions o)
    {
        if (o.Metadata != ExportMetadata.CopyrightAndContact) return null;
        var list = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(o.Copyright)) list.Add(("Copyright", o.Copyright));
        if (!string.IsNullOrWhiteSpace(o.Author)) list.Add(("Author", o.Author));
        return list;
    }

    /// <summary>Composites RGBA over an opaque color in place (in the pixels' own gamma-encoded space, as Photoshop flattens).</summary>
    public static void Flatten(byte[] rgba, (byte R, byte G, byte B) matte) => Blend(rgba, matte, partialOnly: false);

    /// <summary>Blends only partly transparent pixels onto the matte: at least half opaque become opaque, the rest clear.</summary>
    public static void FlattenPartial(byte[] rgba, (byte R, byte G, byte B) matte) => Blend(rgba, matte, partialOnly: true);

    private static void Blend(byte[] rgba, (byte R, byte G, byte B) matte, bool partialOnly)
    {
        Parallel.For(0, (int)(rgba.LongLength / 4 / 4096) + 1, chunk =>
        {
            long end = Math.Min(rgba.LongLength, (chunk + 1L) * 4096 * 4);
            for (long i = chunk * 4096L * 4; i < end; i += 4)
            {
                int a = rgba[i + 3];
                if (a == 255) continue;
                if (partialOnly && a < 128)
                {
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = rgba[i + 3] = 0;
                    continue;
                }
                rgba[i] = Mix(rgba[i], matte.R, a);
                rgba[i + 1] = Mix(rgba[i + 1], matte.G, a);
                rgba[i + 2] = Mix(rgba[i + 2], matte.B, a);
                rgba[i + 3] = 255;
            }
        });

        static byte Mix(int c, int m, int a) => (byte)((c * a + m * (255 - a) + 127) / 255);
    }
}
