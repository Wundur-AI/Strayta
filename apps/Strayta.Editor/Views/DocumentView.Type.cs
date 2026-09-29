namespace Strayta.Editor.Views;

// Type tool gestures go from the canvas to the document (DocumentViewModel.TypeTool.cs).
public partial class DocumentView
{
    private void WireType()
    {
        Canvas.TypeEditAt = (x, y) =>
        {
            if (_vm?.TypeLayerAt(x, y) is not { } layer) return false;
            _ = _vm.BeginEditTextAsync(layer, x, y);
            return true;
        };
        Canvas.TypeCreate += (x, y, box) => _vm?.BeginNewText(x, y, box);
        Canvas.TypeCommit += () => _vm?.CommitType();
        Canvas.TypeCancel += () => _vm?.CancelType();
    }

    private void OnDismissFonts(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.DismissMissingFonts();
}
