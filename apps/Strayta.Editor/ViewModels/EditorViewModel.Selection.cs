using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

// Selection tools and the Select / Edit menu commands.
public sealed partial class EditorViewModel
{
    public bool IsRectSelectTool { get => Tool == CanvasTool.RectSelect; set { if (value) Tool = CanvasTool.RectSelect; } }
    public bool IsEllipseSelectTool { get => Tool == CanvasTool.EllipseSelect; set { if (value) Tool = CanvasTool.EllipseSelect; } }
    public bool IsLassoTool { get => Tool == CanvasTool.Lasso; set { if (value) Tool = CanvasTool.Lasso; } }
    public bool IsMarqueeTool => Tool is CanvasTool.RectSelect or CanvasTool.EllipseSelect;

    /// <summary>M picks the last used marquee; Shift+M ("cycle") switches between rectangular and elliptical.</summary>
    [RelayCommand]
    private void SelectMarquee(string? mode)
    {
        if (mode == "cycle") _marqueeGroup.Cycle();
        else _marqueeGroup.Activate();
    }

    /// <summary>Pixels from the last Copy or Cut, shared by all documents.</summary>
    public ClipboardImage? Clipboard { get; set; }

    public RgbColor CurrentBackgroundColor => new(BackgroundColor.R / 255f, BackgroundColor.G / 255f, BackgroundColor.B / 255f);

    // While typing, Select All and the clipboard act on the text (EditorViewModel.Type.cs).
    [RelayCommand] private void SelectAll() { if (!SelectAllText()) ActiveDocument?.SelectAll(); }
    [RelayCommand] private void Deselect() => ActiveDocument?.Deselect();
    [RelayCommand] private void Reselect() => ActiveDocument?.Reselect();
    [RelayCommand] private void InvertSelection() => ActiveDocument?.InvertSelection();
    [RelayCommand] private Task Clear() => ActiveDocument?.ClearAsync() ?? Task.CompletedTask;
    [RelayCommand] private Task Fill(string? color) => ActiveDocument?.FillAsync(background: color == "background") ?? Task.CompletedTask;
    [RelayCommand] private async Task Copy() { if (!await CopyTextAsync(cut: false)) ActiveDocument?.Copy(); }
    [RelayCommand] private async Task Cut() { if (!await CopyTextAsync(cut: true) && ActiveDocument is { } doc) await doc.CutAsync(); }
    [RelayCommand] private async Task Paste() { if (!await PasteTextAsync()) ActiveDocument?.Paste(); }
}
