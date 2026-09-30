using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Strayta.Editor.Editing;

namespace Strayta.Editor.Controls;

// Puppet Warp on the canvas: the deformed mesh (Show Mesh) and the pins, a click adds a pin and drags it, Option-click
// removes one. Panning (Space, middle button, Hand tool) still works meanwhile.
public sealed partial class ImageCanvas
{
    public static readonly StyledProperty<PuppetWarpSession?> PuppetWarpProperty =
        AvaloniaProperty.Register<ImageCanvas, PuppetWarpSession?>(nameof(PuppetWarp));

    /// <summary>The open Puppet Warp to draw and drive, or null.</summary>
    public PuppetWarpSession? PuppetWarp { get => GetValue(PuppetWarpProperty); set => SetValue(PuppetWarpProperty, value); }

    private const double PinRadius = 5;

    // Registered from a field initializer: the class's static constructor lives in ImageCanvas.cs.
    private static readonly IDisposable PuppetHook = PuppetWarpProperty.Changed.AddClassHandler<ImageCanvas>((canvas, change) =>
        {
            if (change.OldValue is PuppetWarpSession old) old.Changed -= canvas.InvalidateVisual;
            if (change.NewValue is PuppetWarpSession now) now.Changed += canvas.InvalidateVisual;
            canvas.UpdateCursor();
            canvas.InvalidateVisual();
        });

    private void DrawPuppet(DrawingContext context)
    {
        if (PuppetWarp is not { } s) return;
        var x = s.X;
        var y = s.Y;
        if (s.ShowMesh)
        {
            var mesh = new StreamGeometry();
            using (var g = mesh.Open())
            {
                // Each edge once: the grid's edges run between a vertex and a higher-numbered neighbor.
                var neighbors = s.Mesh.Neighbors;
                for (int v = 0; v < neighbors.Length; v++)
                    foreach (int w in neighbors[v])
                    {
                        if (w < v) continue;
                        g.BeginFigure(ToScreen((x[v], y[v])), false);
                        g.LineTo(ToScreen((x[w], y[w])));
                        g.EndFigure(false);
                    }
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 1), mesh);
        }
        var edge = new Pen(Brushes.Black, 1.2);
        for (int i = 0; i < s.Pins.Count; i++)
        {
            var p = ToScreen((s.Pins[i].X, s.Pins[i].Y));
            context.DrawEllipse(Brushes.White, edge, p, PinRadius, PinRadius);
            context.DrawEllipse(i == s.SelectedPin ? Brushes.Black : Brushes.Gold, null, p, PinRadius - 2.2, PinRadius - 2.2);
        }
    }

    private bool PuppetPressed(PointerPressedEventArgs e)
    {
        if (PuppetWarp is not { } s || _panning) return false;
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return true;
        var p = ToImage(e.GetPosition(this));
        s.Press(p.X, p.Y, (PinRadius + 2) / Zoom, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        return true;
    }

    private bool PuppetMoved(PointerEventArgs e)
    {
        if (PuppetWarp is not { } s || _panning) return false;
        var p = ToImage(e.GetPosition(this));
        if (s.IsDragging) s.DragTo(p.X, p.Y);
        else if (!_spaceHeld && Tool != CanvasTool.Hand)
        {
            bool onPin = s.HitPin(p.X, p.Y, (PinRadius + 2) / Zoom) >= 0;
            Cursor = new Cursor(onPin ? StandardCursorType.Hand : StandardCursorType.Cross);
        }
        return true;
    }

    private void PuppetReleased() => PuppetWarp?.EndDrag();
}
