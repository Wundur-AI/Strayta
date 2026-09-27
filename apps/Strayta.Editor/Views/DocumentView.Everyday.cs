namespace Strayta.Editor.Views;

// Eyedropper samples, Paint Bucket clicks and Gradient drags go from the canvas to the document.
public partial class DocumentView
{
    private void WireEverydayTools()
    {
        Canvas.EyedropperBegin = (x, y, background) => _vm?.BeginEyedropper(x, y, background) == true;
        Canvas.EyedropperMove += (x, y) => _vm?.SampleEyedropper(x, y);
        Canvas.EyedropperEnd += () => _vm?.EndEyedropper();
        Canvas.EyedropperColor = background => _vm is { } vm
            ? background ? vm.Editor.BackgroundColor : vm.Editor.ForegroundColor
            : Avalonia.Media.Colors.Transparent;
        Canvas.PaintBucketClicked += (x, y) => _ = _vm?.PaintBucketAsync(x, y);
        Canvas.GradientBegin = (x, y) => _vm?.BeginGradient(x, y) == true;
        Canvas.GradientMove += (x, y) => _vm?.MoveGradient(x, y);
        Canvas.GradientEnd += () => _ = _vm?.EndGradientAsync();
    }
}
