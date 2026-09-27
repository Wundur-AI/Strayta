using Strayta.Core;
using Strayta.Imaging;
using Strayta.Rendering.Export;

namespace Strayta.Editor.ViewModels;

// Documents opened from standard images (PNG, JPEG, ...) and saving back to them.
public sealed partial class DocumentViewModel
{
    /// <summary>The image file this document was opened from, or null for PSDs and new documents.</summary>
    public ImageSource? ImageSource => Model.SourceData as ImageSource;

    /// <summary>JPEG quality chosen for this document the first time it was saved as JPEG (like Photoshop's JPEG Options).</summary>
    public int? JpegQuality { get; set; }

    /// <summary>
    /// True when the document is still a single plain layer covering the canvas, so a PNG or JPEG holds all
    /// of it. Anything more (layers, masks, adjustments, effects, blend modes) needs a PSD to keep it.
    /// </summary>
    public bool IsFlatImage =>
        Model.Root.Children is [PixelLayer { Visible: true, Pixels: not null, Mask: null, Effects: null, Clipped: false } layer]
        && layer.Opacity >= 1f && layer.FillOpacity >= 1f && layer.BlendMode == BlendMode.Normal
        && layer.Bounds == Model.Bounds;

    /// <summary>
    /// Writes the document as PNG or JPEG. A flat document's PNG is written straight from its pixels (exact
    /// values, 16-bit kept); anything else goes through the full-resolution render.
    /// </summary>
    /// <param name="becomesFile">True when the document is now this file (Save, or Save As of a flat image);
    /// false when this was a flattened copy and the document keeps its layers and its current file.</param>
    public async Task SaveImageAsync(string path, ExportOptions options, bool becomesFile)
    {
        if (IsTransforming) await CommitTransformAsync();
        IsBusy = true;
        try
        {
            var doc = Model;
            bool direct = options.Format == ExportFormat.Png && IsFlatImage;
            await Task.Run(() =>
            {
                if (!direct)
                {
                    ImageExporter.Export(doc, path, options);
                    return;
                }
                var layer = (PixelLayer)doc.Root.Children[0];
                string temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!,
                    $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var file = File.Create(temp)) PngWriter.Write(file, layer.Pixels!, doc.IccProfile);
                    File.Move(temp, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            });

            if (!becomesFile) return;
            FilePath = path;
            Title = System.IO.Path.GetFileName(path);
            Model.SourceData = new ImageSource(path, options.Format == ExportFormat.Jpeg ? SkiaSharp.SKEncodedImageFormat.Jpeg : SkiaSharp.SKEncodedImageFormat.Png);
            _undo.MarkSaved();
            IsModified = false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
