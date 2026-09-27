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

    [RelayCommand] private void SelectAll() => ActiveDocument?.SelectAll();
    [RelayCommand] private void Deselect() => ActiveDocument?.Deselect();
    [RelayCommand] private void Reselect() => ActiveDocument?.Reselect();
    [RelayCommand] private void InvertSelection() => ActiveDocument?.InvertSelection();
    [RelayCommand] private Task Clear() => ActiveDocument?.ClearAsync() ?? Task.CompletedTask;
    [RelayCommand] private Task Fill(string? color) => ActiveDocument?.FillAsync(background: color == "background") ?? Task.CompletedTask;
    [RelayCommand] private void Copy() => ActiveDocument?.Copy();
    [RelayCommand] private Task Cut() => ActiveDocument?.CutAsync() ?? Task.CompletedTask;
    [RelayCommand] private void Paste() => ActiveDocument?.Paste();
}
