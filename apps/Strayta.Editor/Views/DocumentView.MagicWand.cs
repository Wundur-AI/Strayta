namespace Strayta.Editor.Views;

// Magic Wand clicks and Quick Selection drags go from the canvas to the document.
public partial class DocumentView
{
    private void WireWandTools()
    {
        Canvas.MagicWandClicked += (x, y, mode) => _ = _vm?.MagicWandAsync(x, y, mode);
        Canvas.QuickSelectBegin = (x, y, mode) => _vm?.BeginQuickSelection(x, y, mode) == true;
        Canvas.QuickSelectMove += (x, y) => _vm?.ContinueQuickSelection(x, y);
        Canvas.QuickSelectEnd += () => _ = _vm?.EndQuickSelectionAsync();
    }
}
