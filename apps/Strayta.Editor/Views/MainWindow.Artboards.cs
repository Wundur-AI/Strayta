using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Strayta.Core;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Layer › New Artboard…, Artboard from Group and Artboards from Layers (after New Group, as in Photoshop's Layer › New).
public partial class MainWindow : IArtboardDialogs
{
    private void AddArtboardMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var layer = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Layer").Menu!;
        var group = layer.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "New Group");
        int at = group is null ? 0 : layer.Items.IndexOf(group) + 1;
        layer.Items.Insert(at, item("New Artboard…", Editor.NewArtboardCommand, null, null));
        layer.Items.Insert(at + 1, item("Artboard from Group", Editor.ArtboardFromGroupCommand, null, null));
        layer.Items.Insert(at + 2, item("Artboards from Layers", Editor.ArtboardFromLayersCommand, null, null));
    }

    public async Task<NewArtboardRequest?> AskNewArtboardAsync(NewArtboardRequest defaults)
    {
        var name = new TextBox { Text = defaults.Name, Width = 200, MinHeight = 24, Padding = new Thickness(6, 2) };
        var presets = new ComboBox { ItemsSource = Editor.ArtboardPresetNames, Width = 200 };
        var width = new NumericUpDown { Value = defaults.Width, Minimum = 1, Maximum = 30000, Increment = 1, FormatString = "0", Width = 130 };
        var height = new NumericUpDown { Value = defaults.Height, Minimum = 1, Maximum = 30000, Increment = 1, FormatString = "0", Width = 130 };
        var background = new ComboBox { ItemsSource = EditorViewModel.ArtboardBackgroundNames, SelectedIndex = (int)defaults.Background - 1, Width = 200 };
        bool syncing = false;
        void SelectPreset()
        {
            var match = ArtboardPresets.Matching((int)(width.Value ?? 0), (int)(height.Value ?? 0));
            syncing = true;
            presets.SelectedIndex = match is null ? 0 : ArtboardPresets.All.ToList().IndexOf(match) + 1;
            syncing = false;
        }
        presets.SelectionChanged += (_, _) =>
        {
            if (syncing || presets.SelectedIndex <= 0) return;
            var p = ArtboardPresets.All[presets.SelectedIndex - 1];
            syncing = true;
            (width.Value, height.Value) = (p.Width, p.Height);
            syncing = false;
        };
        width.ValueChanged += (_, _) => { if (!syncing) SelectPreset(); };
        height.ValueChanged += (_, _) => { if (!syncing) SelectPreset(); };
        SelectPreset();

        if (!await ShowSimpleDialog("New Artboard", "Create", Row("Name", name), Row("Size", presets), Row("Width (px)", width),
                Row("Height (px)", height), Row("Background", background)))
            return null;
        var kind = (ArtboardBackground)(Math.Max(0, background.SelectedIndex) + 1);
        int w = (int)(width.Value ?? defaults.Width), h = (int)(height.Value ?? defaults.Height);
        return new NewArtboardRequest(string.IsNullOrWhiteSpace(name.Text) ? defaults.Name : name.Text!, w, h,
            ArtboardPresets.Matching(w, h)?.Name ?? "Custom", kind,
            kind == ArtboardBackground.Custom ? (Editor.BackgroundColor.R, Editor.BackgroundColor.G, Editor.BackgroundColor.B) : defaults.Color);
    }
}
