using Avalonia.Data;
using Avalonia.Input;
using Strayta.Editor.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// The Move tool's Auto-Select press and Show Transform Controls box (ImageCanvas.MoveTool.cs, DocumentViewModel.MoveTool.cs).
public partial class DocumentView
{
    private DocumentViewModel? _layersVm;

    private void WireLayers()
    {
        Canvas.MovePress += (p, modifiers) =>
        {
            bool command = OperatingSystem.IsMacOS() ? modifiers.HasFlag(KeyModifiers.Meta) : modifiers.HasFlag(KeyModifiers.Control);
            _vm?.MoveToolPressed(p.X, p.Y, command, modifiers.HasFlag(KeyModifiers.Alt), modifiers.HasFlag(KeyModifiers.Shift));
        };
        Canvas.BeginTransformFromControls = () => _vm?.BeginFreeTransform() == true;
        Canvas.Bind(ImageCanvas.ShowTransformControlsProperty, new Binding("Editor.MoveShowTransformControls"));
        Canvas.PropertyChanged += (_, e) =>
        {
            if (e.Property == ImageCanvas.ShowTransformControlsProperty) Canvas.InvalidateVisual();
        };
        DataContextChanged += (_, _) =>
        {
            if (_layersVm is not null) _layersVm.LayerSelectionChanged -= Canvas.InvalidateVisual;
            _layersVm = DataContext as DocumentViewModel;
            if (_layersVm is not null) _layersVm.LayerSelectionChanged += Canvas.InvalidateVisual;
        };
    }
}
