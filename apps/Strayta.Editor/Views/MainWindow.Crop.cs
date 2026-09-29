using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Strayta.Editor.ViewModels;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Views;

// The Image menu (Image Size, Canvas Size, Crop, Trim), its dialogs, and the Crop tool's Enter and Esc keys.
public partial class MainWindow : ICanvasDialogs
{
    /// <summary>Adds the Image menu in Photoshop's place, right after Edit.</summary>
    private void AddImageMenu(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var image = new NativeMenu
        {
            item("Image Size…", Editor.ImageSizeCommand, new KeyGesture(Key.I, cmd | KeyModifiers.Alt), null),
            item("Canvas Size…", Editor.CanvasSizeCommand, new KeyGesture(Key.C, cmd | KeyModifiers.Alt), null),
            new NativeMenuItemSeparator(),
            item("Crop", Editor.CropToSelectionCommand, null, null),
            item("Trim…", Editor.TrimCommand, null, null),
        };
        var edit = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Edit");
        menu.Items.Insert(menu.Items.IndexOf(edit) + 1, new NativeMenuItem("Image") { Menu = image });

        AddHandler(KeyDownEvent, OnCropKey, Avalonia.Interactivity.RoutingStrategies.Bubble);
        if (Environment.GetEnvironmentVariable("STRAYTA_CROPBENCH") is { Length: > 0 } bench)
            Opened += async (_, _) => await SelfTest.RunCropBenchmarkAsync(Editor, synthetic: bench == "new");
    }

    /// <summary>
    /// Enter crops and Esc resets the box while the Crop tool is active; O cycles the overlay and Shift+O turns it.
    /// Enter and Esc also drive the Perspective Crop tool.
    /// </summary>
    private void OnCropKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || IsTyping) return;
        var doc = Editor.ActiveDocument;
        if (doc?.PerspectiveCrop is not null && e.KeyModifiers == KeyModifiers.None && e.Key is Key.Enter or Key.Return or Key.Escape)
        {
            (e.Key == Key.Escape ? Editor.CancelPerspectiveCropCommand : Editor.CommitPerspectiveCropCommand).Execute(null);
            e.Handled = true;
            return;
        }
        if (doc?.CropBox is null) return;
        if (e.Key == Key.O && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            Editor.CycleCropOverlay(orientation: e.KeyModifiers == KeyModifiers.Shift);
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter or Key.Return:
                Editor.CommitCropCommand.Execute(null);
                break;
            case Key.Escape:
                Editor.CancelCropCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    public async Task<CropPromptChoice> AskApplyCropAsync()
    {
        var dialog = new Window
        {
            Title = "Crop",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var choice = CropPromptChoice.Cancel;
        Button Choice(string text, CropPromptChoice value, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, MinWidth = 90, IsDefault = isDefault, IsCancel = isCancel };
            b.Click += (_, _) =>
            {
                choice = value;
                dialog.Close();
            };
            return b;
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Crop the image?", FontWeight = FontWeight.SemiBold },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        Choice("Cancel", CropPromptChoice.Cancel, isCancel: true),
                        Choice("Don't Crop", CropPromptChoice.DontCrop),
                        Choice("Crop", CropPromptChoice.Crop, isDefault: true),
                    },
                },
            },
        };
        await dialog.ShowDialog(this);
        return choice;
    }

    // ---- Dialogs ------------------------------------------------------------------------------------

    private static Control DialogRow(string label, Control input, double labelWidth = 110) => new DockPanel
    {
        Children = { new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center }, input },
    };

    private static NumericUpDown NumberBox(double value, double min, double max, string format = "0") => new()
    {
        Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = 1, FormatString = format, Width = 120,
    };

    /// <summary>Shows a dialog with OK and Cancel; returns true on OK.</summary>
    private async Task<bool> ShowDialogAsync(string title, string okText, params Control[] rows)
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
        var accept = new Button { Content = okText, IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        accept.Click += (_, _) => { ok = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        foreach (var r in rows) panel.Children.Add(r);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0),
            Children = { cancel, accept },
        });
        dialog.Content = panel;
        await dialog.ShowDialog(this);
        return ok;
    }

    public async Task<ImageSizeRequest?> AskImageSizeAsync(int width, int height, double resolution, ResampleMethod method)
    {
        var w = NumberBox(width, 1, 300_000, "0.##");
        var h = NumberBox(height, 1, 300_000, "0.##");
        var units = new ComboBox { ItemsSource = new[] { "Pixels", "Percent" }, SelectedIndex = 0, Width = 100 };
        var constrain = new CheckBox { Content = "Constrain proportions", IsChecked = true };
        // Photoshop's Scale Styles: on by default, and only with constrained proportions.
        var scaleStyles = new CheckBox { Content = "Scale Styles", IsChecked = true };
        constrain.IsCheckedChanged += (_, _) =>
        {
            scaleStyles.IsEnabled = constrain.IsChecked == true;
            if (constrain.IsChecked != true) scaleStyles.IsChecked = false;
        };
        var ppi = NumberBox(resolution, 1, 30_000, "0.##");
        var resample = new ComboBox
        {
            ItemsSource = new[] { "Bicubic Automatic", "Bilinear", "Nearest Neighbor (hard edges)" },
            SelectedIndex = method switch { ResampleMethod.Bilinear => 1, ResampleMethod.NearestNeighbor => 2, _ => 0 },
            Width = 230,
        };
        var result = new TextBlock { Classes = { "muted" } };

        bool percent = false, syncing = false;
        (int W, int H) Pixels()
        {
            double a = (double)(w.Value ?? 1), b = (double)(h.Value ?? 1);
            return percent
                ? (Math.Max(1, (int)Math.Round(width * a / 100)), Math.Max(1, (int)Math.Round(height * b / 100)))
                : (Math.Max(1, (int)Math.Round(a)), Math.Max(1, (int)Math.Round(b)));
        }
        void Refresh()
        {
            var (pw, ph) = Pixels();
            double megapixels = (double)pw * ph / 1e6;
            result.Text = $"{width} × {height} px → {pw} × {ph} px ({megapixels:0.#} MP) · {pw / (double)(ppi.Value ?? 72):0.##} × {ph / (double)(ppi.Value ?? 72):0.##} in";
        }
        void Sync(NumericUpDown changed)
        {
            if (syncing) return;
            syncing = true;
            if (constrain.IsChecked == true)
            {
                double v = (double)(changed.Value ?? 1);
                var other = changed == w ? h : w;
                other.Value = (decimal)(percent ? v : changed == w ? v * height / width : v * width / height);
                if (!percent) other.Value = Math.Round(other.Value ?? 1);
            }
            syncing = false;
            Refresh();
        }
        w.ValueChanged += (_, _) => Sync(w);
        h.ValueChanged += (_, _) => Sync(h);
        ppi.ValueChanged += (_, _) => Refresh();
        units.SelectionChanged += (_, _) =>
        {
            var (pw, ph) = Pixels();
            percent = units.SelectedIndex == 1;
            syncing = true;
            w.Value = percent ? (decimal)Math.Round(pw * 100.0 / width, 2) : pw;
            h.Value = percent ? (decimal)Math.Round(ph * 100.0 / height, 2) : ph;
            syncing = false;
            Refresh();
        };
        Refresh();

        if (!await ShowDialogAsync("Image Size", "OK",
                DialogRow("Width", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { w, units } }),
                DialogRow("Height", h),
                DialogRow("", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { constrain, scaleStyles } }),
                DialogRow("Resolution", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ppi, new TextBlock { Text = "pixels/inch", VerticalAlignment = VerticalAlignment.Center } } }),
                DialogRow("Resample", resample),
                result))
            return null;
        var (fw, fh) = Pixels();
        var chosen = resample.SelectedIndex switch { 1 => ResampleMethod.Bilinear, 2 => ResampleMethod.NearestNeighbor, _ => ResampleMethod.Bicubic };
        return new ImageSizeRequest(fw, fh, (double)(ppi.Value ?? (decimal)resolution), chosen, scaleStyles.IsChecked == true);
    }

    public async Task<CanvasSizeRequest?> AskCanvasSizeAsync(int width, int height, Color foreground, Color background, bool hasBackgroundLayer)
    {
        var w = NumberBox(width, -300_000, 300_000, "0.##");
        var h = NumberBox(height, -300_000, 300_000, "0.##");
        var units = new ComboBox { ItemsSource = new[] { "Pixels", "Percent" }, SelectedIndex = 0, Width = 100 };
        var relative = new CheckBox { Content = "Relative" };
        var colors = new ComboBox
        {
            ItemsSource = new[] { "Foreground", "Background", "White", "Black", "Gray", "Other…" },
            SelectedIndex = 1, Width = 130, IsEnabled = hasBackgroundLayer,
        };
        var picker = new ColorPicker { Color = background, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        colors.SelectionChanged += (_, _) => picker.IsVisible = colors.SelectedIndex == 5;
        var result = new TextBlock { Classes = { "muted" } };

        int ax = 0, ay = 0;
        var anchors = new Grid { ColumnDefinitions = new ColumnDefinitions("28,28,28"), RowDefinitions = new RowDefinitions("28,28,28") };
        var buttons = new Button[3, 3];
        void DrawAnchors()
        {
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                {
                    int dx = x - 1 - ax, dy = y - 1 - ay;
                    buttons[x, y].Content = (dx, dy) switch
                    {
                        (0, 0) => "●",
                        (-1, 0) => "←", (1, 0) => "→", (0, -1) => "↑", (0, 1) => "↓",
                        (-1, -1) => "↖", (1, -1) => "↗", (-1, 1) => "↙", (1, 1) => "↘",
                        _ => "",
                    };
                }
        }
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
            {
                var b = new Button { Width = 26, Height = 26, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                int bx = x, by = y;
                b.Click += (_, _) => { (ax, ay) = (bx - 1, by - 1); DrawAnchors(); };
                Grid.SetColumn(b, x);
                Grid.SetRow(b, y);
                anchors.Children.Add(b);
                buttons[x, y] = b;
            }
        DrawAnchors();

        bool percent = false;
        (int W, int H) Pixels()
        {
            double a = (double)(w.Value ?? 0), b = (double)(h.Value ?? 0);
            bool rel = relative.IsChecked == true;
            double pw = percent ? (rel ? width * (1 + a / 100) : width * a / 100) : rel ? width + a : a;
            double ph = percent ? (rel ? height * (1 + b / 100) : height * b / 100) : rel ? height + b : b;
            return (Math.Max(1, (int)Math.Round(pw)), Math.Max(1, (int)Math.Round(ph)));
        }
        void Refresh()
        {
            var (pw, ph) = Pixels();
            result.Text = $"Current size {width} × {height} px → new size {pw} × {ph} px";
        }
        void Reset()
        {
            bool rel = relative.IsChecked == true;
            w.Value = rel ? 0 : percent ? 100 : width;
            h.Value = rel ? 0 : percent ? 100 : height;
            Refresh();
        }
        units.SelectionChanged += (_, _) => { percent = units.SelectedIndex == 1; Reset(); };
        relative.IsCheckedChanged += (_, _) => Reset();
        w.ValueChanged += (_, _) => Refresh();
        h.ValueChanged += (_, _) => Refresh();
        Refresh();

        if (!await ShowDialogAsync("Canvas Size", "OK",
                result,
                DialogRow("Width", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { w, units } }),
                DialogRow("Height", h),
                DialogRow("", relative),
                DialogRow("Anchor", anchors),
                DialogRow("Canvas extension color", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { colors, picker } }, 160)))
            return null;
        var (fw, fh) = Pixels();
        var fill = colors.SelectedIndex switch
        {
            0 => foreground,
            2 => Colors.White,
            3 => Colors.Black,
            4 => Color.FromRgb(128, 128, 128),
            5 => picker.Color,
            _ => background,
        };
        return new CanvasSizeRequest(fw, fh, ax, ay, fill);
    }

    public async Task<TrimRequest?> AskTrimAsync()
    {
        var transparent = new RadioButton { Content = "Transparent Pixels", GroupName = "trim", IsChecked = true };
        var topLeft = new RadioButton { Content = "Top Left Pixel Color", GroupName = "trim" };
        var bottomRight = new RadioButton { Content = "Bottom Right Pixel Color", GroupName = "trim" };
        CheckBox Side(string name) => new() { Content = name, IsChecked = true };
        CheckBox top = Side("Top"), left = Side("Left"), bottom = Side("Bottom"), right = Side("Right");
        if (!await ShowDialogAsync("Trim", "OK",
                new TextBlock { Text = "Based on", FontWeight = FontWeight.SemiBold },
                transparent, topLeft, bottomRight,
                new TextBlock { Text = "Trim Away", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { top, left, bottom, right } }))
            return null;
        var basis = topLeft.IsChecked == true ? TrimBasis.TopLeftColor : bottomRight.IsChecked == true ? TrimBasis.BottomRightColor : TrimBasis.Transparent;
        return new TrimRequest(basis, top.IsChecked == true, left.IsChecked == true, bottom.IsChecked == true, right.IsChecked == true);
    }
}
