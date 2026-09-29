namespace Strayta.Editor.Views;

// Crop tool gestures go from the canvas to the document: double-click applies, a Straighten line levels the image.
public partial class DocumentView
{
    private void WireCrop()
    {
        Canvas.CropCommit += () => _ = _vm?.CommitCropAsync();
        Canvas.PerspectiveCropCommit += () => _ = _vm?.CommitPerspectiveCropAsync();
        Canvas.StraightenDrawn += (a, b) =>
        {
            if (_vm is null) return;
            _vm.CropBox?.Straighten(a.X, a.Y, b.X, b.Y);
            _vm.Editor.CropStraighten = false; // one line per click of the button, as in Photoshop
        };
    }
}
