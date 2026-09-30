using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Strayta.Core;
using Strayta.Editor.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>Layer › New › Layer… (⇧⌘N): name, clipping, color label, mode and opacity, as in Photoshop's dialog.</summary>
public sealed class NewLayerWindow : Window
{
    private readonly TextBox _name;
    private readonly CheckBox _clip = new() { Content = "Use Previous Layer to Create Clipping Mask" };
    private readonly ComboBox _color;
    private readonly ComboBox _mode;
    private readonly NumericUpDown _opacity = new() { Minimum = 0, Maximum = 100, Increment = 1, Value = 100, FormatString = "0", Width = 90 };
    private bool _ok;

    private NewLayerWindow(string suggestedName)
    {
        Title = "New Layer";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name = new TextBox { Text = suggestedName };
        _color = new ComboBox
        {
            ItemsSource = Enum.GetValues<LayerColor>().Select(c => c == LayerColor.None ? "None" : c.ToString()).ToArray(),
            SelectedIndex = 0,
            Width = 140,
        };
        var modes = LayerItemViewModel.BlendModes.Where(m => m != BlendMode.PassThrough).ToArray();
        _mode = new ComboBox { ItemsSource = modes.Select(BlendModeNames.Of).ToArray(), SelectedIndex = Array.IndexOf(modes, BlendMode.Normal), Width = 160 };
        _mode.Tag = modes;

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => { _ok = true; Close(); };
        cancel.Click += (_, _) => Close();

        Control Row(string label, Control input) => new DockPanel
        {
            Children = { new TextBlock { Text = label, Width = 70, VerticalAlignment = VerticalAlignment.Center }, input },
        };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                Row("Name:", _name),
                Row("", _clip),
                Row("Color:", _color),
                Row("Mode:", new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 14,
                    Children = { _mode, new TextBlock { Text = "Opacity:", VerticalAlignment = VerticalAlignment.Center }, _opacity, new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center } },
                }),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { cancel, ok } },
            },
        };
        Opened += (_, _) =>
        {
            _name.Focus();
            _name.SelectAll();
        };
    }

    /// <summary>Shows the dialog; null when cancelled.</summary>
    public static async Task<DocumentViewModel.NewLayerOptions?> AskAsync(Window owner, string suggestedName)
    {
        var dialog = new NewLayerWindow(suggestedName);
        await dialog.ShowDialog(owner);
        if (!dialog._ok) return null;
        var modes = (BlendMode[])dialog._mode.Tag!;
        return new DocumentViewModel.NewLayerOptions(dialog._name.Text ?? suggestedName, dialog._clip.IsChecked == true,
            (LayerColor)Math.Max(0, dialog._color.SelectedIndex), modes[Math.Max(0, dialog._mode.SelectedIndex)], (double)(dialog._opacity.Value ?? 100));
    }

    /// <summary>Asks for the new layer's settings and adds it to the active document.</summary>
    public static async Task NewLayerAsync(Window owner, EditorViewModel editor)
    {
        if (editor.ActiveDocument is not { } doc) return;
        if (await AskAsync(owner, Editing.LayerFactory.NextName(doc.Model, "Layer")) is { } options) doc.NewLayer(options);
    }
}
