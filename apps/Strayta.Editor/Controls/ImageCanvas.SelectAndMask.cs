using Avalonia;
using Avalonia.Media;
using Vector2 = System.Numerics.Vector2;

namespace Strayta.Editor.Controls;

// The Select and Mask workspace's canvas: the selection being refined is set (the tools pick their add/subtract modes
// from it) but its outline is hidden, and the Marching Ants view draws the refined result's outline instead.
public sealed partial class ImageCanvas
{
    /// <summary>Draw marching ants around <see cref="Selection"/> (off in the Select and Mask workspace).</summary>
    public static readonly StyledProperty<bool> ShowSelectionOutlineProperty =
        AvaloniaProperty.Register<ImageCanvas, bool>(nameof(ShowSelectionOutline), true);

    public bool ShowSelectionOutline { get => GetValue(ShowSelectionOutlineProperty); set => SetValue(ShowSelectionOutlineProperty, value); }

    /// <summary>An outline (image coordinates) drawn as marching ants on its own: Select and Mask's Marching Ants view.</summary>
    public static readonly StyledProperty<IReadOnlyList<Vector2[]>?> AntsOutlineProperty =
        AvaloniaProperty.Register<ImageCanvas, IReadOnlyList<Vector2[]>?>(nameof(AntsOutline));

    public IReadOnlyList<Vector2[]>? AntsOutline { get => GetValue(AntsOutlineProperty); set => SetValue(AntsOutlineProperty, value); }

    private Geometry? _antsGeometry;
    private (IReadOnlyList<Vector2[]>? Loops, double Zoom, Vector Offset) _antsKey;

    private void OnWorkspacePropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == AntsOutlineProperty || change.Property == ShowSelectionOutlineProperty) InvalidateVisual();
    }

    private void RenderAntsOutline(DrawingContext context)
    {
        if (AntsOutline is not { Count: > 0 } loops) return;
        if (_antsGeometry is null || !ReferenceEquals(loops, _antsKey.Loops) || Zoom != _antsKey.Zoom || _offset != _antsKey.Offset)
        {
            _antsGeometry = LoopsGeometry(loops, closed: true);
            _antsKey = (loops, Zoom, _offset);
        }
        DrawAnts(context, _antsGeometry);
    }
}
