using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

// Retouching tools: Clone Stamp (S), Spot Healing Brush and Healing Brush (J). Options are app-wide and kept per
// tool, as in Photoshop; the brush size, hardness and opacity are the Brush's (so `[` and `]` work the same). The work
// happens in DocumentViewModel.Retouch.cs and on the canvas (ImageCanvas.Retouch.cs).
public sealed partial class EditorViewModel
{
    public bool IsCloneStampTool { get => Tool == CanvasTool.CloneStamp; set { if (value) Tool = CanvasTool.CloneStamp; } }
    public bool IsHealingTool { get => Tool == CanvasTool.Healing; set { if (value) Tool = CanvasTool.Healing; } }
    public bool IsSpotHealingTool { get => Tool == CanvasTool.SpotHealing; set { if (value) Tool = CanvasTool.SpotHealing; } }

    /// <summary>True for the three retouching tools.</summary>
    public bool IsRetouchTool => Tool is CanvasTool.CloneStamp or CanvasTool.Healing or CanvasTool.SpotHealing;

    /// <summary>True for the tools with an Option-click source point (Clone Stamp, Healing Brush).</summary>
    public bool UsesSourcePoint => Tool is CanvasTool.CloneStamp or CanvasTool.Healing;

    private void NotifyRetouchTools()
    {
        OnPropertyChanged(nameof(IsCloneStampTool));
        OnPropertyChanged(nameof(IsHealingTool));
        OnPropertyChanged(nameof(IsSpotHealingTool));
        OnPropertyChanged(nameof(IsRetouchTool));
        OnPropertyChanged(nameof(UsesSourcePoint));
    }

    public IReadOnlyList<string> RetouchSampleNames { get; } = ["Current Layer", "Current & Below", "All Layers"];

    /// <summary>Clone Stamp: keep the offset between strokes (on by default, as in Photoshop).</summary>
    [ObservableProperty] public partial bool CloneAligned { get; set; } = true;

    /// <summary>Clone Stamp's Sample menu (index into <see cref="RetouchSampleNames"/>).</summary>
    [ObservableProperty] public partial int CloneSampleIndex { get; set; }

    /// <summary>Healing Brush: keep the offset between strokes (off by default, as in Photoshop).</summary>
    [ObservableProperty] public partial bool HealAligned { get; set; }

    /// <summary>Healing Brush's Sample menu.</summary>
    [ObservableProperty] public partial int HealSampleIndex { get; set; }

    /// <summary>Spot Healing Brush: look at the whole image instead of the selected layer.</summary>
    [ObservableProperty] public partial bool SpotSampleAllLayers { get; set; }

    /// <summary>The current tool's Aligned option.</summary>
    public bool RetouchAligned => Tool == CanvasTool.Healing ? HealAligned : CloneAligned;

    /// <summary>What the current retouching tool samples.</summary>
    public RetouchSample CurrentRetouchSample => Tool switch
    {
        CanvasTool.SpotHealing => SpotSampleAllLayers ? RetouchSample.AllLayers : RetouchSample.CurrentLayer,
        CanvasTool.Healing => (RetouchSample)Math.Clamp(HealSampleIndex, 0, 2),
        _ => (RetouchSample)Math.Clamp(CloneSampleIndex, 0, 2),
    };
}
