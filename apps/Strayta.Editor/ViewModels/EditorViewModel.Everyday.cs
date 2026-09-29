using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

// Everyday tools: Eyedropper (I), Gradient and Paint Bucket (G), Zoom (Z). Their options-bar settings are app-wide,
// as in Photoshop; the work happens in DocumentViewModel.Eyedropper.cs / .Fill.cs and on the canvas.
public sealed partial class EditorViewModel
{
    public bool IsEyedropperTool { get => Tool == CanvasTool.Eyedropper; set { if (value) Tool = CanvasTool.Eyedropper; } }
    public bool IsPaintBucketTool { get => Tool == CanvasTool.PaintBucket; set { if (value) Tool = CanvasTool.PaintBucket; } }
    public bool IsGradientTool { get => Tool == CanvasTool.Gradient; set { if (value) Tool = CanvasTool.Gradient; } }
    public bool IsZoomTool { get => Tool == CanvasTool.Zoom; set { if (value) Tool = CanvasTool.Zoom; } }

    private void NotifyEverydayTools()
    {
        OnPropertyChanged(nameof(IsEyedropperTool));
        OnPropertyChanged(nameof(IsPaintBucketTool));
        OnPropertyChanged(nameof(IsGradientTool));
        OnPropertyChanged(nameof(IsZoomTool));
    }

    // ---- Eyedropper -----------------------------------------------------------------------------------

    /// <summary>Photoshop's Sample Size choices: the side of the square that is averaged (1 = the pixel alone).</summary>
    public static IReadOnlyList<(string Name, int Size)> EyedropperSizes { get; } =
    [
        ("Point Sample", 1), ("3 by 3 Average", 3), ("5 by 5 Average", 5), ("11 by 11 Average", 11),
        ("31 by 31 Average", 31), ("51 by 51 Average", 51), ("101 by 101 Average", 101),
    ];

    public IReadOnlyList<string> EyedropperSizeNames { get; } = EyedropperSizes.Select(s => s.Name).ToList();

    [ObservableProperty] public partial int EyedropperSizeIndex { get; set; }

    public int EyedropperSampleSize => EyedropperSizes[Math.Clamp(EyedropperSizeIndex, 0, EyedropperSizes.Count - 1)].Size;

    public IReadOnlyList<string> EyedropperSampleNames { get; } = ["Current Layer", "All Layers"];

    /// <summary>0 samples the selected layer, 1 the image as shown (Photoshop's default).</summary>
    [ObservableProperty] public partial int EyedropperSampleIndex { get; set; } = 1;

    public bool EyedropperSampleAllLayers => EyedropperSampleIndex != 0;

    // ---- Paint Bucket ---------------------------------------------------------------------------------

    /// <summary>How different a color may be from the clicked one and still be filled, 0..255 per channel.</summary>
    [ObservableProperty] public partial double BucketTolerance { get; set; } = 32;

    partial void OnBucketToleranceChanged(double value)
    {
        double clamped = Math.Clamp(Math.Round(value), 0, 255);
        if (clamped != value) BucketTolerance = clamped;
    }

    [ObservableProperty] public partial bool BucketAntiAlias { get; set; } = true;
    [ObservableProperty] public partial bool BucketContiguous { get; set; } = true;
    [ObservableProperty] public partial bool BucketSampleAllLayers { get; set; }

    public MagicWandOptions CurrentBucketOptions => new((int)BucketTolerance, BucketAntiAlias, BucketContiguous);

    // ---- Gradient -------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinearGradient), nameof(IsRadialGradient), nameof(IsAngleGradient), nameof(IsReflectedGradient), nameof(IsDiamondGradient))]
    public partial GradientType GradientType { get; set; } = GradientType.Linear;

    public bool IsLinearGradient { get => GradientType == GradientType.Linear; set { if (value) GradientType = GradientType.Linear; } }
    public bool IsRadialGradient { get => GradientType == GradientType.Radial; set { if (value) GradientType = GradientType.Radial; } }
    public bool IsAngleGradient { get => GradientType == GradientType.Angle; set { if (value) GradientType = GradientType.Angle; } }
    public bool IsReflectedGradient { get => GradientType == GradientType.Reflected; set { if (value) GradientType = GradientType.Reflected; } }
    public bool IsDiamondGradient { get => GradientType == GradientType.Diamond; set { if (value) GradientType = GradientType.Diamond; } }

    /// <summary>
    /// The quick choice the self-test and older code use: 0 picks Foreground to Background, 1 Foreground to Transparent
    /// (the options bar's gradient picker sets <see cref="ToolGradient"/> directly).
    /// </summary>
    public int GradientPresetIndex
    {
        get => ReferenceEquals(ToolGradient, GradientPresets.ForegroundToTransparent) ? 1 : 0;
        set => ToolGradient = value == 1 ? GradientPresets.ForegroundToTransparent : GradientPresets.ForegroundToBackground;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GradientPresetIndex))]
    public partial Core.Gradient ToolGradient { get; set; } = GradientPresets.ForegroundToBackground;

    [ObservableProperty] public partial bool GradientReverse { get; set; }

    /// <summary>On by default, as in Photoshop: long gradients show no bands.</summary>
    [ObservableProperty] public partial bool GradientDither { get; set; } = true;

    /// <summary>Gradient opacity in percent.</summary>
    [ObservableProperty] public partial double GradientOpacity { get; set; } = 100;

    /// <summary>Photoshop's Transparency option: off draws the gradient opaque, ignoring its opacity stops.</summary>
    [ObservableProperty] public partial bool GradientTransparency { get; set; } = true;

    /// <summary>The Method option (Perceptual by default, as in current Photoshop); see <see cref="Core.Painting.GradientMethod"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GradientMethodIndex))]
    public partial GradientMethod GradientMethod { get; set; } = GradientMethod.Perceptual;

    public IReadOnlyList<string> GradientMethodNames { get; } = ["Perceptual", "Linear", "Classic"];
    public int GradientMethodIndex { get => (int)GradientMethod; set { if (value >= 0) GradientMethod = (GradientMethod)value; } }

    /// <summary>The gradient's painting mode (Photoshop's Mode option).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GradientModeIndex))]
    public partial PaintMode GradientMode { get; set; } = PaintMode.Normal;

    public int GradientModeIndex { get => Controls.PaintModeNames.IndexOf(GradientMode); set { if (Controls.PaintModeNames.At(value) is { } m) GradientMode = m; } }

    /// <summary>The foreground color as a Core color, for pickers that show foreground/background stops.</summary>
    public Core.RgbColor ForegroundRgb => CurrentColor;
    public Core.RgbColor BackgroundRgb => CurrentBackgroundColor;

    partial void OnForegroundColorChanged(Avalonia.Media.Color value) => OnPropertyChanged(nameof(ForegroundRgb));
    partial void OnBackgroundColorChanged(Avalonia.Media.Color value) => OnPropertyChanged(nameof(BackgroundRgb));

    /// <summary>The gradient a drag from <paramref name="start"/> to <paramref name="end"/> draws with the current options.</summary>
    public GradientSpec GradientFor(System.Numerics.Vector2 start, System.Numerics.Vector2 end)
    {
        var fg = CurrentColor;
        var resolved = ToolGradient.Resolve(fg, CurrentBackgroundColor);
        // The spec's table is built once per gradient; keep the same resolved instance while nothing changed.
        if (_resolvedGradient is { } cached && ReferenceEquals(cached.Source, ToolGradient) && cached.Resolved == resolved) resolved = cached.Resolved;
        else _resolvedGradient = (ToolGradient, resolved);
        return new GradientSpec(GradientType, start, end, fg, 1f, fg, 1f)
        {
            Stops = resolved,
            Method = GradientMethod,
            Transparency = GradientTransparency,
            Mode = GradientMode,
            Reverse = GradientReverse,
            Dither = GradientDither,
            Opacity = (float)Math.Clamp(GradientOpacity / 100, 0, 1),
        };
    }

    private (Core.Gradient Source, Core.Gradient Resolved)? _resolvedGradient;

    // ---- Zoom -----------------------------------------------------------------------------------------

    /// <summary>View › Zoom In / Zoom Out (⌘+ / ⌘−): the next zoom step, around the center of the view.</summary>
    [RelayCommand] private void ZoomIn() => ActiveDocument?.RequestZoom("in");
    [RelayCommand] private void ZoomOut() => ActiveDocument?.RequestZoom("out");

    /// <summary>
    /// A double-click on a tool-strip slot, with Photoshop's shortcuts: the Zoom tool shows 100%, the Hand tool fits the
    /// image on screen.
    /// </summary>
    public void ToolSlotDoubleClicked(CanvasTool tool)
    {
        if (tool == CanvasTool.Zoom) ActualSizeCommand.Execute(null);
        else if (tool == CanvasTool.Hand) FitCommand.Execute(null);
    }
}
