using Avalonia.Input;

namespace Strayta.Editor.Controls;

// Pen input for painting: the pressure of the pointer event being handled, read by the document while it feeds the
// stroke (DocumentViewModel.Brush.cs). A mouse reports no pressure (Avalonia gives it a constant 0.5), so it paints at full.
public sealed partial class ImageCanvas
{
    /// <summary>Pen pressure (0..1) of the stroke point being delivered, or null when the pointer is not a pen.</summary>
    public float? StrokePressure { get; private set; }

    private void TrackPressure(PointerPoint point, IPointer pointer) =>
        StrokePressure = pointer.Type == PointerType.Pen ? point.Properties.Pressure : null;
}
