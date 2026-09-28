using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Layer styles in the Layers panel: the fx menu, double-clicks that open Layer Style, the fx badge and effect eyes.
public partial class LayersView
{
    private EditorViewModel? Editor => (DataContext as LayersToolViewModel)?.Editor;

    private void OpenLayerStyle(string page) => Editor?.LayerStyleCommand.Execute(page);

    /// <summary>The footer's fx menu, as in Photoshop.</summary>
    private void OnLayerStyleMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string page }) OpenLayerStyle(page);
    }

    /// <summary>Double-clicking a row's empty area opens Blending Options (the name renames, the thumbnails target).</summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled || sender is not Control { DataContext: LayerItemViewModel item }) return;
        if (Editor?.ActiveDocument is { } doc) doc.SelectedLayer = item;
        OpenLayerStyle(nameof(LayerStylePage.BlendingOptions));
        e.Handled = true;
    }

    /// <summary>A click on the fx badge lists the layer's effects under it (or hides them again).</summary>
    private void OnFxPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
        if (sender is Control { DataContext: LayerItemViewModel item }) item.EffectsExpanded = !item.EffectsExpanded;
    }

    /// <summary>Double-clicking the fx badge or an effect opens Layer Style at that effect's page.</summary>
    private void OnFxDoubleTapped(object? sender, TappedEventArgs e)
    {
        var (item, page) = (sender as Control)?.DataContext switch
        {
            LayerItemViewModel i => (i, LayerStylePage.BlendingOptions),
            EffectRowViewModel { IsMaster: true } r => (r.Owner, LayerStylePage.BlendingOptions),
            EffectRowViewModel r when r.Owner.Node.Effects is { } fx => (r.Owner, LayerStyleViewModel.PageOf(fx.Items[r.Index])),
            _ => (null, LayerStylePage.BlendingOptions),
        };
        if (item is null) return;
        if (Editor?.ActiveDocument is { } doc) doc.SelectedLayer = item;
        OpenLayerStyle(page.ToString());
        e.Handled = true;
    }

    /// <summary>An effect's eye (or the "Effects" eye) shows or hides it, as one undo step.</summary>
    private void OnEffectEyePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is Control { DataContext: EffectRowViewModel row }) row.Owner.ToggleEffect(row);
        e.Handled = true;
    }
}
