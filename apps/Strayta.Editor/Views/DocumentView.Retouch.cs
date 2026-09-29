using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Clone Stamp, Healing Brush and Spot Healing Brush: strokes, the Option-click source point and the canvas's source
// overlay go from the canvas to the document (DocumentViewModel.Retouch.cs). Pen pressure for every painting stroke
// goes the same way (DocumentViewModel.Brush.cs).
public partial class DocumentView
{
    private void WireRetouch()
    {
        var paint = Canvas.StrokeBegin;
        Canvas.StrokeBegin = (x, y) => _vm is { } vm && vm.Editor.IsRetouchTool
            ? vm.BeginRetouchStroke(x, y)
            : paint?.Invoke(x, y) == true;
        Canvas.RetouchSourcePicked += (x, y) => _vm?.SetCloneSource(x, y);
        Canvas.RetouchSource = (x, y) => _vm?.CloneOverlayFor(x, y);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DocumentViewModel vm) vm.PenPressure = () => Canvas.StrokePressure;
        };
    }
}
