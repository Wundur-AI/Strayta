using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Editor.Controls;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// Image › Image Size settings: pixel dimensions, resolution (pixels per inch), resampling, and Scale Styles (layer
/// effects grow or shrink with the image).
/// </summary>
public sealed record ImageSizeRequest(int Width, int Height, double Resolution, ResampleMethod Method, bool ScaleStyles = true);

/// <summary>The answer to "Crop the image?" when leaving the Crop tool with a changed box.</summary>
public enum CropPromptChoice
{
    Crop,
    DontCrop,
    Cancel,
}

/// <summary>The Crop tool's composition overlays, in the options bar's order (O cycles through them).</summary>
public enum CropOverlay
{
    RuleOfThirds,
    Grid,
    Diagonal,
    Triangle,
    GoldenRatio,
    GoldenSpiral,
}

/// <summary>Image › Canvas Size settings: the new size, where the old image sits (-1, 0 or 1 per axis) and the new area's color.</summary>
public sealed record CanvasSizeRequest(int Width, int Height, int AnchorX, int AnchorY, Color Extension);

/// <summary>Image › Trim settings.</summary>
public sealed record TrimRequest(TrimBasis Basis, bool Top, bool Left, bool Bottom, bool Right);

/// <summary>The Image menu's dialogs; the main window implements them (MainWindow.Crop.cs).</summary>
public interface ICanvasDialogs
{
    Task<ImageSizeRequest?> AskImageSizeAsync(int width, int height, double resolution, ResampleMethod method);
    Task<CanvasSizeRequest?> AskCanvasSizeAsync(int width, int height, Color foreground, Color background, bool hasBackgroundLayer);
    Task<TrimRequest?> AskTrimAsync();

    /// <summary>"Crop the image?" when switching away from the Crop tool with a changed box.</summary>
    Task<CropPromptChoice> AskApplyCropAsync();
}

// Crop tool options (ratio presets, W × H × Resolution, overlays, Straighten, Delete Cropped Pixels, Content-Aware)
// and the Image menu.
public sealed partial class EditorViewModel
{
    /// <summary>The Crop or Perspective Crop tool (they share the options bar's place).</summary>
    public bool IsCropTool => Tool is CanvasTool.Crop or CanvasTool.PerspectiveCrop;

    public bool IsRectCropTool => Tool == CanvasTool.Crop;

    public bool IsPerspectiveCropTool => Tool == CanvasTool.PerspectiveCrop;

    /// <summary>Photoshop's crop presets, in the options bar's order.</summary>
    public IReadOnlyList<string> CropRatioNames { get; } = ["Ratio", "W x H x Resolution", "Original Ratio", "1:1 (Square)", "4:5 (8:10)", "16:9"];

    /// <summary>Index of the "W x H x Resolution" preset in <see cref="CropRatioNames"/>.</summary>
    public const int CropSizePreset = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCropSizeMode))]
    public partial int CropRatioIndex { get; set; }

    /// <summary>The "W x H x Resolution" preset: the crop is resampled to exactly these pixels (and resolution).</summary>
    public bool IsCropSizeMode => CropRatioIndex == CropSizePreset;

    /// <summary>W × H × Resolution: target width in pixels (empty for none).</summary>
    [ObservableProperty] public partial decimal? CropTargetWidth { get; set; }

    /// <summary>W × H × Resolution: target height in pixels.</summary>
    [ObservableProperty] public partial decimal? CropTargetHeight { get; set; }

    /// <summary>W × H × Resolution: the resolution (pixels per inch) the cropped document gets.</summary>
    [ObservableProperty] public partial decimal? CropTargetResolution { get; set; }

    /// <summary>The composition guide drawn in the crop box.</summary>
    [ObservableProperty] public partial CropOverlay CropOverlay { get; set; }

    /// <summary>Turns the Triangle and Golden Spiral overlays (Shift+O): 0..3 quarter flips.</summary>
    [ObservableProperty] public partial int CropOverlayOrientation { get; set; }

    public IReadOnlyList<string> CropOverlayNames { get; } = ["Rule of Thirds", "Grid", "Diagonal", "Triangle", "Golden Ratio", "Golden Spiral"];

    public int CropOverlayIndex
    {
        get => (int)CropOverlay;
        set => CropOverlay = (CropOverlay)Math.Clamp(value, 0, CropOverlayNames.Count - 1);
    }

    partial void OnCropOverlayChanged(CropOverlay value) => OnPropertyChanged(nameof(CropOverlayIndex));

    /// <summary>O: the next overlay; Shift+O: turns the Triangle or Golden Spiral.</summary>
    public void CycleCropOverlay(bool orientation)
    {
        if (orientation) CropOverlayOrientation = (CropOverlayOrientation + 1) % 4;
        else CropOverlay = (CropOverlay)(((int)CropOverlay + 1) % CropOverlayNames.Count);
    }

    /// <summary>
    /// Content-Aware: new area a crop adds beyond the image is filled from the image around it instead of the
    /// background color. Available once <see cref="DocumentViewModel.ContentAwareCropFill"/> is provided.
    /// </summary>
    [ObservableProperty] public partial bool CropContentAware { get; set; }

    public bool CanCropContentAware => DocumentViewModel.ContentAwareCropFill is not null;

    public string CropContentAwareTip => CanCropContentAware
        ? "Fill the area a crop adds beyond the image from its surroundings"
        : "Content-Aware filling is not available yet";

    /// <summary>Asks "Crop the image?"; replaced by the self-test, which cannot click dialogs.</summary>
    internal Func<Task<CropPromptChoice>> AskApplyCrop =>
        CropPromptOverride ?? (() => _dialogs is ICanvasDialogs d ? d.AskApplyCropAsync() : Task.FromResult(CropPromptChoice.Crop));

    internal Func<Task<CropPromptChoice>>? CropPromptOverride { get; set; }

    /// <summary>The W × H × Resolution target (pixels and optional ppi), or null when the fields are not both filled.</summary>
    public (int Width, int Height, double? Resolution)? CropTargetSize => IsCropSizeMode && CropTargetWidth is > 0 && CropTargetHeight is > 0
        ? ((int)Math.Round(CropTargetWidth.Value), (int)Math.Round(CropTargetHeight.Value), CropTargetResolution is > 0 ? (double)CropTargetResolution.Value : null)
        : null;

    partial void OnCropTargetWidthChanged(decimal? value) => ApplyCropRatio();
    partial void OnCropTargetHeightChanged(decimal? value) => ApplyCropRatio();

    private void ApplyCropRatio()
    {
        if (!_swappingCropSize && ActiveDocument is { CropBox: { } box } doc) box.SetAspectRatio(CropAspectRatio(doc.Model));
    }

    /// <summary>Off, layers keep the pixels outside the crop (the canvas just gets smaller), as in Photoshop.</summary>
    [ObservableProperty] public partial bool CropDeletePixels { get; set; } = true;

    /// <summary>The Straighten tool of the crop options bar: the next drag draws a line to level.</summary>
    [ObservableProperty] public partial bool CropStraighten { get; set; }

    /// <summary>Width over height for the selected preset, or null for a free box.</summary>
    public double? CropAspectRatio(Strayta.Core.Document doc) => CropRatioIndex switch
    {
        CropSizePreset => CropTargetSize is { } t ? t.Width / (double)t.Height : null,
        2 => doc.Width / (double)doc.Height,
        3 => 1,
        4 => 4 / 5.0,
        5 => 16 / 9.0,
        _ => null,
    };

    partial void OnCropRatioIndexChanged(int value)
    {
        if (value == CropSizePreset && CropTargetWidth is null && ActiveDocument is { } d)
        {
            // Photoshop starts the fields from the image: its size and resolution.
            CropTargetWidth = d.Model.Width;
            CropTargetHeight = d.Model.Height;
            CropTargetResolution = (decimal)d.Model.Resolution;
        }
        ApplyCropRatio();
    }

    /// <summary>Clears the W × H × Resolution fields (the options bar's Clear).</summary>
    [RelayCommand]
    private void ClearCropSize()
    {
        CropTargetWidth = CropTargetHeight = CropTargetResolution = null;
        ApplyCropRatio();
    }

    /// <summary>Called when the tool or the active document changes: the crop box appears or goes away.</summary>
    private void SyncCropTool()
    {
        OnPropertyChanged(nameof(IsCropTool));
        OnPropertyChanged(nameof(IsRectCropTool));
        OnPropertyChanged(nameof(IsPerspectiveCropTool));
        if (Tool != CanvasTool.Crop) CropStraighten = false;
        ActiveDocument?.SyncCropSession();
        ActiveDocument?.SyncPerspectiveCropSession(); // DocumentViewModel.PerspectiveCrop.cs
    }

    [RelayCommand] private Task CommitPerspectiveCrop() => ActiveDocument?.CommitPerspectiveCropAsync() ?? Task.CompletedTask;
    [RelayCommand] private void CancelPerspectiveCrop() => ActiveDocument?.CancelPerspectiveCrop();

    [RelayCommand] private Task CommitCrop() => ActiveDocument?.CommitCropAsync() ?? Task.CompletedTask;
    [RelayCommand] private void CancelCrop() => ActiveDocument?.CancelCrop();
    [RelayCommand]
    private void SwapCropRatio()
    {
        if (IsCropSizeMode && CropTargetWidth is not null && CropTargetHeight is not null)
        {
            _swappingCropSize = true; // the box turns once below, not once per field
            (CropTargetWidth, CropTargetHeight) = (CropTargetHeight, CropTargetWidth);
            _swappingCropSize = false;
        }
        ActiveDocument?.CropBox?.SwapOrientation();
    }

    private bool _swappingCropSize;

    [RelayCommand]
    private async Task ImageSize()
    {
        if (ActiveDocument is not { } doc || _dialogs is not ICanvasDialogs dialogs) return;
        var m = doc.Model;
        if (await dialogs.AskImageSizeAsync(m.Width, m.Height, m.Resolution, LastResampleMethod) is not { } r) return;
        LastResampleMethod = r.Method;
        await doc.ResizeImageAsync(r.Width, r.Height, r.Method, r.Resolution, r.ScaleStyles);
    }

    /// <summary>The resampling method Image Size used last, offered again next time.</summary>
    public ResampleMethod LastResampleMethod { get; private set; } = ResampleMethod.Bicubic;

    [RelayCommand]
    private async Task CanvasSize()
    {
        if (ActiveDocument is not { } doc || _dialogs is not ICanvasDialogs dialogs) return;
        var m = doc.Model;
        bool background = CanvasOperations.FindBackground(m) is not null;
        if (await dialogs.AskCanvasSizeAsync(m.Width, m.Height, ForegroundColor, BackgroundColor, background) is not { } r) return;
        int ox = (r.AnchorX + 1) * (r.Width - m.Width) / 2, oy = (r.AnchorY + 1) * (r.Height - m.Height) / 2;
        await doc.ResizeCanvasAsync(r.Width, r.Height, ox, oy, r.Extension);
    }

    [RelayCommand] private Task CropToSelection() => ActiveDocument?.CropToSelectionAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private async Task Trim()
    {
        if (ActiveDocument is not { } doc || _dialogs is not ICanvasDialogs dialogs) return;
        if (await dialogs.AskTrimAsync() is not { } r) return;
        await doc.TrimAsync(r.Basis, r.Top, r.Left, r.Bottom, r.Right);
    }
}
