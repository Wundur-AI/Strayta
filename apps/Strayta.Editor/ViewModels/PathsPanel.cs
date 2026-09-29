using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using Strayta.Core;
using Strayta.Core.Paths;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>A row of the Paths panel: the selected shape layer's path, the Work Path, or a saved path.</summary>
/// <param name="Index">Index among the document's paths, or -1 for the shape layer's path.</param>
public sealed record PathItem(string Name, int Index, bool IsWorkPath, bool IsShapePath, Bitmap? Thumbnail)
{
    /// <summary>The Work Path and a shape's path are shown in italics, as in Photoshop.</summary>
    public Avalonia.Media.FontStyle Style => IsWorkPath || IsShapePath ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal;
}

/// <summary>
/// Window › Paths: the selected shape layer's path, the Work Path and the saved paths, each with a thumbnail. Choosing
/// one makes the pen and path selection tools edit it; the footer fills or strokes it, loads it as a selection or
/// makes a work path from the selection, adds and deletes paths; the menu saves, renames and duplicates.
/// </summary>
public sealed partial class PathsToolViewModel : Tool, IDisposable
{
    private DocumentViewModel? _document;
    private bool _syncing;

    public PathsToolViewModel(EditorViewModel editor)
    {
        Editor = editor;
        editor.PropertyChanged += OnEditorChanged;
        Follow(editor.ActiveDocument);
    }

    public EditorViewModel Editor { get; }

    public ObservableCollection<PathItem> Items { get; } = [];

    public bool HasDocument => _document is not null;

    [ObservableProperty] public partial PathItem? Selected { get; set; }

    partial void OnSelectedChanged(PathItem? value)
    {
        // The list clearing its selection while it is rebuilt must not deselect the path: only Deselect Path does.
        if (_syncing || value is null || _document is not { } doc) return;
        int index = value.IsShapePath ? -1 : value.Index;
        if (index != doc.SelectedPathIndex || index < 0) doc.SelectDocumentPath(index);
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.ActiveDocument)) Follow(Editor.ActiveDocument);
    }

    private void Follow(DocumentViewModel? doc)
    {
        if (_document is not null)
        {
            _document.PathsChanged -= Refresh;
            _document.PropertyChanged -= OnDocumentChanged;
        }
        _document = doc;
        if (doc is not null)
        {
            doc.PathsChanged += Refresh;
            doc.PropertyChanged += OnDocumentChanged;
        }
        OnPropertyChanged(nameof(HasDocument));
        Refresh();
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.UndoText) or nameof(DocumentViewModel.SelectedLayer) or nameof(DocumentViewModel.SelectedPathIndex))
            Refresh();
    }

    /// <summary>Rebuilds the rows from the document.</summary>
    public void Refresh()
    {
        _syncing = true;
        try
        {
            Items.Clear();
            Selected = null;
            if (_document is not { } doc) return;
            if (doc.SelectedLayer?.Node is PixelLayer layer && ShapeLayers.Read(doc.Model, layer) is { } shape)
                Items.Add(new PathItem($"{layer.Name} Shape Path", -1, false, true, Thumbnail(doc.Model, shape.Path)));
            var paths = doc.DocumentPaths;
            for (int i = 0; i < paths.Count; i++)
                Items.Add(new PathItem(paths[i].Name, i, paths[i].Kind == DocumentPathKind.Work, false, Thumbnail(doc.Model, paths[i].Path)));
            var target = doc.PathTarget();
            Selected = target.Kind switch
            {
                PathTargetKind.Document => Items.FirstOrDefault(p => !p.IsShapePath && p.Index == target.Index),
                PathTargetKind.Shape => Items.FirstOrDefault(p => p.IsShapePath),
                _ => null,
            };
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The path's area at thumbnail size (the canvas shape, 40 pixels on its long side), dark on light.</summary>
    private static Bitmap? Thumbnail(Strayta.Core.Document doc, VectorPath path)
    {
        double scale = 40.0 / Math.Max(doc.Width, doc.Height);
        int w = Math.Max(1, (int)Math.Round(doc.Width * scale)), h = Math.Max(1, (int)Math.Round(doc.Height * scale));
        var small = path.Map(p => new PathPoint(p.X * scale, p.Y * scale));
        var coverage = PathRasterizer.Rasterize(small, new PixelRect(0, 0, w, h));
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte v = (byte)(235 - coverage[i] * 200 / 255);
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (v, v, v, 255);
        }
        return BitmapFactory.FromRgba(rgba, w, h);
    }

    // ---- Commands ----------------------------------------------------------------------------------------

    /// <summary>Asks for a path name (set by the window).</summary>
    public static Func<string, string, Task<string?>>? AskName { get; set; }

    [RelayCommand]
    private async Task SavePath()
    {
        if (_document is not { } doc) return;
        string suggested = doc.DocumentPaths.Count(p => p.Kind == DocumentPathKind.Saved) is var n ? $"Path {n + 1}" : "Path 1";
        string? name = AskName is { } ask ? await ask("Save Path", suggested) : suggested;
        if (name is { Length: > 0 }) doc.SavePath(name);
    }

    [RelayCommand]
    private async Task Rename()
    {
        if (_document is not { } doc || Selected is not { IsShapePath: false } item) return;
        string? name = AskName is { } ask ? await ask("Rename Path", item.Name) : null;
        if (name is { Length: > 0 }) doc.RenamePath(item.Index, name);
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (_document is { } doc && Selected is { IsShapePath: false } item) doc.DuplicatePath(item.Index);
    }

    [RelayCommand]
    private void Delete()
    {
        if (_document is { } doc && Selected is { IsShapePath: false } item) doc.DeletePath(item.Index);
    }

    /// <summary>Clicking empty space in the panel deselects the path, as in Photoshop.</summary>
    [RelayCommand]
    private void Deselect()
    {
        _document?.SelectDocumentPath(-1);
        Refresh();
    }

    public void Dispose()
    {
        Editor.PropertyChanged -= OnEditorChanged;
        Follow(null);
    }
}
