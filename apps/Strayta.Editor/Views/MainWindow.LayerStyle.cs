using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Layer › Layer Style and its dialogs.
public partial class MainWindow : ILayerStyleDialogs
{
    /// <summary>
    /// Adds Layer › Layer Style in Photoshop's place (before New Adjustment Layer): Blending Options, the dialog's pages
    /// in Photoshop's order, then Copy, Paste and Clear Layer Style, Global Light and Scale Effects.
    /// </summary>
    private void AddLayerStyleMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var style = new NativeMenu();
        foreach (var page in Enum.GetValues<LayerStylePage>())
        {
            style.Items.Add(item(LayerStyleViewModel.NameOf(page) + "…", Editor.LayerStyleCommand, null, page.ToString()));
            if (page == LayerStylePage.BlendingOptions) style.Items.Add(new NativeMenuItemSeparator());
        }
        style.Items.Add(new NativeMenuItemSeparator());
        style.Items.Add(item("Copy Layer Style", Editor.CopyLayerStyleCommand, null, null));
        style.Items.Add(item("Paste Layer Style", Editor.PasteLayerStyleCommand, null, null));
        style.Items.Add(item("Clear Layer Style", Editor.ClearLayerStyleCommand, null, null));
        style.Items.Add(new NativeMenuItemSeparator());
        style.Items.Add(item("Global Light…", Editor.GlobalLightCommand, null, null));
        style.Items.Add(item("Scale Effects…", Editor.ScaleEffectsCommand, null, null));

        var layer = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Layer").Menu!;
        var adjustment = layer.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "New Adjustment Layer");
        int at = adjustment is null ? layer.Items.Count : layer.Items.IndexOf(adjustment);
        layer.Items.Insert(at, new NativeMenuItem("Layer Style") { Menu = style });

        if (Environment.GetEnvironmentVariable("STRAYTA_STYLEBENCH") is { Length: > 0 } bench)
            Opened += async (_, _) => await SelfTest.RunLayerStyleBenchmarkAsync(Editor, synthetic: bench is "new" or "bevel", bevel: bench == "bevel");
    }

    public async Task<bool> RunLayerStyleAsync(LayerStyleViewModel session) =>
        await new LayerStyleWindow(session).ShowDialog<bool>(this);

    public async Task<bool> RunGlobalLightAsync(GlobalLightViewModel session) =>
        await new GlobalLightWindow(session).ShowDialog<bool>(this);

    public async Task<bool> RunScaleEffectsAsync(ScaleEffectsViewModel session) =>
        await new ScaleEffectsWindow(session).ShowDialog<bool>(this);
}
