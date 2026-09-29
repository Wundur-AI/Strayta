using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

// Magic Wand and Quick Selection: each tool's options-bar settings (app-wide, as in Photoshop).
public sealed partial class EditorViewModel
{
    public bool IsMagicWandTool { get => Tool == CanvasTool.MagicWand; set { if (value) Tool = CanvasTool.MagicWand; } }
    public bool IsQuickSelectTool { get => Tool == CanvasTool.QuickSelect; set { if (value) Tool = CanvasTool.QuickSelect; } }

    /// <summary>W picks the last used tool of the selection group; Shift+W ("cycle") steps through it.</summary>
    [RelayCommand]
    private void SelectWandTool(string? mode)
    {
        if (mode == "cycle") _selectionGroup.Cycle();
        else _selectionGroup.Activate();
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

    public MagicWandOptions CurrentWandOptions => new((int)WandTolerance, WandAntiAlias, WandContiguous, WandSampleSize);

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
