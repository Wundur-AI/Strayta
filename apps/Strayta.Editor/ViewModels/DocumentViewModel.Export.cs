using Strayta.Core;
using Strayta.Rendering.Export;

namespace Strayta.Editor.ViewModels;

/// <summary>Which layers File › Export › Layers to Files writes.</summary>
public enum ExportLayerScope
{
    /// <summary>The selected layers, groups or artboards.</summary>
    Selected,
    /// <summary>Every top-level layer and group (artboards included).</summary>
    TopLevel,
    /// <summary>Every artboard.</summary>
    Artboards,
}

public sealed partial class DocumentViewModel
{
    /// <summary>
    /// File › Export As: renders the document at full resolution and writes a PNG or JPEG. An open Free
    /// Transform is applied first, so the file shows what is on screen.
    /// </summary>
    public async Task ExportAsync(string path, ExportOptions options)
    {
        await SettleForExportAsync();
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

    /// <summary>Applies an open Free Transform and commits typing, so exports show what is on screen.</summary>
    public async Task SettleForExportAsync()
    {
        if (IsTransforming) await CommitTransformAsync();
        CommitType(); // DocumentViewModel.TypeTool.cs
    }

    /// <summary>
    /// The layers an export of "the selection" means: every selected layer, in layer order (top first, as the Layers
    /// panel lists them), without layers already inside a selected group or artboard.
    /// </summary>
    public IReadOnlyList<LayerNode> SelectedNodesForExport()
    {
        var chosen = SelectedNodes.Count > 0 ? SelectedNodes : SelectedLayer?.Node is { } node ? [node] : [];
        var set = chosen.ToHashSet();
        var order = Model.Root.Descendants().Reverse().ToList(); // Descendants runs bottom to top
        return chosen.Where(n => !Ancestors(n).Any(set.Contains)).OrderBy(order.IndexOf).ToList();

        static IEnumerable<LayerNode> Ancestors(LayerNode n)
        {
            for (var p = n.Parent; p is not null; p = p.Parent) yield return p;
        }
    }

    /// <summary>
    /// What Export As lists: the selected layers or artboards (<paramref name="selection"/>), or else the document —
    /// as its artboards when it has any, the way Photoshop exports an artboard document.
    /// </summary>
    public IReadOnlyList<ExportItem> ExportItems(bool selection)
    {
        if (selection && SelectedNodesForExport() is { Count: > 0 } nodes)
            return nodes.Select(n => new ExportItem(ItemName(n.Name), ExportTarget.For(n))).ToList();
        if (Artboards.Any(Model))
            return Artboards.Of(Model).Select(g => new ExportItem(ItemName(g.Name), ExportTarget.For(g))).ToList();
        return [new ExportItem(System.IO.Path.GetFileNameWithoutExtension(Title), ExportTarget.Document)];
    }

    /// <summary>A file name from a layer name: an asset name's extension and modifiers are dropped ("200% icon.png" → "icon").</summary>
    public static string ItemName(string layerName)
    {
        if (ImageAssetNames.Parse(layerName) is [var spec, ..])
            return System.IO.Path.GetFileNameWithoutExtension(spec.FileName.Replace('/', '_'));
        return ImageAssetNames.Sanitize(layerName) is { Length: > 0 } name ? name : "Layer";
    }

    /// <summary>The layers <paramref name="scope"/> covers, top to bottom.</summary>
    public IReadOnlyList<LayerNode> LayersFor(ExportLayerScope scope, bool visibleOnly) => (scope switch
    {
        ExportLayerScope.Selected => SelectedNodesForExport(),
        ExportLayerScope.Artboards => Artboards.Of(Model).Cast<LayerNode>().ToList(),
        _ => Model.Root.Children.Reverse().ToList(),
    }).Where(n => !visibleOnly || n.Visible).ToList();

    /// <summary>
    /// File › Export › Layers to Files: each layer, group or artboard of <paramref name="scope"/> to its own file in
    /// <paramref name="folder"/>, named prefix + layer name (made safe for file names; repeated names get a number).
    /// Layers are trimmed to their pixels or kept at the canvas size; artboards are always their own size.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExportLayersAsync(string folder, ExportOptions options, ExportLayerScope scope,
        bool trimToContent, bool visibleOnly, string prefix, CancellationToken cancel = default)
    {
        await SettleForExportAsync();
        var doc = Model;
        var nodes = LayersFor(scope, visibleOnly);
        var profile = ExportAsViewModel.Profile(doc);
        var written = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IsBusy = true;
        try
        {
            foreach (var node in nodes)
            {
                cancel.ThrowIfCancellationRequested();
                string baseName = ImageAssetNames.Sanitize(prefix + ItemName(node.Name));
                if (baseName.Length == 0) baseName = "Layer";
                string name = baseName;
                for (int n = 2; !used.Add(name); n++) name = $"{baseName} {n}";
                string path = System.IO.Path.Combine(folder, name + ExportOptions.ExtensionOf(options.Format));
                bool ok = await Task.Run(() =>
                {
                    if (ExportRenderer.TryRender(doc, ExportTarget.For(node, trimToContent), cancel) is not { } image) return false;
                    ImageExporter.WriteFile(path, image, options, profile, cancel);
                    return true;
                }, cancel);
                if (ok) written.Add(path);
            }
        }
        finally
        {
            IsBusy = false;
        }
        return written;
    }

    // ---- Generate › Image Assets ------------------------------------------------------------------

    /// <summary>File › Generate › Image Assets is on for this document: assets are written now and after every save.</summary>
    public bool GeneratesImageAssets { get; set; }

    /// <summary>Where the assets go: "&lt;name&gt;-assets" next to the file, or null for a document never saved.</summary>
    public string? AssetsFolder => FilePath is { } path ? ImageAssetGenerator.FolderFor(path) : null;

    /// <summary>Writes every layer named as an asset into <see cref="AssetsFolder"/> (or <paramref name="folder"/>).</summary>
    public async Task<IReadOnlyList<AssetResult>> GenerateImageAssetsAsync(string? folder = null, CancellationToken cancel = default)
    {
        folder ??= AssetsFolder;
        if (folder is null) return [];
        await SettleForExportAsync();
        var doc = Model;
        IsBusy = true;
        try
        {
            var results = await Task.Run(() => ImageAssetGenerator.Generate(doc, folder, cancel: cancel), cancel);
            int ok = results.Count(r => r.Error is null);
            var failed = results.FirstOrDefault(r => r.Error is not null);
            Notice = results.Count == 0
                ? "No layers are named as image assets (e.g. \"icon.png\", \"200% hero@2x.png\")."
                : $"Generated {ok} image asset{(ok == 1 ? "" : "s")} in {System.IO.Path.GetFileName(folder)}"
                  + (failed is null ? "." : $"; \"{failed.LayerName}\": {failed.Error}");
            return results;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>After a save: regenerates the assets when Generate › Image Assets is on (errors only show as a notice).</summary>
    private async Task AfterSaveGenerateAssetsAsync()
    {
        if (!GeneratesImageAssets) return;
        try
        {
            await GenerateImageAssetsAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Notice = $"Could not generate image assets: {ex.Message}";
        }
    }
}
