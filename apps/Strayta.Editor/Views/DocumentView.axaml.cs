using Avalonia.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

public partial class DocumentView : UserControl
{
    private DocumentViewModel? _vm;

    public DocumentView()
    {
        InitializeComponent();
        Canvas.MoveDelta += (dx, dy) => _vm?.MoveSelected(dx, dy);
        Canvas.ZoomChanged += zoom => _vm?.SetViewZoom(zoom);
        Canvas.StrokeBegin = (x, y) => _vm is { } vm &&
            vm.BeginStroke(x, y, vm.Editor.CurrentBrush, vm.Editor.CurrentColor, erase: vm.Editor.Tool == Controls.CanvasTool.Eraser);
        Canvas.StrokeMove += (x, y) => _vm?.ContinueStroke(x, y);
        Canvas.StrokeEnd += () => _ = _vm?.EndStrokeAsync();
        Canvas.SelectionGestureCompleted += g => _ = _vm?.ApplySelectionGestureAsync(g);
        Canvas.TransformCommit += () => _ = _vm?.CommitTransformAsync();
        WireWandTools();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.ZoomRequested -= OnZoom;
            _vm = DataContext as DocumentViewModel;
            if (_vm is not null)
            {
                _vm.ZoomRequested += OnZoom;
                _vm.SetViewZoom(Canvas.Zoom);
            }
        };
    }

    private void OnDismiss(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.DismissPaintBlock();
    private void OnShow(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.ShowSelected();
    private void OnRasterize(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.RasterizeSelected();
    private void OnNewLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.NewLayerForPainting();

    private void OnZoom(string kind)
    {
        if (kind == "fit") Canvas.FitToView();
        else Canvas.ActualSize();
    }
}
