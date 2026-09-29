using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// View › Rulers, Show › Guides / Grid, Snap, Snap To, Lock / Clear / New Guide, New Guide Layout and the grid settings,
// Window › Info, with Photoshop's shortcuts and check marks, and the three small dialogs (EditorViewModel.Guides.cs).
public partial class MainWindow
{
    private void AddGuideMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var top = menu.Items.OfType<NativeMenuItem>().ToList();
        var view = top.First(i => i.Header == "View").Menu!;

        var checks = new List<(NativeMenuItem Item, Func<bool> IsOn)>();
        NativeMenuItem Check(string header, ICommand command, KeyGesture? gesture, object? parameter, Func<bool> isOn)
        {
            var i = item(header, command, gesture, parameter);
            i.ToggleType = MenuItemToggleType.CheckBox;
            i.IsChecked = isOn();
            checks.Add((i, isOn));
            return i;
        }
        NativeMenuItem Sub(string header, params NativeMenuItemBase[] items)
        {
            var m = new NativeMenu();
            foreach (var i in items) m.Items.Add(i);
            return new NativeMenuItem(header) { Menu = m };
        }

        var e = Editor;
        NativeMenuItemBase[] added =
        [
            new NativeMenuItemSeparator(),
            Sub("Show",
                Check("Guides", e.ToggleGuidesCommand, new KeyGesture(Key.OemSemicolon, cmd), null, () => e.ShowGuides),
                Check("Grid", e.ToggleGridCommand, new KeyGesture(Key.OemQuotes, cmd), null, () => e.ShowGrid)),
            Check("Rulers", e.ToggleRulersCommand, new KeyGesture(Key.R, cmd), null, () => e.ShowRulers),
            new NativeMenuItemSeparator(),
            Check("Snap", e.ToggleSnapCommand, new KeyGesture(Key.OemSemicolon, cmd | KeyModifiers.Shift), null, () => e.SnapEnabled),
            Sub("Snap To",
                Check("Guides", e.ToggleSnapToCommand, null, "Guides", () => e.SnapsTo(SnapTargets.Guides)),
                Check("Grid", e.ToggleSnapToCommand, null, "Grid", () => e.SnapsTo(SnapTargets.Grid)),
                Check("Layers", e.ToggleSnapToCommand, null, "Layers", () => e.SnapsTo(SnapTargets.Layers)),
                Check("Document Bounds", e.ToggleSnapToCommand, null, "DocumentBounds", () => e.SnapsTo(SnapTargets.DocumentBounds)),
                new NativeMenuItemSeparator(),
                item("All", e.ToggleSnapToCommand, null, "All"),
                item("None", e.ToggleSnapToCommand, null, "None")),
            new NativeMenuItemSeparator(),
            Check("Lock Guides", e.ToggleLockGuidesCommand, new KeyGesture(Key.OemSemicolon, cmd | KeyModifiers.Alt), null, () => e.LockGuides),
            item("Clear Guides", e.ClearGuidesCommand, null, null),
            item("New Guide…", e.NewGuideCommand, null, null),
            item("New Guide Layout…", e.NewGuideLayoutCommand, null, null),
            item("Guides & Grid Settings…", e.GridSettingsCommand, null, null),
        ];
        // After the zoom commands, before Appearance (Photoshop's View menu order).
        var zoomOut = view.Items.OfType<NativeMenuItem>().First(i => i.Header == "Zoom Out");
        int at = view.Items.IndexOf(zoomOut) + 1;
        foreach (var i in added) view.Items.Insert(at++, i);
        e.ViewSettingsChanged += () =>
        {
            foreach (var (i, isOn) in checks) i.IsChecked = isOn();
        };

        var window = top.First(i => i.Header == "Window").Menu!;
        var history = window.Items.OfType<NativeMenuItem>().First(i => i.Header == "History");
        window.Items.Insert(window.Items.IndexOf(history) + 1, item("Info", e.ShowPanelCommand, new KeyGesture(Key.F8), "Info"));

        e.AskNewGuide = AskNewGuideAsync;
        e.AskGuideLayout = AskGuideLayoutAsync;
        e.AskGridSettings = AskGridSettingsAsync;
    }

    // ---- Dialogs --------------------------------------------------------------------------------------------

    private Window GuideDialog(string title, Control body, Func<bool> accept, out Func<Task<bool>> show)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        bool ok = false;
        var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        okButton.Click += (_, _) =>
        {
            if (!accept()) return;
            ok = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                body,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { cancel, okButton } },
            },
        };
        show = async () =>
        {
            await dialog.ShowDialog(this);
            return ok;
        };
        return dialog;
    }

    private static Control Row(string label, Control input, double labelWidth = 110) => new DockPanel
    {
        Children = { new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center }, input },
    };

    private static string Length(double pixels, RulerUnit unit, double resolution, double extent) =>
        $"{RulerUnits.Format(RulerUnits.ToUnits(pixels, unit, resolution, extent), unit)} {RulerUnits.Suffix(unit)}";

    /// <summary>View › New Guide…: orientation and a position from the ruler origin, typed in any unit ("2 cm", "50%").</summary>
    private async Task<Guide?> AskNewGuideAsync(DocumentViewModel doc)
    {
        var unit = Editor.RulerUnit;
        var horizontal = new RadioButton { Content = "Horizontal", GroupName = "guide-orientation" };
        var vertical = new RadioButton { Content = "Vertical", GroupName = "guide-orientation", IsChecked = true };
        var position = new TextBox { Text = $"0 {RulerUnits.Suffix(unit)}", Width = 140 };
        var error = new TextBlock { Foreground = Avalonia.Media.Brushes.OrangeRed, IsVisible = false, Text = "Enter a number, optionally with a unit (px, in, cm, mm, pt, %)." };
        double? pixels = null;
        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Row("Orientation", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { horizontal, vertical } }),
                Row("Position", position),
                error,
            },
        };
        GuideDialog("New Guide", body, () =>
        {
            bool isHorizontal = horizontal.IsChecked == true;
            pixels = RulerUnits.ParsePixels(position.Text, unit, doc.Resolution, isHorizontal ? doc.Model.Height : doc.Model.Width);
            error.IsVisible = pixels is null;
            return pixels is not null;
        }, out var show);
        position.AttachedToVisualTree += (_, _) =>
        {
            position.Focus();
            position.SelectAll();
        };
        if (!await show() || pixels is not { } p) return null;
        bool h = horizontal.IsChecked == true;
        return new Guide(h ? GuideOrientation.Horizontal : GuideOrientation.Vertical, p + (h ? doc.RulerOrigin.Y : doc.RulerOrigin.X));
    }

    /// <summary>View › New Guide Layout…: Photoshop's columns, rows, gutters and margins (blank sizes fill the space).</summary>
    private async Task<(GuideLayout, bool)?> AskGuideLayoutAsync(DocumentViewModel doc)
    {
        var unit = Editor.RulerUnit;
        double res = doc.Resolution, w = doc.Model.Width, h = doc.Model.Height;
        string suffix = RulerUnits.Suffix(unit);
        TextBox Field(string text) => new() { Text = text, Width = 90, PlaceholderText = suffix };
        var columns = new CheckBox { Content = "Columns", IsChecked = true };
        var colNumber = Field("8");
        var colWidth = Field("");
        var colGutter = Field(Length(20, unit, res, w));
        var rows = new CheckBox { Content = "Rows" };
        var rowNumber = Field("4");
        var rowHeight = Field("");
        var rowGutter = Field(Length(20, unit, res, h));
        var margin = new CheckBox { Content = "Margin" };
        var mTop = Field(Length(40, unit, res, h));
        var mLeft = Field(Length(40, unit, res, w));
        var mBottom = Field(Length(40, unit, res, h));
        var mRight = Field(Length(40, unit, res, w));
        var center = new CheckBox { Content = "Center Columns" };
        var clear = new CheckBox { Content = "Clear Existing Guides", IsChecked = doc.Guides.Count > 0 };
        var error = new TextBlock { Foreground = Avalonia.Media.Brushes.OrangeRed, IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        Control Grid2(params (string Label, Control Input)[] cells)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"), RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", (cells.Length + 1) / 2))) };
            for (int i = 0; i < cells.Length; i++)
            {
                var label = new TextBlock { Text = cells[i].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(i % 2 == 0 ? 18 : 14, 3, 8, 3) };
                Grid.SetRow(label, i / 2);
                Grid.SetColumn(label, (i % 2) * 2);
                Grid.SetRow(cells[i].Input, i / 2);
                Grid.SetColumn(cells[i].Input, (i % 2) * 2 + 1);
                cells[i].Input.Margin = new Thickness(0, 3);
                g.Children.Add(label);
                g.Children.Add(cells[i].Input);
            }
            return g;
        }
        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                columns, Grid2(("Number", colNumber), ("Width", colWidth), ("Gutter", colGutter)),
                rows, Grid2(("Number", rowNumber), ("Height", rowHeight), ("Gutter", rowGutter)),
                margin, Grid2(("Top", mTop), ("Left", mLeft), ("Bottom", mBottom), ("Right", mRight)),
                center, clear, error,
            },
        };
        GuideLayout? layout = null;
        var dialog = GuideDialog("New Guide Layout", body, () =>
        {
            var problems = new List<string>();
            double Len(TextBox box, double extent, string name, double fallback = 0)
            {
                if (string.IsNullOrWhiteSpace(box.Text)) return fallback;
                if (RulerUnits.ParsePixels(box.Text, unit, res, extent) is { } v && v >= 0) return v;
                problems.Add(name);
                return fallback;
            }
            int Count(TextBox box, string name)
            {
                if (int.TryParse(box.Text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) && n is >= 1 and <= 1000) return n;
                problems.Add(name);
                return 0;
            }
            bool useColumns = columns.IsChecked == true, useRows = rows.IsChecked == true;
            layout = new GuideLayout
            {
                Columns = useColumns ? Count(colNumber, "column number") : 0,
                ColumnWidth = useColumns && !string.IsNullOrWhiteSpace(colWidth.Text) ? Len(colWidth, w, "column width") : null,
                ColumnGutter = useColumns ? Len(colGutter, w, "column gutter") : 0,
                Rows = useRows ? Count(rowNumber, "row number") : 0,
                RowHeight = useRows && !string.IsNullOrWhiteSpace(rowHeight.Text) ? Len(rowHeight, h, "row height") : null,
                RowGutter = useRows ? Len(rowGutter, h, "row gutter") : 0,
                HasMargins = margin.IsChecked == true,
                MarginTop = Len(mTop, h, "top margin"),
                MarginLeft = Len(mLeft, w, "left margin"),
                MarginBottom = Len(mBottom, h, "bottom margin"),
                MarginRight = Len(mRight, w, "right margin"),
                CenterColumns = center.IsChecked == true,
            };
            error.Text = $"Check the {string.Join(", ", problems)}.";
            error.IsVisible = problems.Count > 0;
            return problems.Count == 0;
        }, out var show);
        dialog.Width = 400;
        if (!await show() || layout is null) return null;
        return (layout, clear.IsChecked == true);
    }

    /// <summary>Photoshop's Guides, Grid &amp; Slices preferences, the grid part: gridline spacing and unit, subdivisions.</summary>
    private async Task AskGridSettingsAsync()
    {
        var e = Editor;
        var spacing = new NumericUpDown { Value = (decimal)e.GridSpacing, Minimum = 0.001m, Maximum = 100000, Increment = 1, FormatString = "0.###", Width = 110 };
        var unit = new ComboBox { ItemsSource = RulerUnits.All.Select(RulerUnits.Name).ToList(), SelectedIndex = (int)e.GridUnit, Width = 130 };
        var subdivisions = new NumericUpDown { Value = e.GridSubdivisions, Minimum = 1, Maximum = 100, Increment = 1, FormatString = "0", Width = 110 };
        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Grid", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                Row("Gridline Every", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { spacing, unit } }),
                Row("Subdivisions", subdivisions),
            },
        };
        GuideDialog("Guides & Grid", body, () => spacing.Value is > 0 && subdivisions.Value is >= 1, out var show);
        if (!await show()) return;
        e.GridUnit = RulerUnits.All[Math.Max(0, unit.SelectedIndex)];
        e.GridSpacing = (double)(spacing.Value ?? 1);
        e.GridSubdivisions = (int)(subdivisions.Value ?? 4);
    }
}
