using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core.Painting;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

// Brush options beyond size, hardness and opacity (Photoshop's options bar and Brush Settings): Mode, Flow, Airbrush,
// Smoothing, pen pressure for size and opacity, and the tip's Spacing, Angle and Roundness. They are app-wide, as the
// brush is; the Brush, Eraser and Clone Stamp share them (the Eraser ignores Mode, the Clone Stamp has its own Mode).
// DocumentViewModel.Brush.cs applies them to strokes (smoothing, pressure, airbrush build-up).
public sealed partial class EditorViewModel
{
    /// <summary>Photoshop's painting Mode menu, in its order (index = <see cref="PaintMode"/> value).</summary>
    public IReadOnlyList<string> PaintModeNames { get; } =
    [
        "Normal", "Dissolve", "Behind", "Clear",
        "Darken", "Multiply", "Color Burn", "Linear Burn", "Darker Color",
        "Lighten", "Screen", "Color Dodge", "Linear Dodge (Add)", "Lighter Color",
        "Overlay", "Soft Light", "Hard Light", "Vivid Light", "Linear Light", "Pin Light", "Hard Mix",
        "Difference", "Exclusion", "Subtract", "Divide",
        "Hue", "Saturation", "Color", "Luminosity",
    ];

    /// <summary>The Brush's Mode (index into <see cref="PaintModeNames"/>).</summary>
    [ObservableProperty] public partial int BrushModeIndex { get; set; }

    /// <summary>The Clone Stamp's Mode (Photoshop keeps one per tool).</summary>
    [ObservableProperty] public partial int CloneModeIndex { get; set; }

    /// <summary>Flow in percent: how much each dab adds; overlapping dabs build up toward the opacity.</summary>
    [ObservableProperty] public partial double BrushFlow { get; set; } = 100;

    /// <summary>Airbrush: keeps building up while the pointer rests with the button held.</summary>
    [ObservableProperty] public partial bool BrushAirbrush { get; set; }

    /// <summary>Smoothing in percent: the brush trails the pointer on a string this long (lazy mouse), so strokes come out steadier.</summary>
    [ObservableProperty] public partial double BrushSmoothing { get; set; }

    /// <summary>Spacing between dabs in percent of the diameter.</summary>
    [ObservableProperty] public partial double BrushSpacing { get; set; } = 25;

    /// <summary>Tip angle in degrees (counter-clockwise).</summary>
    [ObservableProperty] public partial double BrushAngle { get; set; }

    /// <summary>Tip roundness in percent (100 is round).</summary>
    [ObservableProperty] public partial double BrushRoundness { get; set; } = 100;

    /// <summary>Pen pressure controls size.</summary>
    [ObservableProperty] public partial bool BrushPressureSize { get; set; }

    /// <summary>Pen pressure controls opacity.</summary>
    [ObservableProperty] public partial bool BrushPressureOpacity { get; set; }

    /// <summary>The tool's paint mode: the Brush's or the Clone Stamp's (the Eraser and healing tools use their own).</summary>
    public PaintMode CurrentPaintMode => (PaintMode)Math.Clamp(Tool == CanvasTool.CloneStamp ? CloneModeIndex : BrushModeIndex, 0, PaintModeNames.Count - 1);

    /// <summary>The options bar's settings applied to a size/hardness/opacity brush.</summary>
    private BrushSettings WithBrushOptions(BrushSettings brush) => brush with
    {
        Flow = (float)Math.Clamp(BrushFlow / 100, 0.01, 1),
        SpacingPercent = (float)Math.Clamp(BrushSpacing, 1, 1000),
        Angle = (float)BrushAngle,
        Roundness = (float)Math.Clamp(BrushRoundness / 100, 0.01, 1),
        Mode = CurrentPaintMode,
        PressureSize = BrushPressureSize,
        PressureOpacity = BrushPressureOpacity,
    };
}
