using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Dodge, Burn, Sponge, Blur, Sharpen and Smudge strokes go from the canvas to the document (DocumentViewModel.Toning.cs).
public partial class DocumentView
{
    private void WireToning()
    {
        var paint = Canvas.StrokeBegin;
        Canvas.StrokeBegin = (x, y) => _vm is { } vm && vm.Editor.IsToneOrFocusTool
            ? vm.BeginToolStroke(x, y)
            : paint?.Invoke(x, y) == true;
    }
}
