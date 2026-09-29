namespace Strayta.Editor.Views;

// Type tool gestures go from the canvas to the document (DocumentViewModel.TypeTool.cs).
public partial class DocumentView
{
    private void WireType()
    {
        Canvas.TypeEditAt = (x, y) =>
        {
            if (_vm?.TypeLayerAt(x, y) is not { } layer) return false;
            // Start after the click has been handled: editing may open the missing-fonts dialog, and a modal window
            // opened inside a pointer press on macOS got the same click delivered again when it closed, reopening it.
            var vm = _vm;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = vm.BeginEditTextAsync(layer, x, y));
            return true;
        };
        Canvas.TypeCreate += (x, y, box) => _vm?.BeginNewText(x, y, box);
        Canvas.TypeCommit += () => _vm?.CommitType();
        Canvas.TypeCancel += () => _vm?.CancelType();
    }

    private void OnDismissFonts(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.DismissMissingFonts();
}
