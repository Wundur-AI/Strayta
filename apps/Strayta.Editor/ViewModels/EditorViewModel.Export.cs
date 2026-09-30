using CommunityToolkit.Mvvm.Input;
using Strayta.Editor.Editing;
using Strayta.Rendering.Export;

namespace Strayta.Editor.ViewModels;

/// <summary>Settings of File › Export › Layers to Files.</summary>
public sealed record ExportLayersRequest(ExportOptions Options, ExportLayerScope Scope, bool TrimToContent, bool VisibleOnly, string Prefix);

/// <summary>The export dialogs, implemented by the main window (MainWindow.Export.cs).</summary>
public interface IExportDialogs
{
    /// <summary>Shows Export As; true when the person chose to export.</summary>
    Task<bool> ShowExportAsAsync(ExportAsViewModel model);

    Task<string?> PickFolderAsync(string title, string? startFolder);

    Task<string?> PickExportFileAsync(string title, string suggestedName, ExportFormat format, string? startFolder);

    Task<ExportLayersRequest?> AskExportLayersAsync(ExportLayersRequest current, bool hasSelection, bool hasArtboards);
}

// Edit › Free Transform, and File › Export (Export As, Quick Export, Layers to Files, Save a Copy) and Generate › Image Assets.
public sealed partial class EditorViewModel
{
    /// <summary>True while the active document has a Free Transform open (the options bar shows its values).</summary>
    public bool IsTransforming => ActiveDocument?.IsTransforming == true;

    /// <summary>⌘T: transform the selected layer or group; a notice explains when that is not possible.</summary>
    [RelayCommand]
    private void FreeTransform() => ActiveDocument?.BeginFreeTransform();

    /// <summary>Enter, double-click or the ✓ button.</summary>
    [RelayCommand]
    private Task CommitTransform() => ActiveDocument?.CommitTransformAsync() ?? Task.CompletedTask;

    /// <summary>Esc or the ✕ button.</summary>
    [RelayCommand]
    private void CancelTransform() => ActiveDocument?.CancelTransform();

    /// <summary>The last export settings, offered again next time (Save As PNG/JPEG uses them too).</summary>
    public ExportOptions ExportSettings { get; private set; } = ExportPreferences.Shared.ExportAs.ToOptions();

    private IExportDialogs? ExportDialogs => _dialogs as IExportDialogs;

    private (byte R, byte G, byte B) ExportMatteRgb => (BackgroundColor.R, BackgroundColor.G, BackgroundColor.B);

    /// <summary>File › Export › Export As (⌥⇧⌘W): the document, or its artboards.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task ExportAs() => RunExportAsAsync(selection: false);

    /// <summary>Layer › Export As: the selected layers or artboards.</summary>
    [RelayCommand]
    private Task ExportLayerAs() => RunExportAsAsync(selection: true);

    /// <summary>Builds the Export As model for the active document with the remembered settings.</summary>
    public ExportAsViewModel? CreateExportAs(bool selection)
    {
        if (ActiveDocument is not { } doc) return null;
        var saved = ExportPreferences.Shared.ExportAs;
        var scales = (saved.Scales ?? [(1, "")]).Select(s => ExportScaleRow.Of(s.Scale, s.Suffix));
        return new ExportAsViewModel(doc.Model, doc.ExportItems(selection), ExportSettings, scales, ExportMatteRgb);
    }

    private async Task RunExportAsAsync(bool selection)
    {
        if (ActiveDocument is not { } doc || ExportDialogs is not { } dialogs) return;
        await doc.SettleForExportAsync();
        if (CreateExportAs(selection) is not { } model) return;
        if (!await dialogs.ShowExportAsAsync(model)) return;
        var options = model.Options;
        ExportSettings = options with { Scale = 1, CanvasWidth = null, CanvasHeight = null, Copyright = null, Author = null };
        var prefs = ExportPreferences.Shared;
        prefs.Remember(exportAs: ExportPreferences.Saved.From(ExportSettings, model.Scales.Select(s => (s.Scale, s.Suffix))));

        var included = model.Items.Where(i => i.Include).ToList();
        if (included.Count == 0) return;
        string? folder;
        string? single = null;
        if (included.Count == 1)
        {
            // One item: a save dialog with its name (the scale suffixes are added to further sizes).
            string suggested = included[0].Name + model.Scales[0].Suffix + ExportOptions.ExtensionOf(options.Format);
            if (await dialogs.PickExportFileAsync("Export As", suggested, options.Format, prefs.LastFolder) is not { } path) return;
            folder = Path.GetDirectoryName(path);
            single = Path.GetFileName(path);
            if (ExportOptions.FormatFromPath(path) is { } picked && picked != options.Format) model.FormatIndex = (int)picked;
            // The picked name stands for the first size; strip that size's suffix so the others get theirs.
            string stem = Path.GetFileNameWithoutExtension(single);
            string first = model.Scales[0].Suffix;
            if (first.Length > 0 && stem.EndsWith(first, StringComparison.Ordinal)) single = stem[..^first.Length] + Path.GetExtension(single);
        }
        else folder = await dialogs.PickFolderAsync("Export As: choose a folder", prefs.LastFolder);
        if (folder is null) return;
        prefs.Remember(folder: folder);
        try
        {
            doc.IsBusy = true;
            var files = await model.ExportAllAsync(folder, single);
            doc.Notice = files.Count == 1 ? $"Exported {Path.GetFileName(files[0])}." : $"Exported {files.Count} files to {Path.GetFileName(folder)}.";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not export", ex.Message);
        }
        finally
        {
            doc.IsBusy = false;
        }
    }

    /// <summary>
    /// File › Export › Quick Export as PNG (⇧⌘'): no dialog, the remembered Quick Export settings, the document (or its
    /// artboards) into a file or folder asked for each time, starting in the last one.
    /// </summary>
    [RelayCommand]
    private async Task QuickExportPng()
    {
        if (ActiveDocument is not { } doc || ExportDialogs is not { } dialogs) return;
        await doc.SettleForExportAsync();
        var prefs = ExportPreferences.Shared;
        var options = prefs.QuickExport.ToOptions() with { Format = ExportFormat.Png };
        var items = doc.ExportItems(selection: false);
        string? folder, single = null;
        if (items.Count == 1)
        {
            if (await dialogs.PickExportFileAsync("Quick Export as PNG", items[0].Name + ".png", ExportFormat.Png, prefs.LastFolder) is not { } path) return;
            (folder, single) = (Path.GetDirectoryName(path), Path.GetFileName(path));
        }
        else folder = await dialogs.PickFolderAsync("Quick Export as PNG: choose a folder", prefs.LastFolder);
        if (folder is null) return;
        prefs.Remember(folder: folder);
        await QuickExportToAsync(doc, folder, single, options);
    }

    /// <summary>Writes the document's Quick Export files into <paramref name="folder"/>; returns their paths.</summary>
    public async Task<IReadOnlyList<string>> QuickExportToAsync(DocumentViewModel doc, string folder, string? singleFileName, ExportOptions options)
    {
        var model = new ExportAsViewModel(doc.Model, doc.ExportItems(selection: false), options, [ExportScaleRow.Of(1, "")], ExportMatteRgb);
        try
        {
            doc.IsBusy = true;
            var files = await model.ExportAllAsync(folder, singleFileName);
            doc.Notice = files.Count == 1 ? $"Exported {Path.GetFileName(files[0])}." : $"Exported {files.Count} PNG files to {Path.GetFileName(folder)}.";
            return files;
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not export", ex.Message);
            return [];
        }
        finally
        {
            doc.IsBusy = false;
        }
    }

    private ExportLayersRequest _lastLayersRequest = new(new ExportOptions(), ExportLayerScope.Selected, true, true, "");

    /// <summary>File › Export › Layers to Files…: each selected layer, top-level layer or artboard to its own file.</summary>
    [RelayCommand]
    private async Task ExportLayersToFiles()
    {
        if (ActiveDocument is not { } doc || ExportDialogs is not { } dialogs) return;
        bool hasSelection = doc.SelectedNodesForExport().Count > 0, hasArtboards = Strayta.Core.Artboards.Any(doc.Model);
        var current = _lastLayersRequest with
        {
            Scope = hasSelection ? ExportLayerScope.Selected : hasArtboards ? ExportLayerScope.Artboards : ExportLayerScope.TopLevel,
        };
        if (await dialogs.AskExportLayersAsync(current, hasSelection, hasArtboards) is not { } request) return;
        _lastLayersRequest = request;
        var prefs = ExportPreferences.Shared;
        if (await dialogs.PickFolderAsync("Layers to Files: choose a folder", prefs.LastFolder) is not { } folder) return;
        prefs.Remember(folder: folder);
        try
        {
            var files = await doc.ExportLayersAsync(folder, request.Options, request.Scope, request.TrimToContent, request.VisibleOnly, request.Prefix);
            doc.Notice = $"Exported {files.Count} file{(files.Count == 1 ? "" : "s")} to {Path.GetFileName(folder)}.";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not export", ex.Message);
        }
    }

    /// <summary>File › Generate › Image Assets: turns asset generation on or off for the document (on: generates now).</summary>
    [RelayCommand]
    private async Task ToggleImageAssets()
    {
        if (ActiveDocument is not { } doc) return;
        doc.GeneratesImageAssets = !doc.GeneratesImageAssets;
        DocumentChanged?.Invoke(); // the menu's check mark
        if (!doc.GeneratesImageAssets)
        {
            doc.Notice = "Image assets are no longer generated.";
            return;
        }
        if (doc.FilePath is null)
        {
            doc.Notice = "Image assets will be generated next to the file once it is saved.";
            return;
        }
        try
        {
            await doc.GenerateImageAssetsAsync();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not generate image assets", ex.Message);
        }
    }

    /// <summary>File › Export › Save a Copy…: a flattened copy as PNG, JPEG, GIF or WebP; the document keeps its file.</summary>
    [RelayCommand]
    private async Task SaveACopy()
    {
        if (ActiveDocument is not { } doc || ExportDialogs is not { } dialogs) return;
        var prefs = ExportPreferences.Shared;
        string name = Path.GetFileNameWithoutExtension(doc.Title) + " copy" + ExportOptions.ExtensionOf(ExportSettings.Format);
        if (await dialogs.PickExportFileAsync("Save a Copy", name, ExportSettings.Format, prefs.LastFolder) is not { } path) return;
        prefs.Remember(folder: Path.GetDirectoryName(path));
        var options = ExportSettings with { Format = ExportOptions.FormatFromPath(path) ?? ExportFormat.Png, Scale = 1, Width = null, Height = null };
        try
        {
            await doc.ExportAsync(path, options);
            doc.Notice = $"Saved a copy as {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not save a copy", ex.Message);
        }
    }
}
