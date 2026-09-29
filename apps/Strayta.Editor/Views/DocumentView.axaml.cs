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
        WireEverydayTools(); // DocumentView.Everyday.cs
        WireCrop(); // DocumentView.Crop.cs
        WireRetouch(); // DocumentView.Retouch.cs
        WireSelectionTools(); // History Brush, Object Finder (DocumentView.SelectionTools.cs)
        WireType(); // DocumentView.Type.cs
        WirePaths(); // DocumentView.Paths.cs
        WireGuides(); // rulers, guides, grid, snapping, Info (DocumentView.Guides.cs)
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
        switch (kind)
        {
            case "fit": Canvas.FitToView(); break;
            case "in": Canvas.ZoomStep(zoomIn: true); break;
            case "out": Canvas.ZoomStep(zoomIn: false); break;
            default: Canvas.ActualSize(); break;
        }
    }

    private void OnShowStrayta(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => (DataContext as DocumentViewModel)?.ShowStraytaView();
}
