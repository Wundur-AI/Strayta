using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Strayta.Editor.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// The window side of paths and shapes: Window › Paths, ⌘Return (load the path as a selection), Delete and arrow keys
// for selected path items wherever focus is, and the small dialogs of the Paths panel (feather, tolerance, name).
public partial class MainWindow
{
    private void AddPathMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var window = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Window").Menu!;
        var layers = window.Items.OfType<NativeMenuItem>().First(i => i.Header == "Layers");
        window.Items.Insert(window.Items.IndexOf(layers) + 1, item("Paths", Editor.ShowPanelCommand, null, "Paths"));

        Editor.AskFeather = async () => (float?)await AskNumberAsync("Make Selection", "Feather Radius", " px", 0, 0, 1000);
        Editor.AskTolerance = async () => await AskNumberAsync("Make Work Path", "Tolerance", " px", 2, 0.5, 10);
        PathsToolViewModel.AskName = AskNameAsync;
        // Before the selection's own keys (which would clear pixels on Delete): the path tools' keys come first.
        AddHandler(KeyDownEvent, OnPathKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnPathKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || IsTyping || Editor.ActiveDocument is not { } doc || doc.FreeTransform is not null) return;
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        if (e.Key is Key.Return or Key.Enter && e.KeyModifiers == cmd && doc.CommandPath is not null)
        {
            doc.LoadPathAsSelection();
            e.Handled = true;
            return;
        }
        // With focus away from the canvas (a panel), the canvas's own handling (ImageCanvas.Paths.cs) does not run.
        if (!Editor.IsPathSelectTool && Editor.Tool != CanvasTool.Pen || e.Source is ImageCanvas) return;
        bool direct = Editor.Tool != CanvasTool.PathSelect;
        if (e.Key is Key.Delete or Key.Back && e.KeyModifiers == KeyModifiers.None && doc.DeleteSelectedPathItems(direct)) e.Handled = true;
    }

    /// <summary>A one-number dialog (Make Selection's feather radius, Make Work Path's tolerance); null when cancelled.</summary>
    private async Task<double?> AskNumberAsync(string title, string label, string unit, double value, double min, double max)
    {
        var field = new NumberField { Value = value, Unit = unit, Width = 90 };
        double? result = null;
        var dialog = new Window { Title = title, Width = 320, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            result = Math.Clamp(field.Value, min, max);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new DockPanel { Children = { new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center }, field } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<string?> AskNameAsync(string title, string initial)
    {
        var box = new TextBox { Text = initial, Width = 200 };
        string? result = null;
        var dialog = new Window { Title = title, Width = 340, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            result = box.Text?.Trim();
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new DockPanel { Children = { new TextBlock { Text = "Name", Width = 60, VerticalAlignment = VerticalAlignment.Center }, box } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        await dialog.ShowDialog(this);
        return result;
    }
}
