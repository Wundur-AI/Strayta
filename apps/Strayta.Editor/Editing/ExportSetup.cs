using System.Runtime.CompilerServices;
using Strayta.Imaging;
using Strayta.Rendering.Export;

namespace Strayta.Editor.Editing;

/// <summary>
/// Gives the renderer's exporter what it cannot do on its own (it depends only on Core): WebP encoding and color
/// conversion to sRGB, both from Strayta.Imaging (SkiaSharp). Runs once when the editor loads.
/// </summary>
internal static class ExportSetup
{
#pragma warning disable CA2255 // the editor is an application, not a library
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install()
    {
        ExportCodecs.WebP = (output, image, options, icc) =>
            WebPWriter.Encode(output, image.Pixels, image.Width, image.Height, options.Quality, options.Lossless, icc);
        ExportCodecs.ConvertToSrgb = WebPWriter.ConvertToSrgb;
    }
}
