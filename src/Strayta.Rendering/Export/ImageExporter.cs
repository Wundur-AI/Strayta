using Strayta.Core;

namespace Strayta.Rendering.Export;

public enum ExportFormat
{
    Png,
    Jpeg,
}

/// <summary>Settings for File › Export As.</summary>
public sealed record ExportOptions
{
    public ExportFormat Format { get; init; } = ExportFormat.Png;

    /// <summary>JPEG quality, 1–100.</summary>
    public int Quality { get; init; } = 90;

    /// <summary>PNG only: keep transparency. When false (and always for JPEG) the image is flattened onto <see cref="Matte"/>.</summary>
    public bool Transparency { get; init; } = true;

    /// <summary>Color that transparent areas are flattened onto.</summary>
    public (byte R, byte G, byte B) Matte { get; init; } = (255, 255, 255);

    /// <summary>The format implied by a file name's extension, or null if it is not an export format.</summary>
    public static ExportFormat? FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ExportFormat.Png,
        ".jpg" or ".jpeg" or ".jpe" => ExportFormat.Jpeg,
        _ => null,
    };
}

/// <summary>Renders a document at full resolution and writes it as PNG or JPEG with Strayta's own encoders.</summary>
public static class ImageExporter
{
    /// <summary>Renders <paramref name="document"/> and writes it to <paramref name="path"/>, replacing any existing file only once the new one is complete.</summary>
    public static void Export(Document document, string path, ExportOptions options, CancellationToken cancel = default)
    {
        using var renderer = new CpuRenderer();
        var rgba = renderer.Render(document, new RenderOptions { Cancellation = cancel }).ToRgba8(cancel);
        cancel.ThrowIfCancellationRequested();
        // The pixels are the document's own RGB values, so its profile describes them; other modes are converted to sRGB.
        var icc = document.ColorMode == ColorMode.Rgb && document.BitDepth != 32 ? document.IccProfile : null;

        string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = File.Create(temp)) Encode(file, rgba, document.Width, document.Height, options, icc);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Encodes straight-alpha RGBA pixels; flattens them in place first when the format or options need it.</summary>
    public static void Encode(Stream output, byte[] rgba, int width, int height, ExportOptions options, byte[]? iccProfile = null)
    {
        bool flatten = options.Format == ExportFormat.Jpeg || !options.Transparency;
        if (flatten) Flatten(rgba, options.Matte);
        if (options.Format == ExportFormat.Jpeg) JpegEncoder.Encode(output, rgba, width, height, options.Quality, iccProfile);
        else PngEncoder.Encode(output, rgba, width, height, keepAlpha: !flatten, iccProfile);
    }

    /// <summary>Composites RGBA over an opaque color in place (in the pixels' own gamma-encoded space, as Photoshop flattens).</summary>
    public static void Flatten(byte[] rgba, (byte R, byte G, byte B) matte)
    {
        Parallel.For(0, (int)(rgba.LongLength / 4 / 4096) + 1, chunk =>
        {
            long end = Math.Min(rgba.LongLength, (chunk + 1L) * 4096 * 4);
            for (long i = chunk * 4096L * 4; i < end; i += 4)
            {
                int a = rgba[i + 3];
                if (a == 255) continue;
                rgba[i] = Blend(rgba[i], matte.R, a);
                rgba[i + 1] = Blend(rgba[i + 1], matte.G, a);
                rgba[i + 2] = Blend(rgba[i + 2], matte.B, a);
                rgba[i + 3] = 255;
            }
        });

        static byte Blend(int c, int m, int a) => (byte)((c * a + m * (255 - a) + 127) / 255);
    }
}
