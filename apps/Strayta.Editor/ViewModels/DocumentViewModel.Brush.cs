using System.Diagnostics;
using Avalonia.Threading;
using Strayta.Core.Painting;

namespace Strayta.Editor.ViewModels;

// Brush input for every painting stroke (Brush, Eraser, mask painting, Clone Stamp, healing tools): pen pressure,
// Smoothing and Airbrush build-up. The stroke itself (dabs, flow, mode) is Core's PaintStroke; this feeds it points.
//
// Smoothing is Photoshop's pulled-string ("lazy mouse") model: the brush hangs on a string from the pointer and only
// moves when the pointer pulls the string taut, so jitter shorter than the string never reaches the canvas. The string
// is measured in screen pixels (up to 100 at 100%), so it feels the same at any zoom, and when the pointer rests the
// brush catches up with it smoothly (Photoshop's Stroke Catch-Up). Airbrush adds a dab where the brush rests about 30
// times a second, so paint builds up at the Flow rate while the button is held.
public sealed partial class DocumentViewModel
{
    private sealed class StrokeInput
    {
        public required PaintStroke Stroke { get; init; }
        public float BrushX, BrushY, PointerX, PointerY, Pressure = 1f;
        public float Radius;
        public long LastMove;
        public DispatcherTimer? Timer;
    }

    private StrokeInput? _input;

    /// <summary>Screen pixels of Smoothing's string at 100%.</summary>
    private const float SmoothingStringAt100 = 100f;

    /// <summary>
    /// Pen pressure (0..1) of the pointer event being handled, or null for a mouse; the view sets it (from
    /// Avalonia's <c>PointerPoint.Properties.Pressure</c>) so strokes can use it.
    /// </summary>
    internal Func<float?>? PenPressure { get; set; }

    private float CurrentPressure() => PenPressure?.Invoke() is { } p ? Math.Clamp(p, 0f, 1f) : 1f;

    /// <summary>
    /// Starts feeding <paramref name="stroke"/> from the pointer at (<paramref name="x"/>, <paramref name="y"/>): the
    /// first dab, and smoothing and airbrush state. Every stroke begins here.
    /// </summary>
    private void StartStrokeInput(PaintStroke stroke, float x, float y)
    {
        StopStrokeInput();
        float zoom = (float)Math.Max(1e-3, _viewZoom);
        var input = new StrokeInput
        {
            Stroke = stroke,
            BrushX = x, BrushY = y, PointerX = x, PointerY = y,
            Pressure = CurrentPressure(),
            Radius = (float)(Math.Clamp(Editor.BrushSmoothing, 0, 100) / 100 * SmoothingStringAt100) / zoom,
            LastMove = Stopwatch.GetTimestamp(),
        };
        _input = input;
        stroke.StrokeTo(x, y, input.Pressure);
        bool airbrush = Editor.StrokeAirbrush; // per tool (EditorViewModel.Toning.cs)
        if (airbrush || input.Radius > 0)
        {
            input.Timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Input, (_, _) => Tick(input, airbrush));
            input.Timer.Start();
        }
    }

    /// <summary>A pointer move while painting: the smoothed brush position (and pressure) goes to the stroke.</summary>
    private void FeedStroke(PaintStroke stroke, float x, float y)
    {
        if (_input is not { } input || !ReferenceEquals(input.Stroke, stroke))
        {
            stroke.StrokeTo(x, y, CurrentPressure());
            return;
        }
        input.Pressure = CurrentPressure();
        input.PointerX = x;
        input.PointerY = y;
        input.LastMove = Stopwatch.GetTimestamp();
        if (input.Radius <= 0)
        {
            (input.BrushX, input.BrushY) = (x, y);
            stroke.StrokeTo(x, y, input.Pressure);
            return;
        }
        // Pulled string: the brush moves only as far as the taut string drags it.
        float dx = x - input.BrushX, dy = y - input.BrushY, d = MathF.Sqrt(dx * dx + dy * dy);
        if (d <= input.Radius) return;
        float t = (d - input.Radius) / d;
        input.BrushX += dx * t;
        input.BrushY += dy * t;
        stroke.StrokeTo(input.BrushX, input.BrushY, input.Pressure);
    }

    private void Tick(StrokeInput input, bool airbrush)
    {
        if (!ReferenceEquals(_input, input) || !ReferenceEquals(_stroke, input.Stroke))
        {
            input.Timer?.Stop();
            return;
        }
        bool changed = false;
        // Stroke catch-up: after the pointer has rested briefly, the brush eases the rest of the way to it.
        if (input.Radius > 0 && Stopwatch.GetElapsedTime(input.LastMove).TotalMilliseconds > 60)
        {
            float dx = input.PointerX - input.BrushX, dy = input.PointerY - input.BrushY;
            if (dx * dx + dy * dy > 0.25f)
            {
                input.BrushX += dx * 0.3f;
                input.BrushY += dy * 0.3f;
                input.Stroke.StrokeTo(input.BrushX, input.BrushY, input.Pressure);
                changed = true;
            }
        }
        if (airbrush)
        {
            input.Stroke.Airbrush();
            changed = true;
        }
        if (changed) RequestRender();
    }

    /// <summary>Ends pointer input for the current stroke (release); the stroke keeps what it has.</summary>
    private void StopStrokeInput()
    {
        _input?.Timer?.Stop();
        _input = null;
    }

    /// <summary>Where the brush is (after smoothing) while painting, for the canvas's string indicator; null otherwise.</summary>
    public (float X, float Y)? SmoothedBrushPosition => _input is { Radius: > 0 } i ? (i.BrushX, i.BrushY) : null;
}
