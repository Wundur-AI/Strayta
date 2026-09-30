using Strayta.Core;
using Strayta.Editor.Controls;

namespace Strayta.Editor.Views;

// Artboard names on the canvas and the Artboard tool's gestures go from the canvas to the document.
public partial class DocumentView
{
    private void WireArtboards()
    {
        Canvas.ArtboardsSource = () => _vm is { } vm && Artboards.Any(vm.Model)
            ? vm.ArtboardOverlays().Select(a => new CanvasArtboard(a.Group, a.Name, a.Rect.Left, a.Rect.Top, a.Rect.Right, a.Rect.Bottom, a.IsSelected)).ToList()
            : [];
        Canvas.ArtboardDrawn += (l, t, r, b) =>
        {
            if (_vm is not { } vm) return;
            var preset = ArtboardPresets.Matching(r - l, b - t)?.Name ?? "Custom";
            vm.NewArtboard(new PixelRect(l, t, r, b), preset: preset);
        };
        Canvas.ArtboardSelected += key => _vm?.SelectArtboard((LayerGroup)key);
        Canvas.ArtboardMoved += (key, dx, dy) => _vm?.MoveArtboard((LayerGroup)key, dx, dy);
        Canvas.ArtboardResized += (key, l, t, r, b) => _vm?.ResizeArtboard((LayerGroup)key, new PixelRect(l, t, r, b));
        Canvas.ArtboardAddPressed += (key, side) => _vm?.AddAdjacentArtboard((LayerGroup)key, (ViewModels.ArtboardSide)side);
        Canvas.ArtboardGestureEnded += () => _vm?.Editor.NotifyArtboardOptions();
    }
}
