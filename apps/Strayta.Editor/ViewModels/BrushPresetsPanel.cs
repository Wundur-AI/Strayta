using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>Window › Brushes, as a dockable panel (BrushesView).</summary>
public sealed class BrushesToolViewModel(EditorViewModel editor) : Tool
{
    public EditorViewModel Editor { get; } = editor;
    public BrushPresetsViewModel Presets => Editor.BrushPresets;
}

/// <summary>A preset in the Brushes panel and the picker, with its tip thumbnail and stroke preview (made when first shown).</summary>
public sealed partial class BrushPresetItem(BrushPreset preset) : ObservableObject
{
    private Bitmap? _tip, _stroke;

    public BrushPreset Preset { get; } = preset;
    public string Name => Preset.Name;
    public string SizeText => $"{Preset.Size:0}";
    public string Tip => $"{Preset.Name} · {Preset.Size:0} px{(Preset.Tip is null ? $" · hardness {Preset.Hardness * 100:0}%" : "")}";

    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>The tip as a white mask (the view tints it with the text color), 32 × 32.</summary>
    public Bitmap TipImage => _tip ??= BrushPreviews.Tip(Preset, 32);

    /// <summary>A short S-shaped stroke painted with the preset (pressure fading in and out), as a white mask.</summary>
    public Bitmap StrokeImage => _stroke ??= BrushPreviews.Stroke(Preset, 120, 34);
}

/// <summary>A folder of presets.</summary>
public sealed partial class BrushFolderViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;
    public ObservableCollection<BrushPresetItem> Items { get; } = [];
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
}

/// <summary>
/// The brush presets as the Brushes panel and the options-bar picker show them: folders of presets with thumbnails;
/// clicking one sets the brush (<see cref="EditorViewModel.ApplyBrushPreset"/>). New Brush Preset saves the current brush,
/// Import Brushes reads .abr files (Strayta.Psd's AbrReader); the person's presets are kept by <see cref="BrushPresetLibrary"/>.
/// </summary>
public sealed partial class BrushPresetsViewModel : ObservableObject
{
    private readonly EditorViewModel _editor;
    private BrushPresetLibrary? _library;

    /// <summary>Asks for a name (title, suggestion); null when cancelled. Set by the main window.</summary>
    public static Func<string, string, Task<string?>>? AskName { get; set; }

    /// <summary>Lets the person choose .abr files; empty when cancelled. Set by the main window.</summary>
    public static Func<Task<IReadOnlyList<string>>>? PickBrushFiles { get; set; }

    public BrushPresetsViewModel(EditorViewModel editor)
    {
        _editor = editor;
        Rebuild();
    }

    public ObservableCollection<BrushFolderViewModel> Folders { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSelected))]
    public partial BrushPresetItem? Selected { get; set; }

    public bool CanEditSelected => Selected is { Preset.BuiltIn: false };

    /// <summary>The library in use (it follows the settings folder, which the self-test redirects).</summary>
    public BrushPresetLibrary Library
    {
        get
        {
            var shared = BrushPresetLibrary.Shared;
            if (!ReferenceEquals(shared, _library)) Rebuild();
            return _library!;
        }
    }

    /// <summary>The folder new presets go into: the selected preset's own folder when it is the person's, else "My Brushes".</summary>
    private string TargetFolder => Selected is { Preset.BuiltIn: false } s ? s.Preset.Folder : "My Brushes";

    public void Rebuild()
    {
        var shared = BrushPresetLibrary.Shared;
        if (!ReferenceEquals(shared, _library))
        {
            if (_library is not null) _library.Changed -= Rebuild;
            _library = shared;
            _library.Changed += Rebuild;
        }
        var expanded = Folders.ToDictionary(f => f.Name, f => f.IsExpanded);
        string? selected = Selected?.Preset.Name;
        string? selectedFolder = Selected?.Preset.Folder;
        Folders.Clear();
        foreach (var name in _library.Folders)
        {
            var folder = new BrushFolderViewModel(name) { IsExpanded = expanded.GetValueOrDefault(name, true) };
            foreach (var preset in _library.Presets.Where(p => p.Folder == name)) folder.Items.Add(new BrushPresetItem(preset));
            Folders.Add(folder);
        }
        Selected = Folders.SelectMany(f => f.Items).FirstOrDefault(i => i.Name == selected && i.Preset.Folder == selectedFolder);
        if (Selected is not null) Selected.IsSelected = true;
    }

    /// <summary>All presets in display order.</summary>
    public IEnumerable<BrushPresetItem> Items => Folders.SelectMany(f => f.Items);

    [RelayCommand]
    public void Select(BrushPresetItem? item)
    {
        if (item is null) return;
        if (Selected is not null) Selected.IsSelected = false;
        Selected = item;
        item.IsSelected = true;
        _editor.ApplyBrushPreset(item.Preset);
    }

    /// <summary>New Brush Preset: the current brush (tip, size, hardness, spacing, angle, roundness, pressure, dynamics).</summary>
    [RelayCommand]
    public async Task NewPresetAsync()
    {
        string suggestion = $"{_editor.BrushPresetName} {_editor.BrushSize:0}";
        string? name = AskName is null ? suggestion : await AskName("New Brush Preset", suggestion);
        if (string.IsNullOrWhiteSpace(name)) return;
        AddPreset(name.Trim(), TargetFolder);
    }

    /// <summary>Adds the current brush as a preset (without asking); returns it.</summary>
    public BrushPreset AddPreset(string name, string folder)
    {
        var preset = _editor.CurrentBrushAsPreset(name, folder);
        Library.Add(preset);
        _editor.BrushPresetName = name;
        Select(Items.LastOrDefault(i => i.Name == name && i.Preset.Folder == folder));
        return preset;
    }

    /// <summary>Edit › Define Brush Preset…: the selection's visible image becomes a sampled tip and a new preset.</summary>
    [RelayCommand]
    public async Task DefineFromSelectionAsync()
    {
        if (_editor.ActiveDocument is not { } doc) return;
        string? name = AskName is null ? "Sampled Brush" : await AskName("Brush Name", $"Sampled Brush {Items.Count(i => !i.Preset.BuiltIn) + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        await DefineAsync(doc, name.Trim());
    }

    /// <summary>Defines a preset named <paramref name="name"/> from <paramref name="doc"/>'s selection and selects it; null if none was made.</summary>
    public async Task<BrushPreset?> DefineAsync(DocumentViewModel doc, string name)
    {
        if (await doc.DefineBrushTipAsync(name) is not { } tip) return null;
        var preset = new BrushPreset(name, "My Brushes", tip.NativeSize, 1f, 25, 0, 1, tip);
        Library.Add(preset);
        Select(Items.LastOrDefault(i => i.Name == name && i.Preset.Tip == tip));
        return preset;
    }

    [RelayCommand]
    public async Task NewFolderAsync()
    {
        string? name = AskName is null ? "New Folder" : await AskName("New Folder", "Brushes");
        if (!string.IsNullOrWhiteSpace(name)) Library.NewFolder(name);
    }

    [RelayCommand]
    public async Task RenameAsync(BrushPresetItem? item)
    {
        item ??= Selected;
        if (item is not { Preset.BuiltIn: false } || AskName is null) return;
        string? name = await AskName("Rename Brush Preset", item.Name);
        if (!string.IsNullOrWhiteSpace(name)) Library.Rename(item.Preset, name);
    }

    [RelayCommand]
    public void Delete(BrushPresetItem? item)
    {
        item ??= Selected;
        if (item is not { Preset.BuiltIn: false }) return;
        if (ReferenceEquals(item, Selected)) Selected = null;
        Library.Remove(item.Preset);
    }

    [RelayCommand]
    public void DeleteFolder(BrushFolderViewModel? folder)
    {
        if (folder is not null) Library.RemoveFolder(folder.Name);
    }

    /// <summary>Import Brushes…: reads .abr files into folders named after them.</summary>
    [RelayCommand]
    public async Task ImportAsync()
    {
        var files = PickBrushFiles is null ? [] : await PickBrushFiles();
        Import(files);
    }

    /// <summary>Imports .abr files; unreadable ones are reported in the editor's notice. Returns the number of brushes added.</summary>
    public int Import(IEnumerable<string> files)
    {
        int added = 0;
        var problems = new List<string>();
        foreach (var file in files)
        {
            try
            {
                int n = Library.ImportAbr(file);
                if (n == 0) problems.Add($"{Path.GetFileName(file)} has no brushes Strayta can read");
                added += n;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Strayta.Psd.PsdFormatException)
            {
                problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        if (_editor.ActiveDocument is { } doc)
            doc.Notice = problems.Count > 0 ? $"Could not import {string.Join("; ", problems)}" : added > 0 ? $"Imported {added} brushes" : doc.Notice;
        return added;
    }
}

/// <summary>Thumbnails for brush presets: the tip, and a short stroke painted by the brush engine itself.</summary>
internal static class BrushPreviews
{
    public static Bitmap Tip(BrushPreset preset, int size)
    {
        // One dab of the preset, fitted into the square (a tip larger than the square is shown smaller).
        var brush = preset.ToBrush() with { Size = MathF.Min(preset.Size, size - 2), PressureSize = false, PressureOpacity = false, Dynamics = null };
        var stroke = new PaintStroke(new PixelLayer(), brush, new RgbColor(1, 1, 1), false, new PixelRect(0, 0, size, size));
        stroke.StrokeTo(size / 2f, size / 2f);
        return Mask(stroke, size, size);
    }

    public static Bitmap Stroke(BrushPreset preset, int width, int height)
    {
        float size = Math.Clamp(preset.Size, 3f, height * 0.55f);
        var brush = preset.ToBrush() with { Size = size };
        var stroke = new PaintStroke(new PixelLayer(), brush, new RgbColor(1, 1, 1), false, new PixelRect(0, 0, width, height));
        const int steps = 60;
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            float x = 10 + t * (width - 20), y = height / 2f + MathF.Sin(t * MathF.PI * 2) * (height / 2f - size / 2f - 2);
            stroke.StrokeTo(x, y, 0.15f + 0.85f * MathF.Sin(t * MathF.PI));
        }
        return Mask(stroke, width, height);
    }

    private static Bitmap Mask(PaintStroke stroke, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2]) = (255, 255, 255);
                rgba[i + 3] = (byte)MathF.Round(Math.Clamp(stroke.CoverageAt(x, y) * stroke.Brush.Opacity, 0f, 1f) * 255f);
            }
        return BitmapFactory.FromRgba(rgba, w, h);
    }
}
