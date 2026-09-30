using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Strayta.Core;
using SelectionMode = Strayta.Core.Selection.SelectionMode;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// The window side of channels: Window › Channels, Select › Edit in Quick Mask Mode / Load Selection… / Save Selection…,
// Image › Adjustments, the keys (Q, \, ⌘2–⌘9) and the channel dialogs.
public partial class MainWindow : IChannelDialogs
{
    private void AddChannelMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var top = menu.Items.OfType<NativeMenuItem>().ToList();

        var window = top.First(i => i.Header == "Window").Menu!;
        var layers = window.Items.OfType<NativeMenuItem>().First(i => i.Header == "Layers");
        window.Items.Insert(window.Items.IndexOf(layers) + 1, item("Channels", Editor.ShowPanelCommand, null, "Channels"));

        if (top.FirstOrDefault(i => i.Header == "Select")?.Menu is { } select)
        {
            select.Items.Add(new NativeMenuItemSeparator());
            // Q is handled in OnChannelKey (a single key must not fire while typing).
            select.Items.Add(item("Edit in Quick Mask Mode", Editor.ToggleQuickMaskCommand, null, null));
            select.Items.Add(new NativeMenuItemSeparator());
            select.Items.Add(item("Load Selection…", Editor.LoadSelectionCommand, null, null));
            select.Items.Add(item("Save Selection…", Editor.SaveSelectionCommand, null, null));
        }

        if (top.FirstOrDefault(i => i.Header == "Image")?.Menu is { } image)
        {
            NativeMenuItemBase Adjust(string header, string kind, KeyGesture? gesture = null) => item(header, Editor.ImageAdjustmentCommand, gesture, kind);
            var adjustments = new NativeMenu
            {
                Adjust("Brightness/Contrast…", "Brightness/Contrast"),
                Adjust("Levels…", "Levels", new KeyGesture(Key.L, cmd)),
                Adjust("Exposure…", "Exposure"),
                new NativeMenuItemSeparator(),
                Adjust("Hue/Saturation…", "Hue/Saturation", new KeyGesture(Key.U, cmd)),
                new NativeMenuItemSeparator(),
                Adjust("Invert", "Invert", new KeyGesture(Key.I, cmd)),
                Adjust("Posterize…", "Posterize"),
                Adjust("Threshold…", "Threshold"),
                new NativeMenuItemSeparator(),
                Adjust("Desaturate", "Desaturate", new KeyGesture(Key.U, cmd | KeyModifiers.Shift)),
            };
            image.Items.Insert(0, new NativeMenuItem("Adjustments") { Menu = adjustments });
            image.Items.Insert(1, new NativeMenuItemSeparator());
        }

        AddHandler(KeyDownEvent, OnChannelKey, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    /// <summary>Q toggles Quick Mask mode, \ the layer mask's overlay, ⌘2–⌘9 target channels.</summary>
    private void OnChannelKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || IsTyping || Editor.ActiveDocument is not { } doc) return;
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Q && doc.FreeTransform is null && doc.CropBox is null)
        {
            doc.ToggleQuickMask();
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.OemPipe or Key.OemBackslash)
        {
            doc.ToggleLayerMaskOverlay();
            e.Handled = true;
        }
        else if (e.KeyModifiers == cmd && e.Key is >= Key.D2 and <= Key.D9)
        {
            doc.TargetByShortcut(e.Key - Key.D0);
            e.Handled = true;
        }
    }

    // ---- IChannelDialogs ------------------------------------------------------------------------------------

    private static Color ToColor(RgbColor c) => Color.FromRgb((byte)MathF.Round(c.R * 255), (byte)MathF.Round(c.G * 255), (byte)MathF.Round(c.B * 255));

    private static RgbColor ToRgb(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    public async Task<ChannelOptions?> AskChannelOptionsAsync(string title, ChannelOptions initial, bool spot)
    {
        var name = new TextBox { Text = initial.Name, Width = 200 };
        var masked = new RadioButton { Content = "Masked Areas", GroupName = "indicates", IsChecked = initial.Kind == ChannelKind.MaskedAreas };
        var selected = new RadioButton { Content = "Selected Areas", GroupName = "indicates", IsChecked = initial.Kind == ChannelKind.SelectedAreas };
        var spotColor = new RadioButton { Content = "Spot Color", GroupName = "indicates", IsChecked = initial.Kind == ChannelKind.Spot };
        var picker = new ColorPicker { Color = ToColor(initial.Color), VerticalAlignment = VerticalAlignment.Center };
        var opacity = NumberBox(Math.Round(initial.Opacity * 100), 0, 100);
        var opacityLabel = new TextBlock { Text = initial.Kind == ChannelKind.Spot ? "Solidity (%)" : "Opacity (%)", Width = 110, VerticalAlignment = VerticalAlignment.Center };
        spotColor.IsCheckedChanged += (_, _) => opacityLabel.Text = spotColor.IsChecked == true ? "Solidity (%)" : "Opacity (%)";
        var rows = new List<Control> { DialogRow("Name", name) };
        if (!spot)
            rows.Add(DialogRow("Color Indicates", new StackPanel { Spacing = 4, Children = { masked, selected, spotColor } }));
        rows.Add(DialogRow(spot ? "Ink Color" : "Color", picker));
        rows.Add(new DockPanel { Children = { opacityLabel, opacity } });
        name.AttachedToVisualTree += (_, _) => { name.Focus(); name.SelectAll(); };
        if (!await ShowDialogAsync(title, "OK", rows.ToArray())) return null;
        var kind = spot || spotColor.IsChecked == true ? ChannelKind.Spot : selected.IsChecked == true ? ChannelKind.SelectedAreas : ChannelKind.MaskedAreas;
        return new ChannelOptions(name.Text?.Trim() is { Length: > 0 } n ? n : initial.Name, kind, ToRgb(picker.Color), (float)((double)(opacity.Value ?? 50) / 100));
    }

    private static readonly string[] Operations = ["New Selection", "Add to Selection", "Subtract from Selection", "Intersect with Selection"];
    private static readonly string[] ChannelOperations = ["Replace Channel", "Add to Channel", "Subtract from Channel", "Intersect with Channel"];

    public async Task<SaveSelectionChoice?> AskSaveSelectionAsync(IReadOnlyList<DocumentChannel> channels, string suggestedName)
    {
        var alphas = channels.Where(c => !c.IsSpot).ToList();
        var target = new ComboBox { ItemsSource = new[] { "New" }.Concat(alphas.Select(c => c.Name)).ToList(), SelectedIndex = 0, Width = 200 };
        var name = new TextBox { Text = suggestedName, Width = 200 };
        var operation = new ComboBox { ItemsSource = ChannelOperations, SelectedIndex = 0, Width = 200, IsEnabled = false };
        target.SelectionChanged += (_, _) =>
        {
            bool fresh = target.SelectedIndex <= 0;
            name.IsEnabled = fresh;
            operation.IsEnabled = !fresh;
            if (fresh) operation.SelectedIndex = 0;
        };
        if (!await ShowDialogAsync("Save Selection", "OK", DialogRow("Channel", target), DialogRow("Name", name), DialogRow("Operation", operation)))
            return null;
        int into = target.SelectedIndex <= 0 ? 0 : alphas[target.SelectedIndex - 1].Id;
        return new SaveSelectionChoice(into, name.Text ?? "", (SelectionMode)Math.Max(0, operation.SelectedIndex));
    }

    public async Task<LoadSelectionChoice?> AskLoadSelectionAsync(IReadOnlyList<DocumentChannel> channels, int targetedId, bool hasSelection)
    {
        var channel = new ComboBox { ItemsSource = channels.Select(c => c.Name).ToList(), Width = 200 };
        channel.SelectedIndex = Math.Max(0, channels.ToList().FindIndex(c => c.Id == targetedId));
        var invert = new CheckBox { Content = "Invert" };
        var operation = new ComboBox { ItemsSource = Operations, SelectedIndex = 0, Width = 200, IsEnabled = hasSelection };
        if (!await ShowDialogAsync("Load Selection", "OK", DialogRow("Channel", channel), invert, DialogRow("Operation", operation)))
            return null;
        return new LoadSelectionChoice(channels[Math.Max(0, channel.SelectedIndex)].Id, invert.IsChecked == true, (SelectionMode)Math.Max(0, operation.SelectedIndex));
    }

    public async Task<QuickMaskOptions?> AskQuickMaskOptionsAsync(QuickMaskOptions current)
    {
        var masked = new RadioButton { Content = "Masked Areas", GroupName = "qm", IsChecked = !current.SelectedAreas };
        var selected = new RadioButton { Content = "Selected Areas", GroupName = "qm", IsChecked = current.SelectedAreas };
        var picker = new ColorPicker { Color = ToColor(current.Color), VerticalAlignment = VerticalAlignment.Center };
        var opacity = NumberBox(Math.Round(current.Opacity * 100), 0, 100);
        if (!await ShowDialogAsync("Quick Mask Options", "OK",
                DialogRow("Color Indicates", new StackPanel { Spacing = 4, Children = { masked, selected } }),
                DialogRow("Color", picker), DialogRow("Opacity (%)", opacity)))
            return null;
        return new QuickMaskOptions(ToRgb(picker.Color), (float)((double)(opacity.Value ?? 50) / 100), selected.IsChecked == true);
    }

    public Task<string?> AskChannelNameAsync(string title, string initial) => AskNameAsync(title, initial); // MainWindow.Paths.cs

    public async Task<(double[] Values, bool Option)?> AskAdjustmentAsync(string title, IReadOnlyList<AdjustmentSetting> settings, string? option,
        Action<double[], bool> preview)
    {
        var values = settings.Select(s => s.Value).ToArray();
        var check = option is null ? null : new CheckBox { Content = option };
        var rows = new List<Control>();
        bool loading = true;
        void Changed()
        {
            if (!loading) preview(values.ToArray(), check?.IsChecked == true);
        }
        for (int i = 0; i < settings.Count; i++)
        {
            var s = settings[i];
            int index = i;
            var slider = new Slider { Minimum = s.Minimum, Maximum = s.Maximum, Value = s.Value, Width = 180, VerticalAlignment = VerticalAlignment.Center };
            var box = NumberBox(s.Value, s.Minimum, s.Maximum, s.Format);
            box.Width = 90;
            box.Increment = s.Format.Contains('.') ? 0.01m : 1m;
            slider.ValueChanged += (_, e) =>
            {
                double v = s.Format.Contains('.') ? e.NewValue : Math.Round(e.NewValue);
                if (values[index] == v) return;
                values[index] = v;
                box.Value = (decimal)v;
                Changed();
            };
            box.ValueChanged += (_, e) =>
            {
                double v = (double)(e.NewValue ?? (decimal)s.Value);
                if (values[index] == v) return;
                values[index] = v;
                slider.Value = v;
                Changed();
            };
            rows.Add(new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = s.Label, Width = 120, VerticalAlignment = VerticalAlignment.Center },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { slider, box } },
                },
            });
        }
        if (check is not null)
        {
            check.IsCheckedChanged += (_, _) => Changed();
            rows.Add(check);
        }
        loading = false;
        preview(values.ToArray(), false);
        if (!await ShowDialogAsync(title, "OK", rows.ToArray())) return null;
        return (values, check?.IsChecked == true);
    }
}
