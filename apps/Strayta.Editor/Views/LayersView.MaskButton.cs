using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// The Layers panel's mask button with Option held: a mask that hides the selection (or, with nothing selected, hides
// everything), as in Photoshop. A plain click keeps its command (reveal).
public partial class LayersView
{
    private bool _maskOption;

    private void WireMaskButton()
    {
        MaskButton.AddHandler(PointerPressedEvent, (_, e) => _maskOption = e.KeyModifiers.HasFlag(KeyModifiers.Alt), RoutingStrategies.Tunnel, handledEventsToo: true);
        MaskButton.Click += (_, e) =>
        {
            if (!_maskOption || DataContext is not LayersToolViewModel { Editor: var editor }) return;
            _maskOption = false;
            editor.AddHidingLayerMask();
            e.Handled = true; // the button's reveal command must not run too
        };
    }
}
