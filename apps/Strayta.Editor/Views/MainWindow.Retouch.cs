using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Edit › Content-Aware Fill… and Window › Clone Source, and the Content-Aware Fill dialog.
public partial class MainWindow : IContentAwareFillDialogs
{
    /// <summary>Adds Content-Aware Fill… to the Edit menu (after Free Transform) and Clone Source to the Window menu.</summary>
    private void AddRetouchMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        if (menu.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Edit")?.Menu is { } edit)
        {
            var transform = edit.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Free Transform");
            int at = transform is null ? edit.Items.Count : edit.Items.IndexOf(transform);
            edit.Items.Insert(at, item("Content-Aware Fill…", Editor.ContentAwareFillCommand, null, null));
        }
        if (menu.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Window")?.Menu is { } window)
        {
            var history = window.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "History");
            int at = history is null ? 0 : window.Items.IndexOf(history);
            window.Items.Insert(at, item("Clone Source", Editor.ShowPanelCommand, null, "CloneSource"));
        }
        if (Environment.GetEnvironmentVariable("STRAYTA_PAINTBENCH") == "new")
            Opened += async (_, _) => await SelfTest.RunSyntheticPaintBenchmarkAsync(Editor); // SelfTest.Brush.cs
    }

    // ---- IContentAwareFillDialogs ------------------------------------------------------------------------

    public async Task<ContentAwareFillSettings?> AskContentAwareFillAsync(ContentAwareFillSettings current)
    {
        var sample = new ComboBox { ItemsSource = new[] { "Current Layer", "All Layers" }, SelectedIndex = current.SampleAllLayers ? 1 : 0, Width = 160 };
        var output = new ComboBox { ItemsSource = new[] { "Current Layer", "New Layer" }, SelectedIndex = current.OutputToNewLayer ? 1 : 0, Width = 160 };
        var adapt = new CheckBox { Content = "Color Adaptation", IsChecked = current.ColorAdaptation };
        ToolTip.SetTip(adapt, "Blend the fill's brightness and color into its surroundings");
        var dialog = new Window
        {
            Title = "Content-Aware Fill",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        bool ok = false;
        var fill = new Button { Content = "OK", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        fill.Click += (_, _) => { ok = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        Control Row(string label, Control input) => new DockPanel
        {
            Children = { new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center }, input },
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Fills the selection with detail from everything outside it.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Classes = { "muted" },
                },
                Row("Sample", sample),
                Row("Output To", output),
                adapt,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { cancel, fill } },
            },
        };
        await dialog.ShowDialog(this);
        return ok ? new ContentAwareFillSettings(sample.SelectedIndex == 1, output.SelectedIndex == 1, adapt.IsChecked == true) : null;
    }
}
