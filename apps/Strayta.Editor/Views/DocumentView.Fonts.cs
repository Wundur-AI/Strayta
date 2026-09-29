using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Strayta.Editor.Views;

// The missing-fonts banner: search links, installing font files, and checking again.
public partial class DocumentView
{
    private async void OnFindFont(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: Uri uri } && TopLevel.GetTopLevel(this) is { } top) await top.Launcher.LaunchUriAsync(uri);
    }

    private async void OnInstallFonts(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install Font Files",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Fonts") { Patterns = ["*.ttf", "*.otf", "*.ttc"] }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) await vm.InstallFontsAsync(paths);
    }

    private async void OnRecheckFonts(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm) await vm.RecheckFontsAsync();
    }
}
