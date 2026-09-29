using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core.Painting;
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
        OnPropertyChanged(nameof(ShowsBrushOptions));
        ActiveDocument?.ActiveCloneSource.Refresh(); // the Clone Stamp and Healing Brush keep separate sources
    }

    /// <summary>Tools with Flow, Airbrush, Smoothing and the Brush Settings popover: Brush, Eraser, Clone Stamp.</summary>
    public bool ShowsBrushOptions => Tool is CanvasTool.Brush or CanvasTool.Eraser or CanvasTool.CloneStamp;

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

    // ---- Healing options ---------------------------------------------------------------------------------

    /// <summary>The healing tools' Mode menu (index = <see cref="HealMode"/> value).</summary>
    public IReadOnlyList<string> HealModeNames { get; } = ["Normal", "Replace", "Multiply", "Screen", "Darken", "Lighten", "Color", "Luminosity"];

    /// <summary>Healing Brush's Mode.</summary>
    [ObservableProperty] public partial int HealModeIndex { get; set; }

    /// <summary>Spot Healing Brush's Mode.</summary>
    [ObservableProperty] public partial int SpotModeIndex { get; set; }

    /// <summary>Diffusion, 1..7 (Photoshop's default 5): how far the surrounding colors reach into a heal (HealOptions).</summary>
    [ObservableProperty] public partial double HealDiffusion { get; set; } = 5;

    /// <summary>Heal with a gain instead of an offset (log domain), for strong shading changes between source and destination.</summary>
    [ObservableProperty] public partial bool HealMultiplicative { get; set; }

    /// <summary>Spot Healing's Type menu, in Photoshop's order.</summary>
    public IReadOnlyList<string> SpotTypeNames { get; } = ["Content-Aware", "Create Texture", "Proximity Match"];

    /// <summary>Spot Healing's Type (index into <see cref="SpotTypeNames"/>); Content-Aware by default, as in Photoshop.</summary>
    [ObservableProperty] public partial int SpotTypeIndex { get; set; }

    /// <summary>The current healing tool's mode.</summary>
    public HealMode CurrentHealMode => (HealMode)Math.Clamp(Tool == CanvasTool.SpotHealing ? SpotModeIndex : HealModeIndex, 0, HealModeNames.Count - 1);

    /// <summary>The heal settings for a brush of <paramref name="size"/> pixels.</summary>
    public HealOptions CurrentHealOptions(float size) => new()
    {
        Diffusion = (int)Math.Clamp(Math.Round(HealDiffusion), 1, 7),
        Multiplicative = HealMultiplicative,
        BrushSize = size,
    };

    // ---- Clone Source overlay (Window › Clone Source) ---------------------------------------------------

    /// <summary>Show the source under the brush before painting (and while painting unless Auto Hide).</summary>
    [ObservableProperty] public partial bool CloneOverlayShow { get; set; } = true;

    /// <summary>The overlay's opacity in percent.</summary>
    [ObservableProperty] public partial double CloneOverlayOpacity { get; set; } = 100;

    /// <summary>Clip the overlay to the brush tip; off shows the whole sampled image moved into place.</summary>
    [ObservableProperty] public partial bool CloneOverlayClipped { get; set; } = true;

    /// <summary>Hide the overlay while painting (the stroke itself shows the clone).</summary>
    [ObservableProperty] public partial bool CloneOverlayAutoHide { get; set; } = true;

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
