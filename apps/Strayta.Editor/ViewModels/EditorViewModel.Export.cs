using CommunityToolkit.Mvvm.Input;
using Strayta.Rendering.Export;

namespace Strayta.Editor.ViewModels;

// Edit › Free Transform and File › Export As.
public sealed partial class EditorViewModel
{
    /// <summary>True while the active document has a Free Transform open (the options bar shows its values).</summary>
    public bool IsTransforming => ActiveDocument?.IsTransforming == true;

    /// <summary>⌘T: transform the selected layer or group; a notice explains when that is not possible.</summary>
    [RelayCommand]
    private void FreeTransform() => ActiveDocument?.BeginFreeTransform();

    /// <summary>Enter, double-click or the ✓ button.</summary>
    [RelayCommand]
    private Task CommitTransform() => ActiveDocument?.CommitTransformAsync() ?? Task.CompletedTask;

    /// <summary>Esc or the ✕ button.</summary>
    [RelayCommand]
    private void CancelTransform() => ActiveDocument?.CancelTransform();

    /// <summary>The last export settings, offered again next time.</summary>
    public ExportOptions ExportSettings { get; private set; } = new();

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task ExportAs()
    {
        if (ActiveDocument is not { } doc) return;
        var background = (BackgroundColor.R, BackgroundColor.G, BackgroundColor.B);
        if (await _dialogs.AskExportOptionsAsync(ExportSettings, background) is not { } options) return;
        ExportSettings = options;
        string extension = options.Format == ExportFormat.Jpeg ? ".jpg" : ".png";
        if (await _dialogs.PickExportFileAsync(Path.ChangeExtension(doc.Title, extension), options.Format) is not { } path) return;
        // The file name has the last word: "photo.jpg" is a JPEG even if PNG was chosen first.
        if (ExportOptions.FormatFromPath(path) is { } format) options = options with { Format = format };
        try
        {
            await doc.ExportAsync(path, options);
            doc.Notice = $"Exported {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not export", ex.Message);
        }
    }
}
