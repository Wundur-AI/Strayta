using System.Globalization;
using Strayta.Core.Text;

namespace Strayta.Text;

/// <summary>Where a caret key moves the caret (see <see cref="TextEditor.Move"/>).</summary>
public enum CaretMove
{
    /// <summary>One character (grapheme cluster) back: ←.</summary>
    Left,

    /// <summary>One character forward: →.</summary>
    Right,

    /// <summary>To the start of the word before the caret: Option+← (Ctrl+← on Windows).</summary>
    WordLeft,

    /// <summary>To the end of the word after the caret: Option+→.</summary>
    WordRight,

    /// <summary>To the start of the laid-out line: ⌘← (Home).</summary>
    LineStart,

    /// <summary>To the end of the laid-out line: ⌘→ (End).</summary>
    LineEnd,

    /// <summary>To the line above, keeping the horizontal position: ↑.</summary>
    Up,

    /// <summary>To the line below: ↓.</summary>
    Down,

    /// <summary>To the start of the paragraph (or the previous one's start when already there): Option+↑.</summary>
    ParagraphStart,

    /// <summary>To the end of the paragraph (or the next one's end): Option+↓.</summary>
    ParagraphEnd,

    /// <summary>To the start of the text: ⌘↑.</summary>
    TextStart,

    /// <summary>To the end of the text: ⌘↓.</summary>
    TextEnd,
}

/// <summary>
/// Editing a type layer's text as the Type tool does, without any UI: a caret and a selection (an anchor and the
/// caret, so Shift-extension grows from where it started), typing that replaces the selection and takes the style of
/// the character before (or a style picked while nothing was selected, as Photoshop keeps for the next characters),
/// deleting by character or word, caret movement by character, word, line and paragraph (using the laid-out lines), word /
/// line / paragraph selection for double, triple and quadruple clicks, and an undo history local to the edit (consecutive
/// typing undoes as one step). The text itself is an immutable <see cref="TextLayerData"/>; every change replaces
/// <see cref="Data"/>.
/// <para>
/// Caret positions are indices between characters of <see cref="TextLayerData.Text"/> (0..Length), always on grapheme
/// cluster boundaries, so an emoji or a letter with a combining accent moves and deletes as one character.
/// </para>
/// </summary>
public sealed class TextEditor
{
    private readonly FontCatalog? _fonts;
    private readonly List<Snapshot> _undo = [], _redo = [];
    private TextLayout? _layout;
    private double? _preferredX;
    private bool _typing; // the last change was typing that the next typing may join in the undo history

    private sealed record Snapshot(TextLayerData Data, int Caret, int Anchor, TextStyle? Pending);

    /// <param name="data">The text to edit.</param>
    /// <param name="fonts">Fonts for laying the text out (line movement and hit testing); the installed ones by default.</param>
    public TextEditor(TextLayerData data, FontCatalog? fonts = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        _fonts = fonts;
        Caret = Anchor = data.Text.Length;
    }

    /// <summary>The text as edited so far.</summary>
    public TextLayerData Data { get; private set; }

    /// <summary>The text as the edit started (what Cancel returns to).</summary>
    public TextLayerData Original => _undo.Count > 0 ? _undo[0].Data : Data;

    /// <summary>The caret: the insertion point, and the moving end of the selection.</summary>
    public int Caret { get; private set; }

    /// <summary>The fixed end of the selection (equal to <see cref="Caret"/> when nothing is selected).</summary>
    public int Anchor { get; private set; }

    public int SelectionStart => Math.Min(Caret, Anchor);
    public int SelectionLength => Math.Abs(Caret - Anchor);
    public bool HasSelection => Caret != Anchor;
    public string SelectedText => Data.Text.Substring(SelectionStart, SelectionLength);

    /// <summary>
    /// A style chosen while nothing was selected: the next typed characters take it (Photoshop keeps a style changed
    /// in the Character panel at the caret for what is typed next). Cleared when the caret moves.
    /// </summary>
    public TextStyle? PendingStyle { get; private set; }

    /// <summary>Raised after the text, caret or selection changed.</summary>
    public event Action? Changed;

    /// <summary>The current text laid out (built on demand and kept until the text changes).</summary>
    public TextLayout Layout => _layout ??= TextLayout.Create(Data, _fonts);

    /// <summary>Lets a caller that already laid out <see cref="Data"/> (to render it) share that layout.</summary>
    public void UseLayout(TextLayout layout)
    {
        if (ReferenceEquals(layout.Data, Data)) _layout = layout;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>True once the text or its formatting differs from where the edit started.</summary>
    public bool IsModified => !Data.Equals(Original);

    /// <summary>
    /// The style new characters would get at the caret: the pending style, else the first selected character's, else
    /// the character before the caret's (the first character's at the start).
    /// </summary>
    public TextStyle InsertionStyle =>
        PendingStyle ?? (HasSelection ? Data.StyleAt(SelectionStart) : Data.StyleAt(Caret > 0 ? Caret - 1 : 0));

    /// <summary>
    /// The distinct character styles the Character panel should show: the selected characters', or the insertion
    /// style when nothing is selected (so values that differ across a selection can be shown as mixed).
    /// </summary>
    public IReadOnlyList<TextStyle> SelectedStyles
    {
        get
        {
            if (!HasSelection) return [InsertionStyle];
            var list = new List<TextStyle>();
            int at = 0, start = SelectionStart, end = SelectionStart + SelectionLength;
            foreach (var run in Data.StyleRuns)
            {
                int runEnd = at + run.Length;
                if (runEnd > start && at < end && !list.Contains(run.Style)) list.Add(run.Style);
                at = runEnd;
            }
            return list;
        }
    }

    /// <summary>The paragraph styles of the paragraphs the caret or selection touches.</summary>
    public IReadOnlyList<ParagraphStyle> SelectedParagraphStyles
    {
        get
        {
            var list = new List<ParagraphStyle>();
            int start = SelectionStart, end = SelectionStart + SelectionLength;
            foreach (var (ps, pl) in Data.Paragraphs)
                if (ps <= end && ps + pl >= start)
                {
                    var style = Data.ParagraphStyleAt(Math.Min(ps, Math.Max(0, Data.Text.Length - 1)));
                    if (!list.Contains(style)) list.Add(style);
                }
            if (list.Count == 0) list.Add(Data.ParagraphStyleAt(0));
            return list;
        }
    }

    // ---- Typing ---------------------------------------------------------------------------------------

    /// <summary>
    /// Types <paramref name="text"/> over the selection. Line endings become paragraph breaks ('\n'); a Shift+Return
    /// forced line break is <see cref="TextLayerData.LineBreak"/>. Other control characters are dropped.
    /// </summary>
    public void Insert(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = Clean(text);
        if (text.Length == 0) return;
        bool joins = _typing && !HasSelection && PendingStyle is null;
        if (!joins) Record();
        var style = InsertionStyle;
        int start = SelectionStart;
        SetData(Data.ReplaceText(start, SelectionLength, text, style));
        PendingStyle = null;
        Caret = Anchor = start + text.Length;
        _typing = true;
        _preferredX = null;
        Changed?.Invoke();
    }

    /// <summary>Return: a new paragraph.</summary>
    public void NewParagraph() => Insert("\n");

    /// <summary>Shift+Return: a line break inside the paragraph.</summary>
    public void LineBreak() => Insert(TextLayerData.LineBreak.ToString());

    /// <summary>Backspace: deletes the selection, or the character (or with <paramref name="unit"/>, the word or the line start) before the caret.</summary>
    public void Backspace(CaretMove unit = CaretMove.Left)
    {
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }
        int to = Target(unit);
        if (to >= Caret) return;
        Delete(to, Caret - to);
    }

    /// <summary>Forward Delete: deletes the selection, or the character (word, line end) after the caret.</summary>
    public void DeleteForward(CaretMove unit = CaretMove.Right)
    {
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }
        int to = Target(unit);
        if (to <= Caret) return;
        Delete(Caret, to - Caret);
    }

    /// <summary>Deletes the selected characters (Cut, or Delete with a selection).</summary>
    public void DeleteSelection()
    {
        if (!HasSelection) return;
        Delete(SelectionStart, SelectionLength);
    }

    private void Delete(int start, int length)
    {
        Record();
        // An emptied text keeps the style of what was deleted, so typing again continues in it.
        var keep = Data.StyleAt(start);
        SetData(Data.ReplaceText(start, length, "", keep));
        Caret = Anchor = start;
        PendingStyle = null;
        _preferredX = null;
        Changed?.Invoke();
    }

    private static string Clean(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace((char)0x2029, '\n').Replace((char)0x2028, TextLayerData.LineBreak);
        return string.Concat(text.Where(c => c is '\n' or TextLayerData.LineBreak or '\t' || !char.IsControl(c)));
    }

    // ---- Formatting ---------------------------------------------------------------------------------------

    /// <summary>
    /// Changes the character style of the selection; with nothing selected, of the characters typed next (the
    /// Character panel at a caret). One undo step.
    /// </summary>
    public void ApplyStyle(Func<TextStyle, TextStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!HasSelection && Data.Text.Length > 0)
        {
            PendingStyle = change(InsertionStyle);
            _typing = false;
            Changed?.Invoke();
            return;
        }
        Record();
        SetData(HasSelection ? Data.ApplyStyle(SelectionStart, SelectionLength, change) : Data.ApplyStyle(change));
        Changed?.Invoke();
    }

    /// <summary>Changes the paragraph style of every paragraph the caret or selection touches. One undo step.</summary>
    public void ApplyParagraphStyle(Func<ParagraphStyle, ParagraphStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Record();
        SetData(Data.ApplyParagraphStyle(SelectionStart, SelectionLength, change));
        Changed?.Invoke();
    }

    /// <summary>
    /// Replaces the whole text data, e.g. with a resized box, another anti-aliasing or position. The caret and
    /// selection are kept where they still fit. <paramref name="newStep"/> false folds the change into the previous
    /// undo step (the moves of one drag).
    /// </summary>
    public void Replace(TextLayerData data, bool newStep = true)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Equals(Data)) return;
        if (newStep || _undo.Count == 0) Record();
        SetData(data);
        Caret = Math.Min(Caret, data.Text.Length);
        Anchor = Math.Min(Anchor, data.Text.Length);
        Changed?.Invoke();
    }

    // ---- Caret and selection ---------------------------------------------------------------------------

    /// <summary>Moves the caret; with <paramref name="extend"/> (Shift) the selection grows or shrinks from its anchor.</summary>
    public void Move(CaretMove move, bool extend = false)
    {
        int target;
        if (!extend && HasSelection && move is CaretMove.Left or CaretMove.Right)
            target = move == CaretMove.Left ? SelectionStart : SelectionStart + SelectionLength; // ← / → collapse a selection to its side
        else
            target = Target(move);
        bool vertical = move is CaretMove.Up or CaretMove.Down;
        SetCaretCore(target, extend, keepPreferredX: vertical);
    }

    /// <summary>Puts the caret at <paramref name="index"/> (snapped to a character boundary); <paramref name="extend"/> keeps the anchor.</summary>
    public void SetCaret(int index, bool extend = false) => SetCaretCore(Snap(index), extend, keepPreferredX: false);

    /// <summary>Selects from <paramref name="anchor"/> to <paramref name="caret"/>.</summary>
    public void Select(int anchor, int caret)
    {
        int a = Snap(anchor), c = Snap(caret);
        if (a == Anchor && c == Caret) return;
        Anchor = a;
        Caret = c;
        _preferredX = null;
        PendingStyle = null;
        _typing = false;
        Changed?.Invoke();
    }

    /// <summary>⌘A: every character.</summary>
    public void SelectAll() => Select(0, Data.Text.Length);

    /// <summary>Double-click: the word at <paramref name="index"/> (or the run of spaces, or the punctuation mark there).</summary>
    public void SelectWord(int index)
    {
        var (start, end) = WordRange(Data.Text, index);
        Select(start, end);
    }

    /// <summary>Triple-click: the laid-out line holding <paramref name="index"/>.</summary>
    public void SelectLine(int index)
    {
        var line = Layout.Lines[Layout.LineOf(index)];
        Select(line.Start, line.End);
    }

    /// <summary>Quadruple-click: the paragraph holding <paramref name="index"/>.</summary>
    public void SelectParagraph(int index)
    {
        var (start, end) = ParagraphRange(Data.Text, index);
        Select(start, end);
    }

    /// <summary>The caret position nearest to a point in text space, on a character boundary.</summary>
    public int HitTest(double x, double y) => Snap(Layout.HitTest(x, y));

    /// <summary>
    /// The character under a point in text space (for double-clicks): the one before the nearest caret position when
    /// the point is left of it, else the one after.
    /// </summary>
    public int CharacterAt(double x, double y)
    {
        int hit = HitTest(x, y);
        var caret = Layout.GetCaret(hit);
        var line = Layout.Lines[caret.Line];
        bool before = x < caret.X && hit > line.Start || hit >= line.End && hit > line.Start;
        return before ? PreviousBoundary(Data.Text, hit) : hit;
    }

    private void SetCaretCore(int index, bool extend, bool keepPreferredX)
    {
        index = Math.Clamp(index, 0, Data.Text.Length);
        if (!keepPreferredX) _preferredX = null;
        if (index == Caret && (extend || Anchor == Caret)) return;
        Caret = index;
        if (!extend) Anchor = index;
        PendingStyle = null;
        _typing = false;
        Changed?.Invoke();
    }

    /// <summary>Where <paramref name="move"/> takes the caret from its current position.</summary>
    private int Target(CaretMove move)
    {
        string text = Data.Text;
        switch (move)
        {
            case CaretMove.Left: return PreviousBoundary(text, Caret);
            case CaretMove.Right: return NextBoundary(text, Caret);
            case CaretMove.WordLeft: return PreviousWordStart(text, Caret);
            case CaretMove.WordRight: return NextWordEnd(text, Caret);
            case CaretMove.TextStart: return 0;
            case CaretMove.TextEnd: return text.Length;
            case CaretMove.ParagraphStart:
            {
                var (start, _) = ParagraphRange(text, Caret);
                return Caret > start ? start : ParagraphRange(text, Math.Max(0, start - 1)).Start;
            }
            case CaretMove.ParagraphEnd:
            {
                var (_, end) = ParagraphRange(text, Caret);
                return Caret < end ? end : ParagraphRange(text, Math.Min(text.Length, end + 1)).End;
            }
            case CaretMove.LineStart:
                return Layout.Lines[Layout.LineOf(Caret)].Start;
            case CaretMove.LineEnd:
            {
                var line = Layout.Lines[Layout.LineOf(Caret)];
                int end = line.End;
                // A soft-wrapped line ends in the space it broke after; the caret goes before that space, as in text editors.
                bool wrapped = end < text.Length && text[end] is not ('\n' or TextLayerData.LineBreak);
                if (wrapped && end > line.Start && text[end - 1] == ' ') end--;
                return end;
            }
            case CaretMove.Up:
            case CaretMove.Down:
            {
                var layout = Layout;
                var caret = layout.GetCaret(Caret);
                double x = _preferredX ??= caret.X;
                int li = caret.Line + (move == CaretMove.Up ? -1 : 1);
                if (li < 0) return 0;
                if (li >= layout.Lines.Count) return text.Length;
                var line = layout.Lines[li];
                return Snap(layout.HitTest(x, line.Baseline));
            }
            default: throw new ArgumentOutOfRangeException(nameof(move));
        }
    }

    // ---- Undo inside the edit -----------------------------------------------------------------------------

    /// <summary>Steps back one change made in this edit (a run of typing counts as one).</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Add(Current());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(Current());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    private Snapshot Current() => new(Data, Caret, Anchor, PendingStyle);

    private void Record()
    {
        _undo.Add(Current());
        _redo.Clear();
        _typing = false;
    }

    private void Restore(Snapshot s)
    {
        SetData(s.Data);
        (Caret, Anchor, PendingStyle) = (s.Caret, s.Anchor, s.Pending);
        _typing = false;
        _preferredX = null;
        Changed?.Invoke();
    }

    private void SetData(TextLayerData data)
    {
        Data = data;
        _layout = null;
    }

    private int Snap(int index) => SnapToBoundary(Data.Text, index);

    // ---- Text boundaries (static, for tests and other callers) -----------------------------------------------

    /// <summary>The nearest grapheme cluster boundary at or before <paramref name="index"/>.</summary>
    public static int SnapToBoundary(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        if (index == 0 || index == text.Length) return index;
        int at = 0;
        while (at < text.Length)
        {
            int next = at + StringInfo.GetNextTextElementLength(text, at);
            if (next > index) return at;
            at = next;
        }
        return text.Length;
    }

    /// <summary>The grapheme cluster boundary before <paramref name="index"/>.</summary>
    public static int PreviousBoundary(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        if (index == 0) return 0;
        // Line breaks are their own characters (StringInfo would join "\r\n", which never occurs here).
        int at = LineStartBefore(text, index), previous = at;
        while (at < index)
        {
            previous = at;
            at += Math.Max(1, StringInfo.GetNextTextElementLength(text, at));
        }
        return previous;
    }

    /// <summary>The grapheme cluster boundary after <paramref name="index"/>.</summary>
    public static int NextBoundary(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        if (index == text.Length) return index;
        return Math.Min(text.Length, index + Math.Max(1, StringInfo.GetNextTextElementLength(text, index)));
    }

    /// <summary>Where Option+← goes: back over spaces and punctuation, then to the start of the word.</summary>
    public static int PreviousWordStart(string text, int index)
    {
        int i = Math.Clamp(index, 0, text.Length);
        while (i > 0 && !IsWordChar(text, i - 1)) i--;
        while (i > 0 && IsWordChar(text, i - 1)) i--;
        return SnapToBoundary(text, i);
    }

    /// <summary>Where Option+→ goes: forward over spaces and punctuation, then to the end of the word.</summary>
    public static int NextWordEnd(string text, int index)
    {
        int i = Math.Clamp(index, 0, text.Length);
        while (i < text.Length && !IsWordChar(text, i)) i++;
        while (i < text.Length && IsWordChar(text, i)) i++;
        return i;
    }

    /// <summary>
    /// The word a double-click on character <paramref name="index"/> selects (see <see cref="CharacterAt"/>): the run of letters and digits there (an apostrophe
    /// between letters belongs to the word), else the run of spaces, else the one character.
    /// </summary>
    public static (int Start, int End) WordRange(string text, int index)
    {
        if (text.Length == 0) return (0, 0);
        index = Math.Clamp(index, 0, text.Length);
        // At the very end, the last character.
        int probe = index < text.Length ? index : text.Length - 1;
        char c = text[probe];
        if (IsWordChar(text, probe))
        {
            int s = probe, e = probe;
            while (s > 0 && IsWordChar(text, s - 1)) s--;
            while (e < text.Length && IsWordChar(text, e)) e++;
            return (s, e);
        }
        if (c is ' ' or '\t')
        {
            int s = probe, e = probe;
            while (s > 0 && text[s - 1] is ' ' or '\t') s--;
            while (e < text.Length && text[e] is ' ' or '\t') e++;
            return (s, e);
        }
        if (c is '\n' or TextLayerData.LineBreak) return (probe, probe);
        return (probe, NextBoundary(text, probe));
    }

    /// <summary>The paragraph holding <paramref name="index"/>, without its '\n'.</summary>
    public static (int Start, int End) ParagraphRange(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        int start = index > 0 ? text.LastIndexOf('\n', index - 1) + 1 : 0;
        int end = text.IndexOf('\n', index);
        return (start, end < 0 ? text.Length : end);
    }

    private static int LineStartBefore(string text, int index)
    {
        for (int i = index - 1; i >= 0; i--)
            if (text[i] is '\n' or TextLayerData.LineBreak) return i + 1 == index ? i : i + 1;
        return 0;
    }

    private static bool IsWordChar(string text, int i)
    {
        char c = text[i];
        if (char.IsLetterOrDigit(c) || c == '_' || char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            return true;
        if (char.IsSurrogate(c))
        {
            int start = char.IsLowSurrogate(c) && i > 0 ? i - 1 : i;
            return start + 1 < text.Length && char.IsLetterOrDigit(text, start);
        }
        // "don't", "l'eau": an apostrophe between letters.
        return c is '\'' or '\u2019' && i > 0 && i + 1 < text.Length && char.IsLetter(text[i - 1]) && char.IsLetter(text[i + 1]);
    }
}
