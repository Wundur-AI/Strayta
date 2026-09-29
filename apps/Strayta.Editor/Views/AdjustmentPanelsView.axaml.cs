using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

public partial class AdjustmentPanelsView : UserControl
{
    public AdjustmentPanelsView() => InitializeComponent();

    /// <summary>Color Lookup's "Load 3D LUT…" item: pick a .cube or .3dl file and embed it in the layer.</summary>
    private async void OnLutChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: ColorLookupPanel panel } box || box.SelectedItem as string != ColorLookupPanel.LoadItem) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load 3D LUT",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("3D LUT files") { Patterns = ["*.cube", "*.3dl", "*.CUBE", "*.3DL"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) panel.LoadFile(path);
        panel.Refresh();
    }
}
