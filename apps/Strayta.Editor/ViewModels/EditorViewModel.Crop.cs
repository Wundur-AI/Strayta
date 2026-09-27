using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Editor.Controls;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

/// <summary>Image › Image Size settings: pixel dimensions, resolution (pixels per inch) and resampling.</summary>
public sealed record ImageSizeRequest(int Width, int Height, double Resolution, ResampleMethod Method);

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
}

// Crop tool options (ratio presets, Straighten, Delete Cropped Pixels) and the Image menu.
public sealed partial class EditorViewModel
{
    public bool IsCropTool => Tool == CanvasTool.Crop;

    /// <summary>Photoshop's crop ratio presets, in the options bar's order.</summary>
    public IReadOnlyList<string> CropRatioNames { get; } = ["Ratio", "1:1 (Square)", "4:5 (8:10)", "16:9", "Original Ratio"];

    [ObservableProperty] public partial int CropRatioIndex { get; set; }

    /// <summary>Off, layers keep the pixels outside the crop (the canvas just gets smaller), as in Photoshop.</summary>
    [ObservableProperty] public partial bool CropDeletePixels { get; set; } = true;

    /// <summary>The Straighten tool of the crop options bar: the next drag draws a line to level.</summary>
    [ObservableProperty] public partial bool CropStraighten { get; set; }

    /// <summary>Width over height for the selected preset, or null for a free box.</summary>
    public double? CropAspectRatio(Strayta.Core.Document doc) => CropRatioIndex switch
    {
        1 => 1,
        2 => 4 / 5.0,
        3 => 16 / 9.0,
        4 => doc.Width / (double)doc.Height,
        _ => null,
    };

    partial void OnCropRatioIndexChanged(int value)
    {
        if (ActiveDocument is { CropBox: { } box } doc) box.SetAspectRatio(CropAspectRatio(doc.Model));
    }

    /// <summary>Called when the tool or the active document changes: the crop box appears or goes away.</summary>
    private void SyncCropTool()
    {
        OnPropertyChanged(nameof(IsCropTool));
        if (Tool != CanvasTool.Crop) CropStraighten = false;
        ActiveDocument?.SyncCropSession();
    }

    [RelayCommand] private Task CommitCrop() => ActiveDocument?.CommitCropAsync() ?? Task.CompletedTask;
    [RelayCommand] private void CancelCrop() => ActiveDocument?.CancelCrop();
    [RelayCommand] private void SwapCropRatio() => ActiveDocument?.CropBox?.SwapOrientation();

    [RelayCommand]
    private async Task ImageSize()
    {
        if (ActiveDocument is not { } doc || _dialogs is not ICanvasDialogs dialogs) return;
        var m = doc.Model;
        if (await dialogs.AskImageSizeAsync(m.Width, m.Height, m.Resolution, LastResampleMethod) is not { } r) return;
        LastResampleMethod = r.Method;
        await doc.ResizeImageAsync(r.Width, r.Height, r.Method, r.Resolution);
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
