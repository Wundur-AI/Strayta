using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Psd;

namespace Strayta.Editor.ViewModels;

/// <summary>What the editor needs from the window: file pickers and simple dialogs.</summary>
public interface IEditorDialogs
{
    Task<string?> PickFileToOpenAsync();
    Task<string?> PickFileToSaveAsync(string suggestedName);
    /// <summary>Returns "save", "discard" or "cancel".</summary>
    Task<string> AskSaveChangesAsync(string documentName);
    Task ShowErrorAsync(string title, string message);
    /// <summary>Returns the new document's size and background, or null if cancelled.</summary>
    Task<(int Width, int Height, bool White)?> AskNewDocumentAsync();
    /// <summary>File › Export As settings (format, JPEG quality, transparency, matte), or null if cancelled.</summary>
    Task<Strayta.Rendering.Export.ExportOptions?> AskExportOptionsAsync(Strayta.Rendering.Export.ExportOptions current, (byte R, byte G, byte B) backgroundColor);
    Task<string?> PickExportFileAsync(string suggestedName, Strayta.Rendering.Export.ExportFormat format);
}

public sealed partial class EditorViewModel : ObservableObject
{
    private readonly IEditorDialogs _dialogs;

    public EditorViewModel(IEditorDialogs dialogs)
    {
        _dialogs = dialogs;
        Factory = new DockFactory(this);
        Layout = Factory.CreateLayout();
        Factory.InitLayout(Layout);
        HookFactoryEvents();
    }

    private void HookFactoryEvents()
    {
        Factory.ActiveDockableChanged += (_, e) =>
        {
            if (e.Dockable is DocumentViewModel d) ActiveDocument = d;
        };
        Factory.FocusedDockableChanged += (_, e) =>
        {
            if (e.Dockable is DocumentViewModel d) ActiveDocument = d;
        };
        Factory.DockableClosed += (_, e) =>
        {
            if (ReferenceEquals(e.Dockable, ActiveDocument))
                ActiveDocument = Factory.OpenDocuments().FirstOrDefault(d => !ReferenceEquals(d, e.Dockable));
        };
    }

    public DockFactory Factory { get; }

    [ObservableProperty] public partial IRootDock Layout { get; private set; }

    /// <summary>
    /// Window > Reset Layout: puts every panel back where it started (e.g. after one was closed or lost)
    /// and keeps the open documents.
    /// </summary>
    /// <summary>Window > Layers / Color / Swatches: bring a panel forward, restoring the layout if it was lost.</summary>
    [RelayCommand]
    private void ShowPanel(string id)
    {
        var panel = Factory.Find(d => d.Id == id).FirstOrDefault();
        if (panel is null)
        {
            ResetLayout();
            panel = Factory.Find(d => d.Id == id).FirstOrDefault();
        }
        if (panel is not null) Factory.SetActiveDockable(panel);
    }

    [RelayCommand]
    private void ResetLayout()
    {
        var documents = Factory.OpenDocuments().ToList();
        var active = ActiveDocument;
        foreach (var window in Layout.Windows?.ToList() ?? []) window.Exit();
        var layout = Factory.CreateLayout();
        Factory.InitLayout(layout);
        Layout = layout;
        foreach (var doc in documents) Factory.AddDocument(doc);
        if (active is not null) Factory.SetActiveDockable(active);
        ActiveDocument = active;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(SaveAsCommand), nameof(CloseCommand), nameof(ExportAsCommand))]
    public partial DocumentViewModel? ActiveDocument { get; set; }

    public bool HasDocument => ActiveDocument is not null;

    // View switcher (segmented control) for the active document.
    public bool IsStraytaView { get => ActiveDocument?.Mode == ViewMode.Strayta; set { if (value) SetView("Strayta"); } }
    public bool IsPhotoshopView { get => ActiveDocument?.Mode == ViewMode.Photoshop; set { if (value) SetView("Photoshop"); } }
    public bool IsDifferenceView { get => ActiveDocument?.Mode == ViewMode.Difference; set { if (value) SetView("Difference"); } }

    private void NotifyViewMode()
    {
        OnPropertyChanged(nameof(IsStraytaView));
        OnPropertyChanged(nameof(IsPhotoshopView));
        OnPropertyChanged(nameof(IsDifferenceView));
    }

    /// <summary>Raised when the active document or its undo state changes (for menu labels).</summary>
    public event Action? DocumentChanged;

    partial void OnActiveDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnDocumentPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnDocumentPropertyChanged;
        DocumentChanged?.Invoke();
        NotifyViewMode();
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(IsTransforming));
    }

    private void OnDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.CanUndo) or nameof(DocumentViewModel.UndoText)) DocumentChanged?.Invoke();
        if (e.PropertyName is nameof(DocumentViewModel.Mode)) NotifyViewMode();
        if (e.PropertyName is nameof(DocumentViewModel.Title) or nameof(DocumentViewModel.IsModified)) OnPropertyChanged(nameof(WindowTitle));
        if (e.PropertyName is nameof(DocumentViewModel.IsTransforming)) OnPropertyChanged(nameof(IsTransforming));
    }

    public async Task OpenAsync(string path)
    {
        var existing = Factory.OpenDocuments().FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.Ordinal));
        if (existing is not null)
        {
            Factory.SetActiveDockable(existing);
            return;
        }

        try
        {
            var model = await Task.Run(() => PsdFile.OpenForEditing(path));
            var document = new DocumentViewModel(model, path, this) { ConfirmClose = ConfirmCloseAsync };
            Factory.AddDocument(document);
            ActiveDocument = document;
            await document.RenderAsync();
            if (Environment.GetEnvironmentVariable("STRAYTA_DRAGBENCH") == "1")
            {
                await Task.Delay(1500); // let the view fit the image and the preview warm up
                await document.RunDragBenchmarkAsync();
            }
            if (Environment.GetEnvironmentVariable("STRAYTA_PAINTBENCH") == "1")
            {
                await Task.Delay(1500);
                await document.RunPaintBenchmarkAsync();
            }
            if (Environment.GetEnvironmentVariable("STRAYTA_TRANSFORMBENCH") == "1")
            {
                await Task.Delay(1500);
                await document.RunTransformBenchmarkAsync();
            }
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open file", $"{Path.GetFileName(path)}: {ex.Message}");
        }
    }

    // ---- Tools and brush (app-wide, as in Photoshop) ---------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoveTool), nameof(IsHandTool), nameof(IsBrushTool), nameof(IsEraserTool), nameof(IsPaintTool), nameof(ToolName))]
    public partial CanvasTool Tool { get; set; } = CanvasTool.Move;

    public bool IsMoveTool { get => Tool == CanvasTool.Move; set { if (value) Tool = CanvasTool.Move; } }
    public bool IsHandTool { get => Tool == CanvasTool.Hand; set { if (value) Tool = CanvasTool.Hand; } }
    public bool IsBrushTool { get => Tool == CanvasTool.Brush; set { if (value) Tool = CanvasTool.Brush; } }
    public bool IsEraserTool { get => Tool == CanvasTool.Eraser; set { if (value) Tool = CanvasTool.Eraser; } }
    public bool IsPaintTool => Tool is CanvasTool.Brush or CanvasTool.Eraser;
    public string ToolName => Tool.ToString();

    /// <summary>Brush diameter in pixels.</summary>
    [ObservableProperty] public partial double BrushSize { get; set; } = 30;

    /// <summary>Brush hardness in percent.</summary>
    [ObservableProperty] public partial double BrushHardness { get; set; } = 80;

    /// <summary>Brush opacity in percent.</summary>
    [ObservableProperty] public partial double BrushOpacity { get; set; } = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForegroundBrush))]
    public partial Color ForegroundColor { get; set; } = Colors.Black;

    public IBrush ForegroundBrush => new SolidColorBrush(ForegroundColor);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    public partial Color BackgroundColor { get; set; } = Colors.White;

    public IBrush BackgroundBrush => new SolidColorBrush(BackgroundColor);

    /// <summary>X: swap foreground and background colors.</summary>
    [RelayCommand]
    private void SwapColors() => (ForegroundColor, BackgroundColor) = (BackgroundColor, ForegroundColor);

    /// <summary>D: default black foreground, white background.</summary>
    [RelayCommand]
    private void DefaultColors() => (ForegroundColor, BackgroundColor) = (Colors.Black, Colors.White);

    /// <summary>Saved colors for the Swatches panel, starting with a basic set.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Color> Swatches { get; } = new(
        new[]
        {
            "#000000", "#404040", "#808080", "#BFBFBF", "#FFFFFF",
            "#E53935", "#FB8C00", "#FDD835", "#43A047", "#00ACC1", "#1E88E5", "#5E35B1", "#D81B60",
            "#FFCDD2", "#FFE0B2", "#FFF9C4", "#C8E6C9", "#B2EBF2", "#BBDEFB", "#D1C4E9", "#F8BBD0",
            "#7F0000", "#8D4E00", "#7A6A00", "#1B5E20", "#006064", "#0D47A1", "#311B92", "#880E4F",
        }.Select(Color.Parse));

    [RelayCommand]
    private void UseSwatch(Color color) => ForegroundColor = color;

    [RelayCommand]
    private void AddSwatch()
    {
        if (!Swatches.Contains(ForegroundColor)) Swatches.Add(ForegroundColor);
    }

    public BrushSettings CurrentBrush => new((float)BrushSize, (float)(BrushHardness / 100), (float)(BrushOpacity / 100));
    public RgbColor CurrentColor => new(ForegroundColor.R / 255f, ForegroundColor.G / 255f, ForegroundColor.B / 255f);

    /// <summary>[ and ] resize the brush in steps that scale with its size, like Photoshop.</summary>
    [RelayCommand]
    private void ResizeBrush(string direction)
    {
        double step = BrushSize < 10 ? 1 : BrushSize < 100 ? 5 : BrushSize < 300 ? 10 : 25;
        BrushSize = Math.Clamp(BrushSize + (direction == "up" ? step : -step), 1, 1000);
    }

    [RelayCommand]
    private void SetAppearance(string variant)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = variant switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
        OnPropertyChanged(nameof(IsLightAppearance));
    }

    public bool IsLightAppearance => Application.Current?.ActualThemeVariant == ThemeVariant.Light;

    [RelayCommand]
    private void ToggleAppearance() => SetAppearance(IsLightAppearance ? "Dark" : "Light");

    /// <summary>Title shown in the title bar: the active document, marked when it has unsaved changes.</summary>
    public string WindowTitle => ActiveDocument is { } d ? (d.IsModified ? $"{d.Title} — Edited" : d.Title) : "Strayta";

    // ---- Documents and layers ---------------------------------------------------------------------

    [RelayCommand]
    private async Task NewDocument()
    {
        if (await _dialogs.AskNewDocumentAsync() is not { } spec) return;
        var model = LayerFactory.NewDocument(spec.Width, spec.Height, spec.White);
        var document = new DocumentViewModel(model, null, this) { ConfirmClose = ConfirmCloseAsync };
        Factory.AddDocument(document);
        ActiveDocument = document;
        document.SelectedLayer = document.Layers.FirstOrDefault();
        await document.RenderAsync();
    }

    [RelayCommand] private void NewLayer() => ActiveDocument?.NewLayer();
    [RelayCommand] private void NewGroup() => ActiveDocument?.NewGroup();
    [RelayCommand] private void DuplicateLayer() => ActiveDocument?.DuplicateSelected();
    [RelayCommand] private void RasterizeLayer() => ActiveDocument?.RasterizeSelected();

    [RelayCommand]
    private async Task Open()
    {
        if (await _dialogs.PickFileToOpenAsync() is { } path) await OpenAsync(path);
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task Save()
    {
        if (ActiveDocument is { } doc) await SaveDocumentAsync(doc, saveAs: false);
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task SaveAs()
    {
        if (ActiveDocument is { } doc) await SaveDocumentAsync(doc, saveAs: true);
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Close()
    {
        if (ActiveDocument is { } doc) Factory.CloseDockable(doc);
    }

    [RelayCommand] private void Undo() => ActiveDocument?.Undo();
    [RelayCommand] private void Redo() => ActiveDocument?.Redo();
    [RelayCommand] private void LayerUp() => ActiveDocument?.Restack(+1);
    [RelayCommand] private void LayerDown() => ActiveDocument?.Restack(-1);
    [RelayCommand] private void DeleteLayer() => ActiveDocument?.DeleteSelected();

    [RelayCommand]
    private void SetView(string mode)
    {
        if (ActiveDocument is { } doc && Enum.TryParse<ViewMode>(mode, out var m)) doc.Mode = m;
        NotifyViewMode();
    }

    [RelayCommand]
    private void SetTool(string tool)
    {
        if (Enum.TryParse<CanvasTool>(tool, out var t)) Tool = t;
    }

    [RelayCommand] private void Fit() => ActiveDocument?.RequestZoom("fit");
    [RelayCommand] private void ActualSize() => ActiveDocument?.RequestZoom("actual");

    /// <returns>False if the user cancelled or saving failed.</returns>
    private async Task<bool> SaveDocumentAsync(DocumentViewModel doc, bool saveAs)
    {
        if (!doc.CanSave)
        {
            await _dialogs.ShowErrorAsync("Cannot save", $"Saving {doc.Model.ColorMode} documents is not supported yet.");
            return false;
        }
        string? path = saveAs || doc.FilePath is null ? await _dialogs.PickFileToSaveAsync(doc.Title) : doc.FilePath;
        if (path is null) return false;
        try
        {
            await doc.SaveAsync(path);
            return true;
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not save", ex.Message);
            return false;
        }
    }

    private async Task<bool> ConfirmCloseAsync(DocumentViewModel doc)
    {
        switch (await _dialogs.AskSaveChangesAsync(doc.Title))
        {
            case "save": return await SaveDocumentAsync(doc, saveAs: false);
            case "discard": return true;
            default: return false;
        }
    }

    /// <summary>Called when the window closes; true if every document may be discarded.</summary>
    public async Task<bool> ConfirmQuitAsync()
    {
        foreach (var doc in Factory.OpenDocuments().Where(d => d.IsModified).ToList())
        {
            Factory.SetActiveDockable(doc);
            if (!await ConfirmCloseAsync(doc)) return false;
        }
        return true;
    }
}
