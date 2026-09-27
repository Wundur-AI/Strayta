using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Strayta.Editor.Controls;

public enum CanvasTool
{
    Move,
    Hand,
    Brush,
    Eraser,
    RectSelect,
    EllipseSelect,
    Lasso,
    MagicWand,
    QuickSelect,
}

/// <summary>
/// Displays a document over a transparency checkerboard. Wheel zooms around the cursor; the Hand tool,
/// the middle button or holding Space pans; the Move tool reports drags in image pixels.
/// </summary>
public sealed partial class ImageCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ImageCanvas, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ImageCanvas, double>(nameof(Zoom), 1.0);

    /// <summary>The document's size in pixels; a smaller preview bitmap is stretched to fill it.</summary>
    public static readonly StyledProperty<PixelSize> DocumentSizeProperty =
        AvaloniaProperty.Register<ImageCanvas, PixelSize>(nameof(DocumentSize));

    public static readonly StyledProperty<CanvasTool> ToolProperty =
        AvaloniaProperty.Register<ImageCanvas, CanvasTool>(nameof(Tool));

    private IBrush _checker = CreateChecker(Color.FromRgb(0x6B, 0x6B, 0x70), Color.FromRgb(0x4E, 0x4E, 0x53));
    private IBrush Backdrop => this.TryFindResource("St.Canvas", ActualThemeVariant, out var b) && b is IBrush brush ? brush : Brushes.Black;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += (_, _) => RefreshTheme();
        RefreshTheme();
        StartAnts();
    }

    private void RefreshTheme()
    {
        Color light = this.TryFindResource("St.CheckerLight", ActualThemeVariant, out var l) && l is Color lc ? lc : Colors.White;
        Color dark = this.TryFindResource("St.CheckerDark", ActualThemeVariant, out var d) && d is Color dc ? dc : Colors.LightGray;
        _checker = CreateChecker(light, dark);
        InvalidateVisual();
    }

    private Vector _offset;
    private Point? _dragStart;
    private Vector _panOrigin;
    private bool _panning, _spaceHeld;
    private Vector _moveRemainder;
    private PixelSize _lastSize;

    static ImageCanvas()
    {
        AffectsRender<ImageCanvas>(SourceProperty, ZoomProperty, DocumentSizeProperty, BrushSizeProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ImageCanvas>(true);
        FocusableProperty.OverrideDefaultValue<ImageCanvas>(true);
    }

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public CanvasTool Tool { get => GetValue(ToolProperty); set => SetValue(ToolProperty, value); }
    public PixelSize DocumentSize { get => GetValue(DocumentSizeProperty); set => SetValue(DocumentSizeProperty, value); }

    /// <summary>Raised when the zoom level changes, so the document can pick a preview resolution.</summary>
    public event Action<double>? ZoomChanged;

    private PixelSize ImageSize => DocumentSize.Width > 0 ? DocumentSize : Source?.PixelSize ?? default;

    /// <summary>Brush diameter in image pixels, for the brush outline cursor.</summary>
    public static readonly StyledProperty<double> BrushSizeProperty =
        AvaloniaProperty.Register<ImageCanvas, double>(nameof(BrushSize), 30);

    public double BrushSize { get => GetValue(BrushSizeProperty); set => SetValue(BrushSizeProperty, value); }

    /// <summary>Raised while dragging with the Move tool, with whole-pixel offsets since the last event.</summary>
    public event Action<int, int>? MoveDelta;

    /// <summary>Brush/eraser input in image coordinates: begin (returns false to refuse), continue, end.</summary>
    public Func<float, float, bool>? StrokeBegin { get; set; }
    public event Action<float, float>? StrokeMove;
    public event Action? StrokeEnd;

    private Point? _hover;
    private bool _stroking;

    private bool IsPaintTool => Tool is CanvasTool.Brush or CanvasTool.Eraser;

    private Point ToImage(Point screen) => new((screen.X - _offset.X) / Zoom, (screen.Y - _offset.Y) / Zoom);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == SourceProperty || change.Property == DocumentSizeProperty) && Source is not null && ImageSize != _lastSize)
        {
            _lastSize = ImageSize;
            FitToView();
        }
        if (change.Property == ZoomProperty) ZoomChanged?.Invoke(Zoom);
        if (change.Property == ToolProperty) UpdateCursor();
        OnSelectionPropertyChanged(change);
        OnWandPropertyChanged(change);
        if (change.Property == FreeTransformProperty) OnFreeTransformChanged(change);
    }

    public void FitToView()
    {
        var size = ImageSize;
        if (Source is null || size.Width == 0 || Bounds.Width <= 0) return;
        double z = Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height) * 0.95;
        Zoom = z >= 1 ? Math.Floor(z) : z;
        Center();
    }

    public void ActualSize()
    {
        Zoom = 1;
        Center();
    }

    private void Center()
    {
        if (Source is null) return;
        var size = ImageSize;
        _offset = new Vector((Bounds.Width - size.Width * Zoom) / 2, (Bounds.Height - size.Height * Zoom) / 2);
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.PreviousSize.Width <= 0) FitToView();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Backdrop, new Rect(Bounds.Size));
        if (Source is not { } bmp) return;

        var size = ImageSize;
        var dest = new Rect(_offset.X, _offset.Y, size.Width * Zoom, size.Height * Zoom);
        context.FillRectangle(_checker, dest);
        // Screen pixels per bitmap pixel: show hard pixels only when truly zoomed in on full-resolution data.
        double scale = Zoom * size.Width / bmp.PixelSize.Width;
        var mode = scale >= 2 && bmp.PixelSize == size ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
            context.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height), dest);
        RenderLiveOutline(context); // first: it decides whether the selection's own outline shows
        RenderSelection(context);
        DrawTransformBox(context);

        // Brush outline: a dark and a light ring so it stays visible over any colors.
        if ((IsPaintTool || Tool == CanvasTool.QuickSelect) && !_spaceHeld && FreeTransform is null && _hover is { } h)
        {
            double r = Math.Max(1.5, BrushSize * Zoom / 2);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1.5), h, r, r);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 0.75), h, r, r);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source is null) return;
        var p = e.GetPosition(this);
        double old = Zoom, next = Math.Clamp(old * Math.Pow(1.2, e.Delta.Y), 0.02, 64);
        _offset = new Vector(p.X - (p.X - _offset.X) * next / old, p.Y - (p.Y - _offset.Y) * next / old);
        Zoom = next;
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        _panning = Tool == CanvasTool.Hand || _spaceHeld || props.IsMiddleButtonPressed;
        _dragStart = e.GetPosition(this);
        if (TransformPressed(e))
        {
            _dragStart = null;
            e.Pointer.Capture(this);
            return;
        }
        if (!_panning && IsPaintTool && props.IsLeftButtonPressed)
        {
            var p = ToImage(e.GetPosition(this));
            _stroking = StrokeBegin?.Invoke((float)p.X, (float)p.Y) == true;
            if (!_stroking) _dragStart = null;
        }
        if (!_panning && IsSelectTool)
        {
            if (props.IsLeftButtonPressed) BeginSelection(e);
            else _dragStart = null; // other buttons must not fall through to moving the layer
        }
        if (!_panning && IsWandTool) WandPressed(e, props.IsLeftButtonPressed);
        _panOrigin = _offset;
        _moveRemainder = default;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (TransformMoved(e)) return;
        if (IsPaintTool || Tool == CanvasTool.QuickSelect)
        {
            _hover = e.GetPosition(this);
            InvalidateVisual();
        }
        if (_dragStart is not { } start) return;
        var pos = e.GetPosition(this);
        if (_selecting)
        {
            MoveSelection(e);
            return;
        }
        if (_quickSelecting)
        {
            QuickSelectMoved(e);
            return;
        }
        if (_stroking)
        {
            // Include coalesced intermediate points so fast strokes stay smooth.
            foreach (var point in e.GetIntermediatePoints(this))
            {
                var p = ToImage(point.Position);
                StrokeMove?.Invoke((float)p.X, (float)p.Y);
            }
            return;
        }
        if (_panning)
        {
            _offset = _panOrigin + (pos - start);
            InvalidateVisual();
            return;
        }

        // Convert screen movement to image pixels, carrying fractions so slow drags still move.
        _moveRemainder += (pos - start) / Zoom;
        _dragStart = pos;
        int dx = (int)Math.Truncate(_moveRemainder.X), dy = (int)Math.Truncate(_moveRemainder.Y);
        if (dx == 0 && dy == 0) return;
        _moveRemainder -= new Vector(dx, dy);
        MoveDelta?.Invoke(dx, dy);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        TransformReleased();
        if (_stroking)
        {
            _stroking = false;
            StrokeEnd?.Invoke();
        }
        if (_selecting) EndSelection(e);
        if (_quickSelecting) QuickSelectReleased();
        _dragStart = null;
        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        TransformModifiersChanged(e);
        if (e.Key == Key.Space && !_spaceHeld)
        {
            _spaceHeld = true;
            UpdateCursor();
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        TransformModifiersChanged(e);
        if (e.Key == Key.Space)
        {
            _spaceHeld = false;
            UpdateCursor();
            e.Handled = true;
        }
    }

    private void UpdateCursor() => Cursor = new Cursor(
        Tool == CanvasTool.Hand || _spaceHeld ? StandardCursorType.Hand
        : FreeTransform is not null ? StandardCursorType.Arrow
        : IsPaintTool || IsSelectTool || IsWandTool ? StandardCursorType.Cross
        : StandardCursorType.SizeAll);

    private static IBrush CreateChecker(Color light, Color dark)
    {
        var bmp = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            var px = new byte[16 * 16 * 4];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    var c = (x < 8) ^ (y < 8) ? dark : light;
                    int i = (y * 16 + x) * 4;
                    (px[i], px[i + 1], px[i + 2], px[i + 3]) = (c.R, c.G, c.B, 255);
                }
            for (int y = 0; y < 16; y++)
                System.Runtime.InteropServices.Marshal.Copy(px, y * 64, fb.Address + y * fb.RowBytes, 64);
        }
        return new ImageBrush(bmp) { TileMode = TileMode.Tile, DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute) };
    }
}
