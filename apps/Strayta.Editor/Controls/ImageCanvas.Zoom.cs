using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Strayta.Editor.Controls;

// Zooming as in Photoshop: the Zoom tool (click in, Option-click out, drag scrubs, or with Scrubby Zoom off drags a
// rectangle to zoom into), ⌘Space and ⌘⌥Space as a temporary zoom-in / zoom-out tool over any tool, and Animated Zoom:
// preset steps (clicks, ⌘+ / ⌘−) glide to the new level in a short ease instead of jumping.
public sealed partial class ImageCanvas
{
    /// <summary>Preset zoom steps glide over <see cref="ZoomAnimation"/> instead of jumping.</summary>
    public static readonly StyledProperty<bool> AnimatedZoomProperty =
        AvaloniaProperty.Register<ImageCanvas, bool>(nameof(AnimatedZoom), true);

    public bool AnimatedZoom { get => GetValue(AnimatedZoomProperty); set => SetValue(AnimatedZoomProperty, value); }

    /// <summary>Dragging with the Zoom tool zooms continuously; off, the drag draws a rectangle to zoom into.</summary>
    public static readonly StyledProperty<bool> ScrubbyZoomProperty =
        AvaloniaProperty.Register<ImageCanvas, bool>(nameof(ScrubbyZoom), true);

    public bool ScrubbyZoom { get => GetValue(ScrubbyZoomProperty); set => SetValue(ScrubbyZoomProperty, value); }

    /// <summary>How long an animated zoom step takes.</summary>
    public static readonly TimeSpan ZoomAnimation = TimeSpan.FromMilliseconds(140);

    private bool _tempZoom, _metaHeld, _spaceDown;
    private bool _zooming, _zoomDragged, _zoomAway, _zoomScrub;
    private Point _zoomAt, _zoomTo;
    private double _zoomStart;

    // The animation in progress: from/to zoom around a fixed screen point.
    private DispatcherTimer? _zoomTimer;
    private readonly Stopwatch _zoomClock = new();
    private double _animFrom, _animTo;
    private Point _animAnchor;

    /// <summary>True while ⌘Space (or ⌘⌥Space) turns the pointer into the zoom tool.</summary>
    internal bool TemporaryZoom => _tempZoom;

    /// <summary>The zoom level being animated to, or the current one.</summary>
    private double ZoomTarget => _zoomTimer is not null ? _animTo : Zoom;

    /// <summary>Tracks Space and ⌘ for the temporary zoom tool; returns true when the key is consumed.</summary>
    private bool ZoomKeyChanged(KeyEventArgs e, bool down)
    {
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key is Key.LWin or Key.RWin or Key.LeftCtrl or Key.RightCtrl) command = down;
        _metaHeld = command;
        if (e.Key == Key.Space) _spaceDown = down;
        bool temp = _spaceDown && _metaHeld;
        if (temp != _tempZoom)
        {
            _tempZoom = temp;
            if (temp) _spaceHeld = false; // ⌘Space zooms, it does not pan
            else if (_spaceDown && e.Key != Key.Space) _spaceHeld = true; // ⌘ released first: back to panning
            UpdateCursor();
        }
        if (_tempZoom && e.Key is Key.LeftAlt or Key.RightAlt) UpdateCursor();
        if (_tempZoom && e.Key == Key.Space)
        {
            e.Handled = true;
            return true;
        }
        return false;
    }

    /// <summary>The cursor while ⌘Space or ⌘⌥Space holds: the zoom in or zoom out magnifier.</summary>
    private Cursor TemporaryZoomCursor() => _altHeld
        ? _zoomOutCursor ??= IconCursor("M10.5 3.5a7 7 0 1 0 0 14a7 7 0 1 0 0-14Z M15.5 15.5L21 21 M7.5 10.5h6", 10.5, 10.5)
        : _zoomInCursor ??= IconCursor("M10.5 3.5a7 7 0 1 0 0 14a7 7 0 1 0 0-14Z M15.5 15.5L21 21 M7.5 10.5h6 M10.5 7.5v6", 10.5, 10.5);

    /// <summary>A press with the Zoom tool or the temporary zoom; false for every other press.</summary>
    private bool ZoomPressed(PointerPressedEventArgs e, PointerPointProperties props)
    {
        if (!_tempZoom && Tool != CanvasTool.Zoom) return false;
        _dragStart = null; // a zoom drag must never fall through to moving the layer
        if (!props.IsLeftButtonPressed || Source is null) return true;
        StopZoomAnimation();
        _zooming = true;
        _zoomDragged = false;
        _zoomAway = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        _zoomScrub = ScrubbyZoom;
        _zoomAt = _zoomTo = e.GetPosition(this);
        _zoomStart = Zoom;
        return true;
    }

    private bool ZoomMoved(PointerEventArgs e)
    {
        if (!_zooming) return false;
        var p = e.GetPosition(this);
        if (!_zoomDragged && Math.Abs(p.X - _zoomAt.X) + Math.Abs(p.Y - _zoomAt.Y) < 3) return true;
        _zoomDragged = true;
        _zoomTo = p;
        if (_zoomScrub)
        {
            // Scrubby zoom: right zooms in, left out, around where the drag started; 100 points doubles.
            ZoomAround(_zoomAt, _zoomStart * Math.Pow(2, (p.X - _zoomAt.X) / 100));
        }
        else InvalidateVisual(); // the zoom rectangle
        return true;
    }

    private void ZoomReleased(PointerReleasedEventArgs e)
    {
        if (!_zooming) return;
        _zooming = false;
        bool away = _zoomAway || e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (!_zoomDragged)
        {
            ZoomStep(!away, _zoomAt);
            return;
        }
        if (_zoomScrub) return;
        // Rectangle zoom: the dragged area fills the view.
        var r = new Rect(_zoomAt, _zoomTo).Normalize();
        InvalidateVisual();
        if (r.Width < 4 || r.Height < 4) return;
        double target = Math.Min(Bounds.Width / r.Width, Bounds.Height / r.Height) * Zoom;
        target = Math.Clamp(target, ZoomLevels[0], ZoomLevels[^1]);
        // Put the rectangle's center in the middle of the view at the new zoom.
        var centerImage = ToImage(r.Center);

        ZoomTo(target, new Point(Bounds.Width / 2, Bounds.Height / 2), centerImage);
    }

    /// <summary>Zooms to <paramref name="target"/> so that image point <paramref name="image"/> lands at screen point <paramref name="screen"/>.</summary>
    private void ZoomTo(double target, Point screen, Point image)
    {
        StopZoomAnimation();
        _offset = new Vector(screen.X - image.X * target, screen.Y - image.Y * target);
        Zoom = target;
        InvalidateVisual();
    }

    private void RenderZoomRectangle(DrawingContext context)
    {
        if (!_zooming || _zoomScrub || !_zoomDragged) return;
        var r = new Rect(_zoomAt, _zoomTo).Normalize();
        context.DrawRectangle(null, new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 3), r);
        context.DrawRectangle(null, new ImmutablePen(Brushes.White, 1), r);
    }

    /// <summary>
    /// Zooms to <paramref name="next"/> around <paramref name="anchor"/>: gliding there when Animated Zoom is on, at once
    /// otherwise. A step during an animation continues from where it was heading.
    /// </summary>
    private void ZoomAnimated(Point anchor, double next)
    {
        next = Math.Clamp(next, ZoomLevels[0], ZoomLevels[^1]);
        if (!AnimatedZoom || !IsEffectivelyVisible || Avalonia.Controls.TopLevel.GetTopLevel(this) is null)
        {
            StopZoomAnimation();
            ZoomAround(anchor, next);
            return;
        }
        _animFrom = Zoom;
        _animTo = next;
        _animAnchor = anchor;
        _zoomClock.Restart();
        _zoomTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (_, _) => ZoomFrame());
        _zoomTimer.Start();
        ZoomFrame();
    }

    private void ZoomFrame()
    {
        double t = Math.Min(1, _zoomClock.Elapsed.TotalMilliseconds / ZoomAnimation.TotalMilliseconds);
        double eased = 1 - Math.Pow(1 - t, 3); // ease out: fast start, gentle landing
        // Interpolated in log space, so zooming in and out feel alike.
        double z = Math.Exp(Math.Log(_animFrom) + (Math.Log(_animTo) - Math.Log(_animFrom)) * eased);
        ZoomAround(_animAnchor, t >= 1 ? _animTo : z);
        if (t >= 1) StopZoomAnimation();
    }

    private void StopZoomAnimation()
    {
        _zoomTimer?.Stop();
        _zoomTimer = null;
    }

    /// <summary>True while a zoom animation is running (for tests).</summary>
    internal bool IsZoomAnimating => _zoomTimer is not null;
}
