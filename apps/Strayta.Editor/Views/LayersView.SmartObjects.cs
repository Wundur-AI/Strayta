using Avalonia.Controls;
using Avalonia.Input;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Smart objects in the Layers panel: double-clicking the thumbnail opens the contents, and the smart filter rows.
public partial class LayersView
{
    /// <summary>Double-clicking a smart object's thumbnail is Edit Contents, as in Photoshop.</summary>
    private void OnLayerThumbDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: LayerItemViewModel { IsSmartObject: true } item } || Editor is not { ActiveDocument: { } doc } editor) return;
        doc.SelectedLayer = item;
        editor.EditContentsCommand.Execute(null);
        e.Handled = true;
    }

    private async void OnSmartFilterEyePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (sender is Control { DataContext: SmartFilterRowViewModel row }) await row.Owner.ToggleSmartFilter(row);
    }

    private async void OnSmartFilterDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: SmartFilterRowViewModel row }) return;
        e.Handled = true;
        if (Editor?.ActiveDocument is { } doc) doc.SelectedLayer = row.Owner;
        await row.Owner.EditSmartFilter(row);
    }
}
