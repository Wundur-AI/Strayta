using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Strayta.Core.Selection;
using Vector2 = System.Numerics.Vector2;

namespace Strayta.Editor.Controls;

// Magic Wand clicks and Quick Selection drags, and the live outline a Quick Selection drag shows while it grows.
public sealed partial class ImageCanvas
{
    /// <summary>
    /// The outline of the selection a Quick Selection drag would make (image coordinates), drawn as marching ants in
    /// place of the current selection's outline while it is set.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<Vector2[]>?> LiveSelectionOutlineProperty =
        AvaloniaProperty.Register<ImageCanvas, IReadOnlyList<Vector2[]>?>(nameof(LiveSelectionOutline));

    public IReadOnlyList<Vector2[]>? LiveSelectionOutline { get => GetValue(LiveSelectionOutlineProperty); set => SetValue(LiveSelectionOutlineProperty, value); }

    /// <summary>What a Quick Selection stroke does without modifier keys (the options bar's New, Add or Subtract).</summary>
    public static readonly StyledProperty<SelectionMode> QuickSelectDefaultModeProperty =
        AvaloniaProperty.Register<ImageCanvas, SelectionMode>(nameof(QuickSelectDefaultMode), SelectionMode.Add);

    public SelectionMode QuickSelectDefaultMode { get => GetValue(QuickSelectDefaultModeProperty); set => SetValue(QuickSelectDefaultModeProperty, value); }

    /// <summary>A Magic Wand click at an image pixel, with the mode its modifier keys chose.</summary>
    public event Action<int, int, SelectionMode>? MagicWandClicked;

    /// <summary>Quick Selection input in image coordinates: begin (returns false to refuse), continue, end.</summary>
    public Func<float, float, SelectionMode, bool>? QuickSelectBegin { get; set; }
    public event Action<float, float>? QuickSelectMove;
    public event Action? QuickSelectEnd;

    private bool _quickSelecting;

    // After a drag the last live outline stays up until the new selection's own outline has been traced.
    private IReadOnlyList<Vector2[]>? _lingering;
    private DateTime _lingerUntil;
    private Geometry? _liveGeometry;
    private (IReadOnlyList<Vector2[]>? Loops, double Zoom, Vector Offset) _liveGeometryKey;

    private bool IsWandTool => Tool is CanvasTool.MagicWand or CanvasTool.QuickSelect;

    private void OnWandPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != LiveSelectionOutlineProperty) return;
        if (change.NewValue is not null)
        {
            _lingering = null;
            _hideOutline = true;
        }
        else if (change.OldValue is IReadOnlyList<Vector2[]> last)
        {
            _lingering = last;
            _lingerUntil = DateTime.UtcNow.AddSeconds(1);
        }
        InvalidateVisual();
    }

    /// <summary>
    /// Photoshop's modes: with a selection, Shift adds, Option subtracts and both intersect. Quick Selection adds by
    /// default once something is selected (its options bar switches to Add after the first stroke) and has no
    /// intersect.
    /// </summary>
    private void WandPressed(PointerPressedEventArgs e, bool left)
    {
        _dragStart = null; // a wand drag must never fall through to moving the layer
        if (!left) return;
        var mods = e.KeyModifiers;
        bool shift = mods.HasFlag(KeyModifiers.Shift), alt = mods.HasFlag(KeyModifiers.Alt), has = Selection is not null;
        var p = ToImage(e.GetPosition(this));
        if (Tool == CanvasTool.MagicWand)
        {
            var mode = !has ? SelectionMode.Replace
                : shift && alt ? SelectionMode.Intersect
                : shift ? SelectionMode.Add
                : alt ? SelectionMode.Subtract
                : SelectionMode.Replace;
            MagicWandClicked?.Invoke((int)Math.Floor(p.X), (int)Math.Floor(p.Y), mode);
            return;
        }
        // The options bar's mode (New, Add, Subtract) unless Shift adds or Option subtracts.
        var brushMode = alt ? SelectionMode.Subtract : shift ? SelectionMode.Add : QuickSelectDefaultMode;
        if (!has && brushMode != SelectionMode.Subtract) brushMode = SelectionMode.Replace;
        _quickSelecting = QuickSelectBegin?.Invoke((float)p.X, (float)p.Y, brushMode) == true;
        if (_quickSelecting) _dragStart = e.GetPosition(this);
    }

    private void QuickSelectMoved(PointerEventArgs e)
    {
        foreach (var point in e.GetIntermediatePoints(this))
        {
            var p = ToImage(point.Position);
            QuickSelectMove?.Invoke((float)p.X, (float)p.Y);
        }
    }

    private void QuickSelectReleased()
    {
        _quickSelecting = false;
        QuickSelectEnd?.Invoke();
    }

    private void RenderLiveOutline(DrawingContext context)
    {
        var loops = LiveSelectionOutline;
        if (loops is null && _lingering is not null)
        {
            // The new selection's outline is ready (or never coming): hand over to it, in this same frame.
            if (ReferenceEquals(_outlineSource, Selection) || DateTime.UtcNow > _lingerUntil)
            {
                _lingering = null;
                _hideOutline = false;
                return;
            }
            loops = _lingering;
        }
        if (loops is null || loops.Count == 0) return;
        if (_liveGeometry is null || !ReferenceEquals(loops, _liveGeometryKey.Loops) || Zoom != _liveGeometryKey.Zoom || _offset != _liveGeometryKey.Offset)
        {
            _liveGeometry = LoopsGeometry(loops, closed: true);
            _liveGeometryKey = (loops, Zoom, _offset);
        }
        DrawAnts(context, _liveGeometry);
    }
}
