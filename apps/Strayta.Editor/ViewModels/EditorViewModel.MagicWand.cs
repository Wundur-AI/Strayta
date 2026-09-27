using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

// Magic Wand and Quick Selection: the W tool group and each tool's options-bar settings (app-wide, as in Photoshop).
public sealed partial class EditorViewModel
{
    public bool IsMagicWandTool { get => Tool == CanvasTool.MagicWand; set { if (value) Tool = _lastWandTool = CanvasTool.MagicWand; } }
    public bool IsQuickSelectTool { get => Tool == CanvasTool.QuickSelect; set { if (value) Tool = _lastWandTool = CanvasTool.QuickSelect; } }

    /// <summary>The tool W switches to: the one of the group used last.</summary>
    private CanvasTool _lastWandTool = CanvasTool.MagicWand;

    /// <summary>W picks the last used tool of the group; Shift+W ("cycle") switches between Quick Selection and Magic Wand.</summary>
    [RelayCommand]
    private void SelectWandTool(string? mode)
    {
        if (mode == "cycle" && Tool is CanvasTool.MagicWand or CanvasTool.QuickSelect)
            _lastWandTool = Tool == CanvasTool.MagicWand ? CanvasTool.QuickSelect : CanvasTool.MagicWand;
        Tool = _lastWandTool;
    }

    /// <summary>Magic Wand tolerance, 0..255 levels per channel.</summary>
    [ObservableProperty] public partial double WandTolerance { get; set; } = 32;

    partial void OnWandToleranceChanged(double value)
    {
        double clamped = Math.Clamp(Math.Round(value), 0, 255);
        if (clamped != value) WandTolerance = clamped;
    }

    [ObservableProperty] public partial bool WandAntiAlias { get; set; } = true;
    [ObservableProperty] public partial bool WandContiguous { get; set; } = true;
    [ObservableProperty] public partial bool WandSampleAllLayers { get; set; }

    public MagicWandOptions CurrentWandOptions => new((int)WandTolerance, WandAntiAlias, WandContiguous);

    /// <summary>Quick Selection brush diameter in pixels (its own size, separate from the paint brush).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolBrushSize))]
    public partial double QuickSelectSize { get; set; } = 30;

    [ObservableProperty] public partial bool QuickSelectSampleAllLayers { get; set; }

    /// <summary>Soft, smoothed edges instead of hard ones (Photoshop's Auto-Enhance; off by default there too).</summary>
    [ObservableProperty] public partial bool QuickSelectAutoEnhance { get; set; }

    /// <summary>The brush outline the canvas draws: the Quick Selection brush or the paint brush.</summary>
    public double ToolBrushSize => Tool == CanvasTool.QuickSelect ? QuickSelectSize : BrushSize;
}
