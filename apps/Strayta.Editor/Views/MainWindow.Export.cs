using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Strayta.Editor.ViewModels;
using Strayta.Rendering.Export;

namespace Strayta.Editor.Views;

// Free Transform keys, the File › Export and Generate menus, and the export dialogs (Export As, Layers to Files).
public partial class MainWindow : IExportDialogs
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

    /// <summary>
    /// File › Export (Quick Export as PNG ⇧⌘', Export As… ⌥⇧⌘W, Layers to Files…, Save a Copy…) replacing the plain
    /// Export As item, File › Generate › Image Assets (a check mark per document), and Layer › Export As….
    /// </summary>
    private void AddExportMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var file = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "File").Menu!;
        var exportAs = file.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Export As…");
        int at = exportAs is null ? file.Items.Count : file.Items.IndexOf(exportAs);
        if (exportAs is not null) file.Items.Remove(exportAs);
        exportAs ??= item("Export As…", Editor.ExportAsCommand, new KeyGesture(Key.W, cmd | KeyModifiers.Shift | KeyModifiers.Alt), null);

        var export = new NativeMenu
        {
            item("Quick Export as PNG", Editor.QuickExportPngCommand, new KeyGesture(Key.OemQuotes, cmd | KeyModifiers.Shift), null),
            exportAs,
            new NativeMenuItemSeparator(),
            item("Layers to Files…", Editor.ExportLayersToFilesCommand, null, null),
            item("Save a Copy…", Editor.SaveACopyCommand, null, null),
        };
        var assets = item("Image Assets", Editor.ToggleImageAssetsCommand, null, null);
        assets.ToggleType = MenuItemToggleType.CheckBox;
        void RefreshAssets() => assets.IsChecked = Editor.ActiveDocument?.GeneratesImageAssets == true;
        Editor.DocumentChanged += RefreshAssets;
        Editor.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EditorViewModel.ActiveDocument)) RefreshAssets(); };

        file.Items.Insert(at, new NativeMenuItem("Export") { Menu = export });
        file.Items.Insert(at + 1, new NativeMenuItem("Generate") { Menu = new NativeMenu { assets } });

        var layer = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Layer").Menu!;
        layer.Items.Add(new NativeMenuItemSeparator());
        layer.Items.Add(item("Export As…", Editor.ExportLayerAsCommand, null, null));
    }

    // ---- IExportDialogs ------------------------------------------------------------------------------

    private static readonly FilePickerFileType PngFiles = new("PNG image") { Patterns = ["*.png"], MimeTypes = ["image/png"] };
    private static readonly FilePickerFileType JpegFiles = new("JPEG image") { Patterns = ["*.jpg", "*.jpeg"], MimeTypes = ["image/jpeg"] };
    private static readonly FilePickerFileType GifFiles = new("GIF image") { Patterns = ["*.gif"], MimeTypes = ["image/gif"] };
    private static readonly FilePickerFileType WebPFiles = new("WebP image") { Patterns = ["*.webp"], MimeTypes = ["image/webp"] };

    private static FilePickerFileType TypeOf(ExportFormat format) => format switch
    {
        ExportFormat.Jpeg => JpegFiles,
        ExportFormat.Gif => GifFiles,
        ExportFormat.WebP => WebPFiles,
        _ => PngFiles,
    };

    public async Task<bool> ShowExportAsAsync(ExportAsViewModel model) =>
        await new ExportAsWindow(model).ShowDialog<bool?>(this) == true;

    private async Task<IStorageFolder?> FolderAsync(string? path) =>
        path is not null && Directory.Exists(path) ? await StorageProvider.TryGetFolderFromPathAsync(path) : null;

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await FolderAsync(startFolder),
        });
        return folders is [var f] ? f.TryGetLocalPath() : null;
    }

    public async Task<string?> PickExportFileAsync(string title, string suggestedName, ExportFormat format, string? startFolder)
    {
        var first = TypeOf(format);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = ExportOptions.ExtensionOf(format).TrimStart('.'),
            FileTypeChoices = [first, .. new[] { PngFiles, JpegFiles, GifFiles, WebPFiles }.Where(t => t != first)],
            SuggestedStartLocation = await FolderAsync(startFolder),
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>The older, format-only picker Save As still uses for PNG/JPEG.</summary>
    public Task<string?> PickExportFileAsync(string suggestedName, ExportFormat format) =>
        PickExportFileAsync("Export As", suggestedName, format, null);

    /// <summary>Save As's JPEG options (quality and matte) for a flattened PNG/JPEG copy.</summary>
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

        if (!await ShowSimpleDialog("Export Options", "Export…", Row("Format", format), qualityRow, transparencyRow, matteRow)) return null;
        return current with
        {
            Format = format.SelectedIndex == 1 ? ExportFormat.Jpeg : ExportFormat.Png,
            Quality = (int)Math.Round(quality.Value),
            Transparency = transparency.IsChecked == true,
            Matte = matte.SelectedIndex switch { 1 => backgroundColor, 2 => ((byte)0, (byte)0, (byte)0), _ => ((byte)255, (byte)255, (byte)255) },
        };
    }

    public async Task<ExportLayersRequest?> AskExportLayersAsync(ExportLayersRequest current, bool hasSelection, bool hasArtboards)
    {
        var scopes = new List<(string Name, ExportLayerScope Scope)>();
        if (hasSelection) scopes.Add(("Selected layers", ExportLayerScope.Selected));
        if (hasArtboards) scopes.Add(("All artboards", ExportLayerScope.Artboards));
        scopes.Add(("All top-level layers", ExportLayerScope.TopLevel));
        var scope = new ComboBox { ItemsSource = scopes.Select(s => s.Name).ToList(), Width = 190,
            SelectedIndex = Math.Max(0, scopes.FindIndex(s => s.Scope == current.Scope)) };
        var format = new ComboBox { ItemsSource = ExportAsViewModel.FormatNames, SelectedIndex = (int)current.Options.Format, Width = 190 };
        var quality = new Slider { Minimum = 1, Maximum = 100, Value = current.Options.Quality, Width = 130, VerticalAlignment = VerticalAlignment.Center };
        var transparency = new CheckBox { Content = "Transparency", IsChecked = current.Options.Transparency };
        var trim = new CheckBox { Content = "Trim to layer content", IsChecked = current.TrimToContent,
            [ToolTip.TipProperty] = "Each file is as large as the layer's pixels; off, every file has the canvas size (artboards are always their own size)" };
        var visible = new CheckBox { Content = "Visible layers only", IsChecked = current.VisibleOnly };
        var prefix = new TextBox { Text = current.Prefix, Width = 190, MinHeight = 24, Padding = new Thickness(6, 2), PlaceholderText = "(none)" };
        var qualityRow = Row("Quality", quality);
        void Refresh() => qualityRow.IsVisible = format.SelectedIndex is 1 or 3;
        format.SelectionChanged += (_, _) => Refresh();
        Refresh();

        if (!await ShowSimpleDialog("Layers to Files", "Choose Folder…", Row("Export", scope), Row("Format", format), qualityRow,
                Row("", transparency), Row("", trim), Row("", visible), Row("Name prefix", prefix)))
            return null;
        return new ExportLayersRequest(
            current.Options with
            {
                Format = (ExportFormat)Math.Max(0, format.SelectedIndex),
                Quality = (int)Math.Round(quality.Value),
                Transparency = transparency.IsChecked == true,
            },
            scopes[Math.Max(0, scope.SelectedIndex)].Scope, trim.IsChecked == true, visible.IsChecked == true, prefix.Text ?? "");
    }

    private static Control Row(string label, Control input) => new DockPanel
    {
        Children = { new TextBlock { Text = label, Width = 100, VerticalAlignment = VerticalAlignment.Center }, input },
    };

    /// <summary>A small modal dialog with the given rows and Cancel / OK buttons; true for OK.</summary>
    private async Task<bool> ShowSimpleDialog(string title, string okLabel, params Control[] rows)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        bool ok = false;
        var okButton = new Button { Content = okLabel, IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        okButton.Click += (_, _) => { ok = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        foreach (var row in rows) panel.Children.Add(row);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0),
            Children = { cancel, okButton },
        });
        dialog.Content = panel;
        await dialog.ShowDialog(this);
        return ok;
    }
}
