using Avalonia;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Threading;
using Strayta.Core.Text;
using Strayta.Editor.Editing;
using Strayta.Text;

namespace Strayta.Editor.Controls;

// The Type tool on the canvas: a click places a caret (new point text, or a click into existing type), a drag draws a
// paragraph box; while typing, the canvas draws the caret, the selection and the box with its handles, turns keys into
// caret moves and edits, and takes typed text (with input-method composition) as TextInput. Keys that type must not
// reach the window's single-key shortcuts; MainWindow checks EditorViewModel.IsEditingType for that.
public sealed partial class ImageCanvas
{
    public static readonly StyledProperty<TypeSession?> TypeSessionProperty =
        AvaloniaProperty.Register<ImageCanvas, TypeSession?>(nameof(TypeSession));

    /// <summary>The type being edited, or null.</summary>
    public TypeSession? TypeSession { get => GetValue(TypeSessionProperty); set => SetValue(TypeSessionProperty, value); }

    /// <summary>A Type tool click at a document point: true when it landed on existing type, which the handler starts editing.</summary>
    public Func<double, double, bool>? TypeEditAt { get; set; }

    /// <summary>New text: a click (point text, box null) or a dragged box, in document coordinates.</summary>
    public event Action<double, double, TextRect?>? TypeCreate;

    /// <summary>Commit the typing: a click outside the text, ⌘Return or Enter on the keypad.</summary>
    public event Action? TypeCommit;

    /// <summary>Esc: cancel the typing.</summary>
    public event Action? TypeCancel;

    private enum TypeGesture { None, Create, Select, Box }

    private TypeGesture _typeGesture;
    private Point _typeFrom, _typeTo;
    private bool _caretVisible = true;
    private DispatcherTimer? _blink;
    private TypeImeClient? _ime;

    private void OnTypePropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != TypeSessionProperty) return;
        if (change.OldValue is TypeSession old) old.Changed -= OnTypeSessionChanged;
        if (change.NewValue is TypeSession now)
        {
            now.Changed += OnTypeSessionChanged;
            Focus(); // typing goes to the canvas
        }
        _typeGesture = TypeGesture.None;
        InputMethod.SetIsInputMethodEnabled(this, TypeSession is not null);
        // Ask the platform for the input-method client again: it exists only while typing.
        RaiseEvent(new TextInputMethodClientRequeryRequestedEventArgs { RoutedEvent = InputMethod.TextInputMethodClientRequeryRequestedEvent });
        RestartBlink();
        UpdateCursor();
        InvalidateVisual();
    }

    private void OnTypeSessionChanged()
    {
        RestartBlink();
        _ime?.Changed();
        InvalidateVisual();
    }

    private void RestartBlink()
    {
        _caretVisible = true;
        if (TypeSession is null)
        {
            _blink?.Stop();
            return;
        }
        _blink ??= new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Render, (_, _) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        });
        _blink.Stop();
        _blink.Start();
    }

    private static bool IsCommand(KeyModifiers m) =>
        OperatingSystem.IsMacOS() ? m.HasFlag(KeyModifiers.Meta) : m.HasFlag(KeyModifiers.Control);

    // ---- Pointer ----------------------------------------------------------------------------------------

    /// <summary>Handles a press with the Type tool; false when it should pan or another tool handles it.</summary>
    private bool TypePressed(PointerPressedEventArgs e, PointerPointProperties props)
    {
        if (Tool != CanvasTool.Type) return false;
        _dragStart = null; // never falls through to moving the layer
        if (!props.IsLeftButtonPressed) return true;
        var p = ToImage(e.GetPosition(this));
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (TypeSession is { } s)
        {
            var handle = s.HitHandle(p.X, p.Y, HandleGrab / Zoom);
            if (handle == BoxHandle.None && IsCommand(e.KeyModifiers)) handle = BoxHandle.Move; // ⌘-drag moves the text
            if (handle != BoxHandle.None)
            {
                s.BeginDrag(handle, p.X, p.Y);
                _typeGesture = TypeGesture.Box;
                return true;
            }
            if (!InsideText(s, p))
            {
                TypeCommit?.Invoke(); // a click away from the text commits it (and does nothing else, as in Photoshop)
                return true;
            }
            var (tx, ty) = s.ToText(p.X, p.Y);
            var editor = s.Editor;
            switch (e.ClickCount)
            {
                case 1: editor.SetCaret(editor.HitTest(tx, ty), extend: shift); break;
                case 2: editor.SelectWord(editor.CharacterAt(tx, ty)); break;
                case 3: editor.SelectLine(editor.HitTest(tx, ty)); break;
                case 4: editor.SelectParagraph(editor.HitTest(tx, ty)); break;
                default: editor.SelectAll(); break;
            }
            _typeGesture = e.ClickCount == 1 ? TypeGesture.Select : TypeGesture.None;
            return true;
        }
        if (TypeEditAt?.Invoke(p.X, p.Y) == true) return true;
        _typeGesture = TypeGesture.Create;
        _typeFrom = _typeTo = p;
        return true;
    }

    /// <summary>Whether a document point is on the text being edited (its box, or its lines with a margin).</summary>
    private bool InsideText(TypeSession s, Point p)
    {
        var (tx, ty) = s.ToText(p.X, p.Y);
        double margin = HandleGrab / Zoom;
        if (s.Editor.Data.Kind == TextKind.Paragraph)
        {
            var b = s.Editor.Data.Box;
            return tx >= b.Left - margin && tx <= b.Right + margin && ty >= b.Top - margin && ty <= b.Bottom + margin;
        }
        var bounds = s.Layout.Bounds;
        double pad = Math.Max(margin, (bounds.Bottom - bounds.Top) * 0.25);
        return tx >= bounds.Left - pad && tx <= bounds.Right + pad && ty >= bounds.Top - pad && ty <= bounds.Bottom + pad;
    }

    /// <summary>Pointer movement with the Type tool: drags, and the cursor over handles; false when not handled here.</summary>
    private bool TypeMoved(PointerEventArgs e)
    {
        if (Tool != CanvasTool.Type || _panning) return false;
        var p = ToImage(e.GetPosition(this));
        switch (_typeGesture)
        {
            case TypeGesture.Create:
                _typeTo = p;
                InvalidateVisual();
                return true;
            case TypeGesture.Select when TypeSession is { } s:
            {
                var (tx, ty) = s.ToText(p.X, p.Y);
                s.Editor.SetCaret(s.Editor.HitTest(tx, ty), extend: true);
                return true;
            }
            case TypeGesture.Box when TypeSession is { } s:
                s.DragTo(p.X, p.Y, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                return true;
        }
        if (!_spaceHeld && FreeTransform is null) Cursor = TypeCursor(p, e.KeyModifiers);
        return true;
    }

    private void TypeReleased(PointerReleasedEventArgs e)
    {
        if (_typeGesture == TypeGesture.None) return;
        if (_typeGesture == TypeGesture.Create)
        {
            var a = _typeFrom;
            var b = ToImage(e.GetPosition(this));
            // A drag of a few screen points is still a click.
            bool drag = Math.Abs(b.X - a.X) * Zoom > 4 || Math.Abs(b.Y - a.Y) * Zoom > 4;
            TextRect? box = drag ? new TextRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)) : null;
            TypeCreate?.Invoke(a.X, a.Y, box);
        }
        TypeSession?.EndDrag();
        _typeGesture = TypeGesture.None;
        InvalidateVisual();
    }

    private Cursor TypeCursor(Point p, KeyModifiers modifiers)
    {
        if (TypeSession is { } s)
        {
            var h = s.HitHandle(p.X, p.Y, HandleGrab / Zoom);
            if (h == BoxHandle.None && IsCommand(modifiers)) return new Cursor(StandardCursorType.SizeAll);
            if (h != BoxHandle.None)
                return new Cursor(h switch
                {
                    BoxHandle.Left or BoxHandle.Right => StandardCursorType.SizeWestEast,
                    BoxHandle.Top or BoxHandle.Bottom => StandardCursorType.SizeNorthSouth,
                    BoxHandle.TopLeft or BoxHandle.BottomRight => StandardCursorType.BottomRightCorner,
                    _ => StandardCursorType.BottomLeftCorner,
                });
        }
        return new Cursor(StandardCursorType.Ibeam);
    }

    // ---- Keys and text ----------------------------------------------------------------------------------

    /// <summary>
    /// Keys while typing. Returns true whenever type is being edited (the canvas's other keys, like Space for panning,
    /// then do nothing); keys that type are left unhandled so their text arrives as TextInput.
    /// </summary>
    private bool TypeKeyDown(KeyEventArgs e)
    {
        if (TypeSession is not { } s) return false;
        var m = e.KeyModifiers;
        bool shift = m.HasFlag(KeyModifiers.Shift), alt = m.HasFlag(KeyModifiers.Alt), cmd = IsCommand(m);
        // Windows and Linux move by word with Ctrl, macOS with Option.
        bool word = OperatingSystem.IsMacOS() ? alt : m.HasFlag(KeyModifiers.Control);
        var editor = s.Editor;
        bool handled = true;
        switch (e.Key)
        {
            case Key.Escape:
                TypeCancel?.Invoke();
                break;
            case Key.Enter when e.PhysicalKey == PhysicalKey.NumPadEnter || cmd:
                TypeCommit?.Invoke();
                break;
            case Key.Enter when shift:
                editor.LineBreak();
                break;
            case Key.Enter:
                editor.NewParagraph();
                break;
            case Key.Tab when !cmd:
                editor.Insert("\t");
                break;
            case Key.Back:
                editor.Backspace(word ? CaretMove.WordLeft : cmd ? CaretMove.LineStart : CaretMove.Left);
                break;
            case Key.Delete:
                editor.DeleteForward(word ? CaretMove.WordRight : cmd ? CaretMove.LineEnd : CaretMove.Right);
                break;
            case Key.Left:
                editor.Move(cmd && OperatingSystem.IsMacOS() ? CaretMove.LineStart : word ? CaretMove.WordLeft : CaretMove.Left, shift);
                break;
            case Key.Right:
                editor.Move(cmd && OperatingSystem.IsMacOS() ? CaretMove.LineEnd : word ? CaretMove.WordRight : CaretMove.Right, shift);
                break;
            case Key.Up:
                editor.Move(cmd ? CaretMove.TextStart : alt ? CaretMove.ParagraphStart : CaretMove.Up, shift);
                break;
            case Key.Down:
                editor.Move(cmd ? CaretMove.TextEnd : alt ? CaretMove.ParagraphEnd : CaretMove.Down, shift);
                break;
            case Key.Home:
                editor.Move(cmd || m.HasFlag(KeyModifiers.Control) ? CaretMove.TextStart : CaretMove.LineStart, shift);
                break;
            case Key.End:
                editor.Move(cmd || m.HasFlag(KeyModifiers.Control) ? CaretMove.TextEnd : CaretMove.LineEnd, shift);
                break;
            default:
                handled = false; // typed characters arrive as TextInput; ⌘ shortcuts go to the menu
                break;
        }
        if (handled) e.Handled = true;
        return true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (TypeSession is not { } s || string.IsNullOrEmpty(e.Text)) return;
        // macOS reports function and arrow keys as private-use characters (U+F700–U+F8FF); they do not type.
        if (e.Text.All(c => c is >= (char)0xF700 and <= (char)0xF8FF)) return;
        s.Preedit = null;
        s.Editor.Insert(e.Text);
        e.Handled = true;
    }

    public ImageCanvas() => InitTypeInput();

    private void InitTypeInput()
    {
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) =>
        {
            if (TypeSession is null) return;
            e.Client = _ime ??= new TypeImeClient(this);
        });
    }

    /// <summary>
    /// Lets input methods (Japanese, Chinese, dead keys on some layouts) compose text at the caret: the composition is
    /// shown underlined at the caret until the input method commits it as TextInput.
    /// </summary>
    private sealed class TypeImeClient(ImageCanvas canvas) : TextInputMethodClient
    {
        public override Visual TextViewVisual => canvas;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";

        public override Rect CursorRectangle
        {
            get
            {
                if (canvas.TypeSession is not { } s) return default;
                var (top, bottom) = s.CaretSegment();
                var a = canvas.ToScreen(top);
                var b = canvas.ToScreen(bottom);
                return new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(1, Math.Abs(b.X - a.X)), Math.Max(1, Math.Abs(b.Y - a.Y)));
            }
        }

        public override TextSelection Selection
        {
            get => canvas.TypeSession is { } s ? new TextSelection(s.Editor.Anchor, s.Editor.Caret) : default;
            set { }
        }

        public override void SetPreeditText(string? preeditText)
        {
            if (canvas.TypeSession is { } s) s.Preedit = preeditText;
        }

        public void Changed()
        {
            RaiseCursorRectangleChanged();
            RaiseSelectionChanged();
        }
    }

    // ---- Drawing ----------------------------------------------------------------------------------------

    private void RenderType(DrawingContext context)
    {
        if (_typeGesture == TypeGesture.Create && Tool == CanvasTool.Type)
        {
            var a = ToScreen((_typeFrom.X, _typeFrom.Y));
            var b = ToScreen((_typeTo.X, _typeTo.Y));
            if (Math.Abs(b.X - a.X) > 4 || Math.Abs(b.Y - a.Y) > 4)
            {
                var rect = new Rect(a, b).Normalize();
                context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 2.5), rect);
                context.DrawRectangle(null, new Pen(Brushes.White, 1, new DashStyle([3, 3], 0)), rect);
            }
        }
        if (TypeSession is not { } s) return;

        // Selection: a translucent highlight over the selected characters, line by line.
        var highlight = new SolidColorBrush(Color.FromArgb(110, 59, 130, 246));
        foreach (var quad in s.SelectionQuads()) context.DrawGeometry(highlight, null, Polygon(quad));

        // The box (paragraph text, with its handles) or a light outline around point text.
        var dark = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 2.5);
        if (s.BoxCorners is { } box)
        {
            var outline = Polygon(box);
            context.DrawGeometry(null, dark, outline);
            context.DrawGeometry(null, new Pen(Brushes.White, 1), outline);
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), 1);
            foreach (var h in TypeSession.BoxHandles)
            {
                var p = ToScreen(s.HandlePosition(h));
                context.DrawRectangle(Brushes.White, edge, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
            }
        }
        else if (s.Editor.Data.Text.Length > 0)
        {
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)), 1), Polygon(s.LineBoundsCorners));
        }

        var (top, bottom) = s.CaretSegment();
        var ct = ToScreen(top);
        var cb = ToScreen(bottom);
        if (s.Preedit is { } preedit)
        {
            // The composition, underlined at the caret, sized like the text there.
            double size = Math.Clamp(Math.Abs(cb.Y - ct.Y) * 0.75, 8, 200);
            var text = new FormattedText(preedit, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size,
                new SolidColorBrush(Color.FromRgb(20, 20, 20)));
            var origin = new Point(ct.X, cb.Y - text.Height);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), null, new Rect(origin, new Size(text.Width, text.Height)));
            context.DrawText(text, origin);
            context.DrawLine(new Pen(Brushes.Black, 1), new Point(ct.X, cb.Y), new Point(ct.X + text.Width, cb.Y));
            return;
        }
        if (_caretVisible && !s.Editor.HasSelection)
        {
            context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 3), ct, cb);
            context.DrawLine(new Pen(Brushes.Black, 1.25), ct, cb);
        }
    }

    private StreamGeometry Polygon((double X, double Y)[] points)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(ToScreen(points[0]), true);
            for (int i = 1; i < points.Length; i++) ctx.LineTo(ToScreen(points[i]));
            ctx.EndFigure(true);
        }
        return g;
    }
}
