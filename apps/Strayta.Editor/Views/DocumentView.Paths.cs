namespace Strayta.Editor.Views;

// The shape, pen and path selection tools reach the document through IPathTools (DocumentViewModel.Paths.cs).
public partial class DocumentView
{
    private void WirePaths() =>
        DataContextChanged += (_, _) => Canvas.PathTools = DataContext as Controls.IPathTools;
}
