using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Strayta.Core;
using Strayta.Editor.Editing;

namespace Strayta.Editor.Controls;

// Rulers, guides and the grid, drawn by the canvas over the image. The rulers are strips along the top and left edge of
// the canvas (content under them is hidden, as it is behind Photoshop's rulers), with ticks and labels that follow zoom
// and pan, a marker for the pointer, and the origin corner. Guides are dragged out of the rulers (Option swaps the
// direction), moved with the Move tool or with ⌘ over any tool, and deleted by dragging them back onto a ruler. All of
// it is drawn as a few line geometries per frame in screen space, so it adds nothing to the document render.
public sealed partial class ImageCanvas
{
    public static readonly StyledProperty<bool> ShowRulersProperty = AvaloniaProperty.Register<ImageCanvas, bool>(nameof(ShowRulers));
    public static readonly StyledProperty<bool> ShowGuidesProperty = AvaloniaProperty.Register<ImageCanvas, bool>(nameof(ShowGuides), true);
    public static readonly StyledProperty<bool> LockGuidesProperty = AvaloniaProperty.Register<ImageCanvas, bool>(nameof(LockGuides));
    public static readonly StyledProperty<bool> ShowGridProperty = AvaloniaProperty.Register<ImageCanvas, bool>(nameof(ShowGrid));

    public static readonly StyledProperty<IReadOnlyList<Guide>> GuidesProperty =
        AvaloniaProperty.Register<ImageCanvas, IReadOnlyList<Guide>>(nameof(Guides), []);

    public static readonly StyledProperty<RulerUnit> RulerUnitProperty = AvaloniaProperty.Register<ImageCanvas, RulerUnit>(nameof(RulerUnit));

    /// <summary>Pixels per inch, for physical ruler units.</summary>
    public static readonly StyledProperty<double> DocumentResolutionProperty =
        AvaloniaProperty.Register<ImageCanvas, double>(nameof(DocumentResolution), 72);

    /// <summary>Where the rulers' zero is, in document pixels (the grid starts there too, as in Photoshop).</summary>
    public static readonly StyledProperty<Point> RulerOriginProperty = AvaloniaProperty.Register<ImageCanvas, Point>(nameof(RulerOrigin));

    /// <summary>Gridline spacing in document pixels.</summary>
    public static readonly StyledProperty<double> GridSpacingProperty = AvaloniaProperty.Register<ImageCanvas, double>(nameof(GridSpacing), 72);
    public static readonly StyledProperty<int> GridSubdivisionsProperty = AvaloniaProperty.Register<ImageCanvas, int>(nameof(GridSubdivisions), 4);

    public bool ShowRulers { get => GetValue(ShowRulersProperty); set => SetValue(ShowRulersProperty, value); }
    public bool ShowGuides { get => GetValue(ShowGuidesProperty); set => SetValue(ShowGuidesProperty, value); }
    public bool LockGuides { get => GetValue(LockGuidesProperty); set => SetValue(LockGuidesProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public IReadOnlyList<Guide> Guides { get => GetValue(GuidesProperty); set => SetValue(GuidesProperty, value); }
    public RulerUnit RulerUnit { get => GetValue(RulerUnitProperty); set => SetValue(RulerUnitProperty, value); }
    public double DocumentResolution { get => GetValue(DocumentResolutionProperty); set => SetValue(DocumentResolutionProperty, value); }
    public Point RulerOrigin { get => GetValue(RulerOriginProperty); set => SetValue(RulerOriginProperty, value); }
    public double GridSpacing { get => GetValue(GridSpacingProperty); set => SetValue(GridSpacingProperty, value); }
    public int GridSubdivisions { get => GetValue(GridSubdivisionsProperty); set => SetValue(GridSubdivisionsProperty, value); }

    /// <summary>A guide was dragged out of a ruler onto the canvas.</summary>
    public event Action<Guide>? GuideAdded;

    /// <summary>Guide <c>index</c> was dragged to a new position (document pixels).</summary>
    public event Action<int, double>? GuideMoved;

    /// <summary>Guide <c>index</c> was dragged back onto a ruler (or off the canvas).</summary>
    public event Action<int>? GuideDeleted;

    /// <summary>The ruler origin was dragged from the corner (or reset by double-clicking it).</summary>
    public event Action<Point>? RulerOriginChanged;

    /// <summary>A unit was picked from a ruler's context menu.</summary>
    public event Action<RulerUnit>? RulerUnitChosen;

    /// <summary>
    /// The pointer moved (document coordinates; null when it left the canvas), with the box being drawn if there is one
    /// (marquee, crop, text box), for the Info panel.
    /// </summary>
    public event Action<Point?, Rect?>? PointerInfo;

    /// <summary>Rulers are this many points wide.</summary>
    public const double RulerSize = 16;

    /// <summary>Guides are picked up within this many screen points.</summary>
    private const double GuideGrab = 4;

    private enum GuideDrag { None, New, Move, Origin }

    private GuideDrag _guideDrag;
    private int _guideIndex = -1;
    private bool _guideFromTop, _guideCursorShown;
    private GuideOrientation _guideOrientation;
    private double _guidePosition, _guideGrab;
    private Point _originDrag;
    private Point? _pointer;

    private static readonly ImmutableSolidColorBrush GuideBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x00, 0xFF, 0xFF)); // Photoshop's default Cyan
    private static readonly IPen GuidePen = new ImmutablePen(GuideBrush, 1);
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x80, 0x80, 0x80)), 1);
    private static readonly IPen GridSubPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)), 1);
    private static readonly IPen OriginPen = new ImmutablePen(Brushes.Black, 1, new ImmutableDashStyle([3, 3], 0));
    private static readonly IPen OriginPenLight = new ImmutablePen(Brushes.White, 1);

    /// <summary>The space the rulers take on the top and left (0 without rulers).</summary>
    private double RulerInset => ShowRulers ? RulerSize : 0;

    private void OnGuidesPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        var p = change.Property;
        if (p == ShowRulersProperty || p == ShowGuidesProperty || p == ShowGridProperty || p == GuidesProperty || p == RulerUnitProperty
            || p == DocumentResolutionProperty || p == RulerOriginProperty || p == GridSpacingProperty || p == GridSubdivisionsProperty)
            InvalidateVisual();
        if (p == LockGuidesProperty || p == ShowGuidesProperty) _guideCursorShown = false;
    }

    /// <summary>
    /// Showing or hiding the rulers takes or gives back a strip on the top and left: the image moves by half of it, so it
    /// stays centered in the space left over.
    /// </summary>
    internal void KeepViewForRulers(bool shown)
    {
        if (Source is null || _lastSize == default) return;
        double d = (shown ? RulerSize : -RulerSize) / 2;
        _offset += new Vector(d, d);
        InvalidateVisual();
    }

    // ---- Geometry ------------------------------------------------------------------------------------------

    private bool InTopRuler(Point p) => ShowRulers && p.Y >= 0 && p.Y < RulerSize && p.X >= RulerSize && p.X < Bounds.Width;
    private bool InLeftRuler(Point p) => ShowRulers && p.X >= 0 && p.X < RulerSize && p.Y >= RulerSize && p.Y < Bounds.Height;
    private bool InRulerCorner(Point p) => ShowRulers && p.X >= 0 && p.X < RulerSize && p.Y >= 0 && p.Y < RulerSize;
    private bool InRulers(Point p) => InTopRuler(p) || InLeftRuler(p) || InRulerCorner(p);

    private double ScreenX(double x) => _offset.X + x * Zoom;
    private double ScreenY(double y) => _offset.Y + y * Zoom;

    /// <summary>The guide under a screen point (within <see cref="GuideGrab"/>), or -1.</summary>
    internal int GuideAt(Point screen)
    {
        int best = -1;
        double bestDistance = GuideGrab;
        var guides = Guides;
        for (int i = 0; i < guides.Count; i++)
        {
            double d = guides[i].IsHorizontal ? Math.Abs(ScreenY(guides[i].Position) - screen.Y) : Math.Abs(ScreenX(guides[i].Position) - screen.X);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Whether a press here with these keys picks up a guide: the Move tool, or ⌘ with any tool, and guides showing and unlocked.</summary>
    private bool CanGrabGuide(KeyModifiers modifiers) =>
        ShowGuides && !LockGuides && FreeTransform is null && CropBox is null && !_spaceHeld && !_tempZoom
        && (Tool == CanvasTool.Move || IsCommand(modifiers));

    // ---- Input ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A press on the rulers or on a guide. Returns true when it was handled here (the caller captures the pointer).
    /// Also resets the snapping state every drag starts from.
    /// </summary>
    private bool GuidesPressed(PointerPressedEventArgs e, PointerPointProperties props)
    {
        ResetDragSnapping(); // ImageCanvas.Snapping.cs
        return GuidePress(e.GetPosition(this), e.KeyModifiers, e.ClickCount, props.IsLeftButtonPressed, props.IsRightButtonPressed);
    }

    /// <summary>The press logic, callable without a pointer event (the self-test drives it).</summary>
    internal bool GuidePress(Point pos, KeyModifiers modifiers, int clickCount, bool left, bool right)
    {
        if (Source is null) return false;
        if (InRulerCorner(pos))
        {
            if (left && clickCount == 2)
            {
                RulerOriginChanged?.Invoke(default); // double-click the corner: back to the image's top left
                return true;
            }
            if (left)
            {
                _guideDrag = GuideDrag.Origin;
                _dragSnapper = BeginSnap();
                _originDrag = ToImage(pos);
            }
            return true;
        }
        if (InTopRuler(pos) || InLeftRuler(pos))
        {
            if (right) ShowRulerUnitsMenu();
            else if (left)
            {
                _guideDrag = GuideDrag.New;
                _guideFromTop = InTopRuler(pos);
                _guideGrab = 0;
                _dragSnapper = BeginSnap();
                UpdateGuideDrag(pos, modifiers);
            }
            return true;
        }
        if (!left || _panning || !CanGrabGuide(modifiers) || GuideAt(pos) is not (>= 0 and var index)) return false;
        _guideDrag = GuideDrag.Move;
        _guideIndex = index;
        _guideOrientation = Guides[index].Orientation;
        _dragSnapper = BeginSnap(excludeGuide: index);
        // The guide keeps its distance from the pointer, so a click without a drag leaves it exactly where it was.
        var at = ToImage(pos);
        _guidePosition = Guides[index].Position;
        _guideGrab = _guidePosition - (Guides[index].IsHorizontal ? at.Y : at.X);
        InvalidateVisual();
        return true;
    }

    /// <summary>Pointer movement: the rulers' pointer marker, the Info panel, guide and origin drags and the guide cursor.</summary>
    private bool GuidesMoved(PointerEventArgs e) => GuideMove(e.GetPosition(this), e.KeyModifiers);

    internal bool GuideMove(Point pos, KeyModifiers modifiers)
    {
        _pointer = pos;
        if (ShowRulers) InvalidateVisual(); // the marker follows the pointer
        PointerInfo?.Invoke(_guideDrag == GuideDrag.None && InRulers(pos) ? null : ToImage(pos), LiveBox());
        switch (_guideDrag)
        {
            case GuideDrag.New or GuideDrag.Move:
                UpdateGuideDrag(pos, modifiers);
                return true;
            case GuideDrag.Origin:
                _originDrag = SnapDrag(ToImage(pos), modifiers);
                InvalidateVisual();
                return true;
        }
        if (_dragStart is not null) return false; // another drag is under way
        if (InRulers(pos))
        {
            Cursor = new Cursor(StandardCursorType.Arrow);
            _guideCursorShown = true;
            return true;
        }
        if (CanGrabGuide(modifiers) && GuideAt(pos) is >= 0 and var index)
        {
            Cursor = new Cursor(Guides[index].IsHorizontal ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast);
            _guideCursorShown = true;
            return Tool == CanvasTool.Move; // other tools keep their own hover (brush outline) with ⌘ held
        }
        if (_guideCursorShown)
        {
            _guideCursorShown = false;
            UpdateCursor();
        }
        return false;
    }

    /// <summary>Follows the pointer with the dragged guide: snapped, or on the rulers' ticks with Shift.</summary>
    private void UpdateGuideDrag(Point pos, KeyModifiers modifiers)
    {
        if (_guideDrag == GuideDrag.New)
            // From the top ruler a horizontal guide, from the left a vertical one; Option swaps them.
            _guideOrientation = _guideFromTop ^ modifiers.HasFlag(KeyModifiers.Alt) ? GuideOrientation.Horizontal : GuideOrientation.Vertical;
        bool horizontal = _guideOrientation == GuideOrientation.Horizontal;
        var image = ToImage(pos);
        double v = (horizontal ? image.Y : image.X) + _guideGrab;
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            // Shift: onto the nearest ruler tick.
            double origin = horizontal ? RulerOrigin.Y : RulerOrigin.X;
            double perUnit = RulerUnits.PixelsPerUnit(RulerUnit, DocumentResolution, horizontal ? ImageSize.Height : ImageSize.Width);
            var (major, divisions) = RulerStep(perUnit * Zoom, RulerUnit);
            double tick = major / divisions * perUnit;
            v = origin + Math.Round((v - origin) / tick) * tick;
        }
        else if (_dragSnapper.IsActive && !SnapSuspended(modifiers))
            v = horizontal ? _dragSnapper.SnapY(v).Value : _dragSnapper.SnapX(v).Value;
        _guidePosition = v;
        Cursor = new Cursor(horizontal ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast);
        _guideCursorShown = true;
        InvalidateVisual();
    }

    private bool GuidesReleased(PointerReleasedEventArgs e) => GuideRelease(e.GetPosition(this));

    /// <summary>Ends a guide or origin drag: adds, moves or (dropped on a ruler or off the canvas) deletes the guide.</summary>
    internal bool GuideRelease(Point pos)
    {
        var drag = _guideDrag;
        if (drag == GuideDrag.None) return false;
        _guideDrag = GuideDrag.None;
        bool away = InRulers(pos) || pos.X < 0 || pos.Y < 0 || pos.X >= Bounds.Width || pos.Y >= Bounds.Height;
        switch (drag)
        {
            case GuideDrag.New when !away:
                GuideAdded?.Invoke(new Guide(_guideOrientation, _guidePosition));
                break;
            case GuideDrag.Move when away:
                GuideDeleted?.Invoke(_guideIndex);
                break;
            case GuideDrag.Move:
                GuideMoved?.Invoke(_guideIndex, _guidePosition);
                break;
            case GuideDrag.Origin:
                RulerOriginChanged?.Invoke(_originDrag);
                break;
        }
        _guideIndex = -1;
        _dragSnapper = Snapper.Off;
        _guideCursorShown = false;
        UpdateCursor();
        InvalidateVisual();
        return true;
    }

    private void GuidesPointerExited()
    {
        _pointer = null;
        PointerInfo?.Invoke(null, LiveBox());
    }

    /// <summary>The box being drawn right now, for the Info panel's W and H.</summary>
    private Rect? LiveBox()
    {
        if (_selecting && _dragged && !_shapeBox.IsEmpty) return new Rect(_shapeBox.Left, _shapeBox.Top, _shapeBox.Width, _shapeBox.Height);
        if (CropBox is { } c) return new Rect(c.Left, c.Top, c.Right - c.Left, c.Bottom - c.Top);
        if (_typeGesture == TypeGesture.Create)
            return new Rect(Math.Min(_typeFrom.X, _typeTo.X), Math.Min(_typeFrom.Y, _typeTo.Y), Math.Abs(_typeTo.X - _typeFrom.X), Math.Abs(_typeTo.Y - _typeFrom.Y));
        return null;
    }

    private void ShowRulerUnitsMenu()
    {
        var menu = new ContextMenu();
        foreach (var unit in RulerUnits.All)
        {
            var item = new MenuItem { Header = RulerUnits.Name(unit), ToggleType = MenuItemToggleType.Radio, IsChecked = unit == RulerUnit };
            item.Click += (_, _) => RulerUnitChosen?.Invoke(unit);
            menu.Items.Add(item);
        }
        menu.Open(this);
    }

    // ---- Drawing: grid and guides (over the image, under the selection and handles) -------------------------------

    private void RenderGridAndGuides(DrawingContext context)
    {
        if (CropBox is not null) return; // the crop turns and moves the image, which guides cannot follow
        if (ShowGrid) RenderGrid(context);
        if (ShowGuides || _guideDrag is GuideDrag.New or GuideDrag.Move) RenderGuides(context);
        if (_guideDrag == GuideDrag.Origin)
        {
            double x = Math.Round(ScreenX(_originDrag.X)) + 0.5, y = Math.Round(ScreenY(_originDrag.Y)) + 0.5;
            foreach (var pen in new[] { OriginPenLight, OriginPen })
            {
                context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
                context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
            }
        }
    }

    private void RenderGuides(DrawingContext context)
    {
        var guides = Guides;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            void Line(Guide guide)
            {
                if (guide.IsHorizontal)
                {
                    double y = Math.Round(ScreenY(guide.Position)) + 0.5;
                    if (y < 0 || y > Bounds.Height) return;
                    g.BeginFigure(new Point(0, y), false);
                    g.LineTo(new Point(Bounds.Width, y));
                }
                else
                {
                    double x = Math.Round(ScreenX(guide.Position)) + 0.5;
                    if (x < 0 || x > Bounds.Width) return;
                    g.BeginFigure(new Point(x, 0), false);
                    g.LineTo(new Point(x, Bounds.Height));
                }
                g.EndFigure(false);
            }
            if (ShowGuides)
                for (int i = 0; i < guides.Count; i++)
                    if (!(_guideDrag == GuideDrag.Move && i == _guideIndex)) Line(guides[i]);
            if (_guideDrag is GuideDrag.New or GuideDrag.Move) Line(new Guide(_guideOrientation, _guidePosition));
        }
        context.DrawGeometry(null, GuidePen, geometry);
    }

    /// <summary>
    /// Gridlines every <see cref="GridSpacing"/> from the ruler origin with <see cref="GridSubdivisions"/> lighter lines
    /// between, over the image only. Lines closer than a few screen points are left out (subdivisions first), so a
    /// zoomed-out grid neither turns into a gray wash nor costs thousands of lines.
    /// </summary>
    private void RenderGrid(DrawingContext context)
    {
        var size = ImageSize;
        double spacing = GridSpacing;
        if (size.Width == 0 || spacing <= 0 || !double.IsFinite(spacing)) return;
        int divisions = Math.Max(1, GridSubdivisions);
        double left = Math.Max(ScreenX(0), 0), right = Math.Min(ScreenX(size.Width), Bounds.Width);
        double top = Math.Max(ScreenY(0), 0), bottom = Math.Min(ScreenY(size.Height), Bounds.Height);
        if (right <= left || bottom <= top) return;

        const double minimum = 4;
        double step = spacing / divisions;
        bool subdivide = divisions > 1 && step * Zoom >= minimum;
        int every = subdivide ? 1 : divisions; // with no room for subdivisions only every whole gridline
        if (!subdivide && spacing * Zoom < minimum) return;
        var major = new StreamGeometry();
        var minor = new StreamGeometry();
        using (var gMajor = major.Open())
        using (var gMinor = minor.Open())
        {
            void Lines(bool vertical)
            {
                double origin = vertical ? RulerOrigin.X : RulerOrigin.Y;
                double from = vertical ? (left - _offset.X) / Zoom : (top - _offset.Y) / Zoom;
                double to = vertical ? (right - _offset.X) / Zoom : (bottom - _offset.Y) / Zoom;
                long first = (long)Math.Ceiling((from - origin) / step), last = (long)Math.Floor((to - origin) / step);
                for (long k = first; k <= last; k++)
                {
                    bool isMajor = k % divisions == 0;
                    if (!isMajor && !subdivide) continue;
                    if (!subdivide && k % every != 0) continue;
                    double doc = origin + k * step;
                    var g = isMajor ? gMajor : gMinor;
                    if (vertical)
                    {
                        double x = Math.Round(ScreenX(doc)) + 0.5;
                        g.BeginFigure(new Point(x, top), false);
                        g.LineTo(new Point(x, bottom));
                    }
                    else
                    {
                        double y = Math.Round(ScreenY(doc)) + 0.5;
                        g.BeginFigure(new Point(left, y), false);
                        g.LineTo(new Point(right, y));
                    }
                    g.EndFigure(false);
                }
            }
            Lines(vertical: true);
            Lines(vertical: false);
        }
        if (subdivide) context.DrawGeometry(null, GridSubPen, minor);
        context.DrawGeometry(null, GridPen, major);
    }

    // ---- Drawing: rulers (last, over everything) --------------------------------------------------------------

    private readonly Dictionary<string, FormattedText> _rulerLabels = [];
    private IBrush? _rulerLabelBrush;

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var b) && b is IBrush brush ? brush : fallback;

    private FormattedText RulerLabel(string text, IBrush brush)
    {
        if (!ReferenceEquals(brush, _rulerLabelBrush) || _rulerLabels.Count > 512)
        {
            _rulerLabels.Clear();
            _rulerLabelBrush = brush;
        }
        if (!_rulerLabels.TryGetValue(text, out var label))
            _rulerLabels[text] = label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 9, brush);
        return label;
    }

    /// <summary>
    /// The labeled step (in units) and how many ticks divide it, so that labels are at least ~56 screen points apart and
    /// ticks at least 5: steps of 1, 2 or 5 times a power of ten (halves, quarters and eighths for inches, as Photoshop).
    /// </summary>
    internal static (double Major, int Divisions) RulerStep(double screenPerUnit, RulerUnit unit)
    {
        if (screenPerUnit <= 0 || !double.IsFinite(screenPerUnit)) return (1, 1);
        const double labelRoom = 56, tickRoom = 5;
        var candidates = new List<double>();
        if (unit == RulerUnit.Inches) candidates.AddRange([0.125, 0.25, 0.5]);
        for (int k = unit == RulerUnit.Pixels ? 0 : -3; k <= 9; k++)
            foreach (int m in (ReadOnlySpan<int>)[1, 2, 5])
                if (!(unit == RulerUnit.Inches && k < 0)) candidates.Add(m * Math.Pow(10, k));
        double major = candidates.FirstOrDefault(c => c * screenPerUnit >= labelRoom, candidates[^1]);
        double lead = major / Math.Pow(10, Math.Floor(Math.Log10(major) + 1e-9));
        int[] options = major < 1 && unit == RulerUnit.Inches ? [8, 4, 2]
            : Math.Abs(lead - 1) < 1e-6 ? [10, 5, 2]
            : Math.Abs(lead - 2) < 1e-6 ? [4, 2]
            : [5];
        int divisions = options.FirstOrDefault(n => major / n * screenPerUnit >= tickRoom, 1);
        if (unit == RulerUnit.Pixels && major / divisions < 1) divisions = (int)major; // no ticks between pixels
        return (major, Math.Max(1, divisions));
    }

    private static string RulerText(double value, double step)
    {
        if (Math.Abs(value) < step * 1e-6) return "0";
        int decimals = step >= 1 ? 0 : Math.Min(4, (int)Math.Ceiling(-Math.Log10(step) - 1e-9));
        return Math.Round(value, decimals).ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture);
    }

    private void RenderRulers(DrawingContext context)
    {
        if (!ShowRulers) return;
        var background = ThemeBrush("St.Panel", Brushes.DimGray);
        var ink = ThemeBrush("St.TextMuted", Brushes.Gray);
        var border = ThemeBrush("St.Border", Brushes.Black);
        double w = Bounds.Width, h = Bounds.Height, r = RulerSize;
        context.FillRectangle(background, new Rect(0, 0, w, r));
        context.FillRectangle(background, new Rect(0, r, r, h - r));

        var size = ImageSize;
        var ticks = new StreamGeometry();
        var labels = new List<(FormattedText Text, Point At, bool Vertical, string Raw)>();
        using (var g = ticks.Open())
        {
            void Ruler(bool horizontal)
            {
                double extent = horizontal ? size.Width : size.Height;
                double perUnit = RulerUnits.PixelsPerUnit(RulerUnit, DocumentResolution, extent);
                double screenPerUnit = perUnit * Zoom;
                var (major, divisions) = RulerStep(screenPerUnit, RulerUnit);
                double origin = horizontal ? RulerOrigin.X : RulerOrigin.Y;
                double offset = horizontal ? _offset.X : _offset.Y;
                double length = horizontal ? w : h;
                // Unit values at the ruler's visible ends.
                double u0 = ((r - offset) / Zoom - origin) / perUnit, u1 = ((length - offset) / Zoom - origin) / perUnit;
                double minor = major / divisions;
                long first = (long)Math.Floor(u0 / minor), last = (long)Math.Ceiling(u1 / minor);
                if (last - first > 20000) return; // cannot happen with the spacing rules; a guard against a zero zoom
                for (long k = first; k <= last; k++)
                {
                    double u = k * minor;
                    double s = Math.Round(offset + (origin + u * perUnit) * Zoom) + 0.5;
                    if (s < r || s > length) continue;
                    long inMajor = ((k % divisions) + divisions) % divisions;
                    double tick = inMajor == 0 ? r : divisions % 2 == 0 && inMajor == divisions / 2 ? r * 0.45 : r * 0.25;
                    if (horizontal)
                    {
                        g.BeginFigure(new Point(s, r), false);
                        g.LineTo(new Point(s, r - tick));
                    }
                    else
                    {
                        g.BeginFigure(new Point(r, s), false);
                        g.LineTo(new Point(r - tick, s));
                    }
                    g.EndFigure(false);
                    if (inMajor == 0) labels.Add((RulerLabel(RulerText(u, major), ink), horizontal ? new Point(s + 2, 0) : new Point(1, s + 2), !horizontal, RulerText(u, major)));
                }
            }
            Ruler(horizontal: true);
            Ruler(horizontal: false);

            // The pointer's position on each ruler.
            if (_pointer is { } p)
            {
                double x = Math.Round(p.X) + 0.5, y = Math.Round(p.Y) + 0.5;
                if (x >= r && x <= w)
                {
                    g.BeginFigure(new Point(x, 0), false);
                    g.LineTo(new Point(x, r));
                    g.EndFigure(false);
                }
                if (y >= r && y <= h)
                {
                    g.BeginFigure(new Point(0, y), false);
                    g.LineTo(new Point(r, y));
                    g.EndFigure(false);
                }
            }
        }
        context.DrawGeometry(null, new Pen(ink, 1), ticks);
        foreach (var (text, at, vertical, raw) in labels)
        {
            if (!vertical)
            {
                context.DrawText(text, at);
                continue;
            }
            // The left ruler's labels read top to bottom, one character under another, as in Photoshop.
            double y = at.Y;
            foreach (char c in raw)
            {
                var ch = RulerLabel(c.ToString(), ink);
                context.DrawText(ch, new Point(at.X + (RulerSize - 2 - ch.Width) / 2, y));
                y += ch.Height - 3;
            }
        }
        var edge = new Pen(border, 1);
        context.DrawLine(edge, new Point(r - 0.5, 0), new Point(r - 0.5, h));
        context.DrawLine(edge, new Point(0, r - 0.5), new Point(w, r - 0.5));
        // The corner: a small crosshair marks where the origin is dragged from.
        context.FillRectangle(background, new Rect(0, 0, r - 1, r - 1));
        var cross = new Pen(ink, 1);
        context.DrawLine(cross, new Point(r / 2, 3), new Point(r / 2, r - 4));
        context.DrawLine(cross, new Point(3, r / 2), new Point(r - 4, r / 2));
    }
}
