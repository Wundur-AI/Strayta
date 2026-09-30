using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core.Painting;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

// The current brush's tip and dynamics beyond size / hardness / spacing / angle / roundness (EditorViewModel.Brush.cs), and
// brush presets: picking one (Brushes panel, the options bar's brush picker) sets the brush; New Brush Preset saves it.
// Shape Dynamics and Scattering are set in the Brush Settings popover (BrushToolOptions).
public sealed partial class EditorViewModel
{
    private BrushPresetsViewModel? _brushPresets;

    /// <summary>The presets with their thumbnails, shared by the Brushes panel and the options-bar picker.</summary>
    public BrushPresetsViewModel BrushPresets => _brushPresets ??= new BrushPresetsViewModel(this);

    /// <summary>The sampled tip the brush paints with, or null for the computed round tip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSampledTip))]
    public partial BrushTip? BrushTip { get; set; }

    public bool HasSampledTip => BrushTip is not null;

    /// <summary>The name of the preset the brush came from (shown in the picker).</summary>
    [ObservableProperty] public partial string BrushPresetName { get; set; } = "Round";

    /// <summary>Tools whose options bar has the brush preset picker.</summary>
    public bool ShowsBrushPresetPicker => IsPaintTool || IsRetouchTool || IsToneOrFocusTool;

    /// <summary>Sets the brush from a preset (its tip, size, hardness, spacing, angle, roundness, pressure and dynamics).</summary>
    public void ApplyBrushPreset(BrushPreset preset)
    {
        BrushTip = preset.Tip;
        BrushSize = preset.Size;
        BrushHardness = preset.Hardness * 100;
        BrushSpacing = preset.Spacing;
        BrushAngle = preset.Angle;
        BrushRoundness = preset.Roundness * 100;
        BrushPressureSize = preset.PressureSize;
        BrushPressureOpacity = preset.PressureOpacity;
        var d = preset.Dynamics;
        ShapeDynamics = d is not null && (d.SizeJitter > 0 || d.SizeControl != DynamicsControl.Off || d.AngleJitter > 0 || d.AngleControl != DynamicsControl.Off || d.RoundnessJitter > 0);
        SizeJitter = (d?.SizeJitter ?? 0) * 100;
        SizeControlIndex = (int)(d?.SizeControl ?? DynamicsControl.Off);
        MinimumDiameter = (d?.MinimumDiameter ?? 0) * 100;
        AngleJitter = (d?.AngleJitter ?? 0) * 100;
        AngleControlIndex = d?.AngleControl == DynamicsControl.Direction ? 1 : 0;
        RoundnessJitter = (d?.RoundnessJitter ?? 0) * 100;
        Scattering = d is not null && (d.Scatter > 0 || d.Count > 1);
        Scatter = (d?.Scatter ?? 0) * 100;
        ScatterBothAxes = d?.BothAxes ?? false;
        ScatterCount = d?.Count ?? 1;
        ScatterCountJitter = (d?.CountJitter ?? 0) * 100;
        BrushPresetName = preset.Name;
    }

    /// <summary>The current brush as a new preset (New Brush Preset).</summary>
    public BrushPreset CurrentBrushAsPreset(string name, string folder) =>
        new(name, folder, (float)BrushSize, (float)(BrushHardness / 100), (float)BrushSpacing, (float)BrushAngle, (float)(BrushRoundness / 100), BrushTip)
        {
            PressureSize = BrushPressureSize,
            PressureOpacity = BrushPressureOpacity,
            Dynamics = CurrentDynamics(seed: 1),
        };

    // ---- Shape Dynamics and Scattering ---------------------------------------------------------------------

    public IReadOnlyList<string> SizeControlNames { get; } = ["Off", "Pen Pressure"];
    public IReadOnlyList<string> AngleControlNames { get; } = ["Off", "Direction"];

    [ObservableProperty] public partial bool ShapeDynamics { get; set; }

    /// <summary>Size jitter in percent.</summary>
    [ObservableProperty] public partial double SizeJitter { get; set; }

    [ObservableProperty] public partial int SizeControlIndex { get; set; }

    /// <summary>Minimum diameter in percent of the size.</summary>
    [ObservableProperty] public partial double MinimumDiameter { get; set; }

    /// <summary>Angle jitter in percent (100% = a full turn either way).</summary>
    [ObservableProperty] public partial double AngleJitter { get; set; }

    [ObservableProperty] public partial int AngleControlIndex { get; set; }

    [ObservableProperty] public partial double RoundnessJitter { get; set; }

    [ObservableProperty] public partial bool Scattering { get; set; }

    /// <summary>Scatter in percent of the diameter (0–1000).</summary>
    [ObservableProperty] public partial double Scatter { get; set; }

    [ObservableProperty] public partial bool ScatterBothAxes { get; set; }

    [ObservableProperty] public partial double ScatterCount { get; set; } = 1;

    [ObservableProperty] public partial double ScatterCountJitter { get; set; }

    private int _dynamicsSeed = 1;

    /// <summary>The dynamics a new stroke uses (a new random sequence each stroke), or null when both sections are off.</summary>
    private BrushDynamics? CurrentDynamics(int? seed = null)
    {
        if (!ShapeDynamics && !Scattering) return null;
        var d = new BrushDynamics
        {
            Seed = seed ?? unchecked(_dynamicsSeed = _dynamicsSeed * 1103515245 + 12345),
        };
        if (ShapeDynamics)
            d = d with
            {
                SizeJitter = (float)Math.Clamp(SizeJitter / 100, 0, 1),
                SizeControl = SizeControlIndex == 1 ? DynamicsControl.PenPressure : DynamicsControl.Off,
                MinimumDiameter = (float)Math.Clamp(MinimumDiameter / 100, 0, 1),
                AngleJitter = (float)Math.Clamp(AngleJitter / 100, 0, 1),
                AngleControl = AngleControlIndex == 1 ? DynamicsControl.Direction : DynamicsControl.Off,
                RoundnessJitter = (float)Math.Clamp(RoundnessJitter / 100, 0, 1),
            };
        if (Scattering)
            d = d with
            {
                Scatter = (float)Math.Clamp(Scatter / 100, 0, 10),
                BothAxes = ScatterBothAxes,
                Count = (int)Math.Clamp(Math.Round(ScatterCount), 1, 16),
                CountJitter = (float)Math.Clamp(ScatterCountJitter / 100, 0, 1),
            };
        return d.IsEmpty ? null : d;
    }
}
