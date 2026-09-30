using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Window › Brushes and the brush presets' dialogs (names, Import Brushes… file choice); STRAYTA_TONEBENCH.
public partial class MainWindow
{
    private void AddBrushMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        BrushPresetsViewModel.AskName = AskNameAsync; // MainWindow.Paths.cs
        BrushPresetsViewModel.PickBrushFiles = PickBrushFilesAsync;
        if (menu.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Edit")?.Menu is { } edit)
        {
            // Photoshop's order: Define Brush Preset…, then Define Pattern….
            var pattern = edit.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header?.StartsWith("Define Pattern", StringComparison.Ordinal) == true);
            int at = pattern is null ? edit.Items.Count : edit.Items.IndexOf(pattern);
            edit.Items.Insert(at, item("Define Brush Preset…", Editor.BrushPresets.DefineFromSelectionCommand, null, null));
        }
        if (menu.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Window")?.Menu is { } window)
        {
            var history = window.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "History");
            int at = history is null ? 0 : window.Items.IndexOf(history);
            window.Items.Insert(at, item("Brushes", Editor.ShowPanelCommand, null, "Brushes"));
        }
        if (Environment.GetEnvironmentVariable("STRAYTA_TONEBENCH") == "new")
            Opened += async (_, _) => await SelfTest.RunToningBenchmarkAsync(Editor); // SelfTest.Toning.cs
    }

    private async Task<IReadOnlyList<string>> PickBrushFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Brushes",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Brushes") { Patterns = ["*.abr"] }, FilePickerFileTypes.All],
        });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }
}
