using Strayta.Rendering.Export;

namespace Strayta.Editor.ViewModels;

public sealed partial class DocumentViewModel
{
    /// <summary>
    /// File › Export As: renders the document at full resolution and writes a PNG or JPEG. An open Free
    /// Transform is applied first, so the file shows what is on screen.
    /// </summary>
    public async Task ExportAsync(string path, ExportOptions options)
    {
        if (IsTransforming) await CommitTransformAsync();
        IsBusy = true;
        try
        {
            var doc = Model;
            await Task.Run(() => ImageExporter.Export(doc, path, options));
        }
        finally
        {
            IsBusy = false;
        }
    }
}
