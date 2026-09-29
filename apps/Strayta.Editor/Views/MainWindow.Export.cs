using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Strayta.Rendering.Export;

namespace Strayta.Editor.Views;

// Free Transform keys and the File › Export As dialogs.
public partial class MainWindow
{
    /// <summary>Enter applies and Esc cancels an open Free Transform; arrow keys nudge it (Shift: 10 pixels).</summary>
    private void OnTransformKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || Editor.ActiveDocument?.FreeTransform is not { } transform || IsTyping) return;
        int step = e.KeyModifiers == KeyModifiers.Shift ? 10 : 1;
        switch (e.Key)
        {
            case Key.Enter or Key.Return when e.KeyModifiers == KeyModifiers.None:
                Editor.CommitTransformCommand.Execute(null);
                break;
            case Key.Escape when e.KeyModifiers == KeyModifiers.None:
                Editor.CancelTransformCommand.Execute(null);
                break;
            case Key.Left: transform.Nudge(-step, 0); break;
            case Key.Right: transform.Nudge(step, 0); break;
            case Key.Up: transform.Nudge(0, -step); break;
            case Key.Down: transform.Nudge(0, step); break;
            default: return;
        }
        e.Handled = true;
    }

    private static readonly FilePickerFileType PngFiles = new("PNG image") { Patterns = ["*.png"], MimeTypes = ["image/png"] };
    private static readonly FilePickerFileType JpegFiles = new("JPEG image") { Patterns = ["*.jpg", "*.jpeg"], MimeTypes = ["image/jpeg"] };

    public async Task<ExportOptions?> AskExportOptionsAsync(ExportOptions current, (byte R, byte G, byte B) backgroundColor)
    {
        var format = new ComboBox { ItemsSource = new[] { "PNG", "JPEG" }, SelectedIndex = current.Format == ExportFormat.Jpeg ? 1 : 0, Width = 150 };
        var quality = new Slider { Minimum = 1, Maximum = 100, Value = current.Quality, Width = 110, VerticalAlignment = VerticalAlignment.Center };
        var qualityText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 30, Margin = new Thickness(8, 0, 0, 0) };
        quality.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) qualityText.Text = $"{quality.Value:0}"; };
        qualityText.Text = $"{current.Quality}";
        var transparency = new CheckBox { Content = "Transparency", IsChecked = current.Transparency };
        bool matteIsBackground = current.Matte != (255, 255, 255) && current.Matte == backgroundColor;
        var matte = new ComboBox { ItemsSource = new[] { "White", "Background color", "Black" }, Width = 150,
            SelectedIndex = matteIsBackground ? 1 : current.Matte == (0, 0, 0) ? 2 : 0 };

        Control Row(string label, Control input) => new DockPanel
        {
            Children = { new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center }, input },
        };
        var qualityRow = Row("Quality", new StackPanel { Orientation = Orientation.Horizontal, Children = { quality, qualityText } });
        var transparencyRow = Row("", transparency);
        var matteRow = Row("Matte", matte);
        void Refresh()
        {
            bool jpeg = format.SelectedIndex == 1;
            qualityRow.IsVisible = jpeg;
            transparencyRow.IsVisible = !jpeg;
            matteRow.IsVisible = jpeg || transparency.IsChecked != true;
        }
        format.SelectionChanged += (_, _) => Refresh();
        transparency.IsCheckedChanged += (_, _) => Refresh();
        Refresh();

        var dialog = new Window
        {
            Title = "Export As",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        bool ok = false;
        var export = new Button { Content = "Export…", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        export.Click += (_, _) => { ok = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                Row("Format", format), qualityRow, transparencyRow, matteRow,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { cancel, export } },
            },
        };
        await dialog.ShowDialog(this);
        if (!ok) return null;
        return new ExportOptions
        {
            Format = format.SelectedIndex == 1 ? ExportFormat.Jpeg : ExportFormat.Png,
            Quality = (int)Math.Round(quality.Value),
            Transparency = transparency.IsChecked == true,
            Matte = matte.SelectedIndex switch { 1 => backgroundColor, 2 => ((byte)0, (byte)0, (byte)0), _ => ((byte)255, (byte)255, (byte)255) },
        };
    }

    public async Task<string?> PickExportFileAsync(string suggestedName, ExportFormat format)
    {
        bool jpeg = format == ExportFormat.Jpeg;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export As",
            SuggestedFileName = suggestedName,
            DefaultExtension = jpeg ? "jpg" : "png",
            FileTypeChoices = jpeg ? [JpegFiles, PngFiles] : [PngFiles, JpegFiles],
        });
        return file?.TryGetLocalPath();
    }
}
