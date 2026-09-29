using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;

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

    /// <summary>Photoshop's Sample menu, in its order (<see cref="LayerSample"/>; EditorViewModel.Sampling.cs).</summary>
    public IReadOnlyList<string> EyedropperSampleNames { get; } = LayerSampleNames;

    /// <summary>What the Eyedropper samples, a <see cref="LayerSample"/>: All Layers (the image as shown) by default, as in Photoshop.</summary>
    [ObservableProperty] public partial int EyedropperSampleIndex { get; set; } = (int)LayerSample.AllLayers;

    public LayerSample EyedropperSample => (LayerSample)Math.Clamp(EyedropperSampleIndex, 0, LayerSampleNames.Count - 1);

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

    public IReadOnlyList<string> GradientPresetNames { get; } = ["Foreground to Background", "Foreground to Transparent"];

    /// <summary>0: foreground to background color; 1: foreground color fading to transparent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GradientPreviewBrush))]
    public partial int GradientPresetIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GradientPreviewBrush))]
    public partial bool GradientReverse { get; set; }

    /// <summary>On by default, as in Photoshop: long gradients show no bands.</summary>
    [ObservableProperty] public partial bool GradientDither { get; set; } = true;

    /// <summary>Gradient opacity in percent.</summary>
    [ObservableProperty] public partial double GradientOpacity { get; set; } = 100;

    partial void OnForegroundColorChanged(Avalonia.Media.Color value) => OnPropertyChanged(nameof(GradientPreviewBrush));
    partial void OnBackgroundColorChanged(Avalonia.Media.Color value) => OnPropertyChanged(nameof(GradientPreviewBrush));

    /// <summary>The options bar's picture of the current preset with today's colors.</summary>
    public Avalonia.Media.IBrush GradientPreviewBrush
    {
        get
        {
            var from = ForegroundColor;
            var to = GradientPresetIndex == 1 ? Avalonia.Media.Color.FromArgb(0, from.R, from.G, from.B) : BackgroundColor;
            if (GradientReverse) (from, to) = (to, from);
            return new Avalonia.Media.LinearGradientBrush
            {
                StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
                EndPoint = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative),
                GradientStops = { new Avalonia.Media.GradientStop(from, 0), new Avalonia.Media.GradientStop(to, 1) },
            };
        }
    }

    /// <summary>The gradient a drag from <paramref name="start"/> to <paramref name="end"/> draws with the current options.</summary>
    public GradientSpec GradientFor(System.Numerics.Vector2 start, System.Numerics.Vector2 end)
    {
        var fg = CurrentColor;
        bool transparent = GradientPresetIndex == 1;
        return new GradientSpec(GradientType, start, end, fg, 1f, transparent ? fg : CurrentBackgroundColor, transparent ? 0f : 1f)
        {
            Reverse = GradientReverse,
            Dither = GradientDither,
            Opacity = (float)Math.Clamp(GradientOpacity / 100, 0, 1),
        };
    }

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
