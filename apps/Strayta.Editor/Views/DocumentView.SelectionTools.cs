using Strayta.Editor.Controls;

namespace Strayta.Editor.Views;

// History Brush strokes and the Object Finder's hover go from the canvas to the document
// (DocumentViewModel.HistoryBrush.cs, DocumentViewModel.ObjectFinder.cs).
public partial class DocumentView
{
    private void WireSelectionTools()
    {
        var paint = Canvas.StrokeBegin;
        Canvas.StrokeBegin = (x, y) => _vm is { } vm && vm.Editor.Tool == CanvasTool.HistoryBrush
            ? vm.BeginHistoryBrushStroke(x, y)
            : paint?.Invoke(x, y) == true;
        Canvas.ObjectHover += p => _vm?.HoverObject(p);
    }
}
