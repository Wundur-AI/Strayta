using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Psd;
using Strayta.Rendering.Filters;

namespace Strayta.Editor.ViewModels;

// Layer › Smart Objects. Edit Contents opens the content as a document of its own tab ("Layer.psb"); saving that tab
// (⌘S) writes it back into the parent and redraws the smart object. Linked smart objects open their file, and every
// open document watches its linked files so a change on disk redraws them.
public sealed partial class EditorViewModel
{
    /// <summary>Picks the file for Replace Contents / Relink (the open dialog by default; scripted in the self-test).</summary>
    public Func<Task<string?>>? PickSmartObjectFile { get; set; }

    /// <summary>Picks where Export Contents writes (the save dialog by default).</summary>
    public Func<string, Task<string?>>? PickSmartExportFile { get; set; }

    private Task<string?> PickSmartFile() => PickSmartObjectFile?.Invoke() ?? _dialogs.PickFileToOpenAsync();

    /// <summary>Layer › Smart Objects › Edit Contents (also a double-click on a smart object's thumbnail).</summary>
    [RelayCommand]
    public async Task EditContents()
    {
        if (ActiveDocument is not { } doc || doc.OpenSmartContent() is not { } opened) return;
        if (opened.LinkedPath is { } linked)
        {
            await OpenAsync(linked);
            WatchLinkedFiles(doc);
            return;
        }
        // One tab per content: a second Edit Contents brings the open one forward.
        var existing = Factory.OpenDocuments().FirstOrDefault(d => d.SmartContent is { } l && ReferenceEquals(l.Parent, doc) && l.UniqueId == opened.Link!.UniqueId);
        if (existing is not null)
        {
            Factory.SetActiveDockable(existing);
            ActiveDocument = existing;
            return;
        }
        var child = new DocumentViewModel(opened.Content!, null, this) { ConfirmClose = ConfirmCloseAsync, SmartContent = opened.Link, Title = opened.Title };
        Factory.AddDocument(child);
        ActiveDocument = child;
        child.SelectedLayer = child.Layers.FirstOrDefault();
        await child.RenderAsync();
    }

    /// <summary>Saving an Edit Contents tab: its content goes back into the parent's smart object ("Update Smart Object").</summary>
    private async Task<bool> SaveSmartContentAsync(DocumentViewModel child, SmartContentLink link)
    {
        if (!Factory.OpenDocuments().Contains(link.Parent))
        {
            await _dialogs.ShowErrorAsync("Cannot save", "The document this smart object belongs to has been closed. Use Save As to keep the contents as a file.");
            return false;
        }
        child.CommitType();
        if (!await link.Parent.UpdateSmartObjectAsync(link.UniqueId, child.Model, link.Name, link.FileType))
        {
            await _dialogs.ShowErrorAsync("Could not update the smart object", link.Parent.Notice);
            return false;
        }
        child.MarkContentSaved();
        return true;
    }

    /// <summary>Layer › Smart Objects › Replace Contents…</summary>
    [RelayCommand]
    public async Task ReplaceContents()
    {
        if (ActiveDocument is not { SelectedSmartObject: not null } doc)
        {
            if (ActiveDocument is { } d) d.Notice = "Select a smart object to replace its contents.";
            return;
        }
        if (await PickSmartFile() is { } path) await doc.ReplaceContentsAsync(path);
    }

    /// <summary>Layer › Smart Objects › Relink to File…</summary>
    [RelayCommand]
    public async Task RelinkToFile()
    {
        if (ActiveDocument is not { SelectedSmartObject: not null } doc) return;
        if (await PickSmartFile() is { } path && await doc.RelinkAsync(path)) WatchLinkedFiles(doc);
    }

    /// <summary>Layer › Smart Objects › Update Modified Content.</summary>
    [RelayCommand]
    public async Task UpdateModifiedContent()
    {
        if (ActiveDocument is { } doc)
        {
            int n = await doc.UpdateLinkedAsync();
            if (n == 0) doc.Notice = "No linked smart object needed updating.";
        }
    }

    /// <summary>Layer › Smart Objects › Export Contents…</summary>
    [RelayCommand]
    public async Task ExportContents()
    {
        if (ActiveDocument is not { } doc || doc.SmartContentForExport() is not { } content) return;
        var path = await (PickSmartExportFile?.Invoke(content.Name) ?? _dialogs.PickFileToSaveAsync(content.Name));
        if (path is null) return;
        try
        {
            await File.WriteAllBytesAsync(path, content.Data);
        }
        catch (IOException ex)
        {
            await _dialogs.ShowErrorAsync("Could not export the contents", ex.Message);
        }
    }

    [RelayCommand] public void NewSmartObjectViaCopy() => ActiveDocument?.NewSmartObjectViaCopy();

    [RelayCommand] public Task ConvertToSmartObject() => ActiveDocument?.ConvertToSmartObjectAsync() ?? Task.CompletedTask;

    /// <summary>
    /// The Filter menu on a smart object: the filter becomes a smart filter (its dialog previews on the canvas as usual);
    /// true when the target was a smart object (handled here), false to filter pixels as usual.
    /// </summary>
    private async Task<bool> FilterSmartObjectAsync(DocumentViewModel doc, IFilterDialogs dialogs, FilterKind kind, ImageFilter initial, bool showDialog)
    {
        if (doc.EditMask || doc.SelectedSmartObject is not { } layer) return false;
        if (!layer.Visible)
        {
            doc.Notice = $"Could not complete the {initial.Name} command because the target layer is hidden.";
            return true;
        }
        var filter = initial;
        if (showDialog)
        {
            if (!doc.BeginSmartFilterPreview(layer, initial.Name)) return true;
            using var session = new FilterSessionViewModel(doc, kind, initial);
            bool ok;
            try
            {
                ok = await dialogs.RunFilterAsync(session);
            }
            catch
            {
                session.Cancel();
                throw;
            }
            session.Cancel(); // the preview ends; the smart filter redraws the layer from its content
            if (!ok) return true;
            filter = session.Filter;
            _filterSettings[kind] = filter;
            LastFilter = filter;
        }
        await doc.AddSmartFilterAsync(layer, filter);
        return true;
    }

    /// <summary>Double-clicking a smart filter: its dialog with the current settings, then the layer redrawn (one undo step).</summary>
    public async Task EditSmartFilterAsync(DocumentViewModel doc, PixelLayer layer, int index)
    {
        if (FilterDialogProvider is not { } dialogs || DocumentViewModel.SmartFiltersOf(layer) is not { } stack || index >= stack.Filters.Count) return;
        if (SmartObjects.ToFilter(stack.Filters[index]) is not { } current)
        {
            doc.Notice = $"Strayta does not have the {stack.Filters[index].Name} filter; it is kept as stored in the file.";
            return;
        }
        var kind = FilterSessionViewModel.KindOf(current);
        // The layer's pixels already include this filter, so previewing on them would apply it twice: the canvas
        // preview starts off, and the dialog's own preview shows the settings.
        if (!doc.BeginSmartFilterPreview(layer, current.Name)) return;
        using var session = new FilterSessionViewModel(doc, kind, current) { Preview = false };
        bool ok;
        try
        {
            ok = await dialogs.RunFilterAsync(session);
        }
        catch
        {
            session.Cancel();
            throw;
        }
        session.Cancel();
        if (ok) await doc.EditSmartFilterAsync(layer, index, session.Filter);
    }

    // ---- Watching linked files ------------------------------------------------------------------------

    private readonly Dictionary<string, FileSystemWatcher> _linkWatchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _linkStamps = new(StringComparer.Ordinal);

    /// <summary>Watches the folders of <paramref name="doc"/>'s linked files; a change redraws the smart objects showing them.</summary>
    public void WatchLinkedFiles(DocumentViewModel doc)
    {
        foreach (var path in doc.LinkedFilePaths().Distinct())
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir is null || !Directory.Exists(dir) || _linkWatchers.ContainsKey(dir)) continue;
            var watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            watcher.Changed += (_, e) => OnLinkedFileChanged(e.FullPath);
            watcher.Created += (_, e) => OnLinkedFileChanged(e.FullPath);
            watcher.Renamed += (_, e) => OnLinkedFileChanged(e.FullPath);
            watcher.EnableRaisingEvents = true;
            _linkWatchers[dir] = watcher;
        }
    }

    private void OnLinkedFileChanged(string path)
    {
        // Saves arrive as several events; act once the file has settled.
        lock (_linkStamps) _linkStamps[path] = DateTime.UtcNow;
        _ = Task.Delay(400).ContinueWith(_ =>
        {
            lock (_linkStamps)
                if (!_linkStamps.TryGetValue(path, out var t) || DateTime.UtcNow - t < TimeSpan.FromMilliseconds(350)) return;
            Dispatcher.UIThread.Post(async () =>
            {
                foreach (var doc in Factory.OpenDocuments().ToList())
                    if (doc.LinkedFilePaths().Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.Ordinal)))
                        await doc.UpdateLinkedAsync(path);
            });
        }, TaskScheduler.Default);
    }
}
