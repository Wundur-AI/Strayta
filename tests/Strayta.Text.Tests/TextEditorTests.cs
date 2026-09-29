using Strayta.Core.Text;

namespace Strayta.Text.Tests;

/// <summary>The Type tool's editing logic: caret movement, selection, typing, deleting, styling ranges and undo.</summary>
public class TextEditorTests
{
    private static TextStyle Style(double size = 40)
    {
        var font = FontCatalog.System.FallbackPostScriptName;
        if (font is null) Assert.Skip("No fonts installed.");
        return new TextStyle { FontPostScriptName = font, FontSize = size };
    }

    private static TextEditor Editor(string text, TextKind kind = TextKind.Point, double width = 1000)
    {
        var data = kind == TextKind.Point
            ? TextLayerData.CreatePoint(text, Style(), 0, 0)
            : TextLayerData.CreateParagraph(text, Style(), new TextRect(0, 0, width, 2000));
        return new TextEditor(data);
    }

    [Fact]
    public void Word_movement_skips_spaces_and_punctuation()
    {
        const string t = "Hello, big world";
        Assert.Equal(5, TextEditor.NextWordEnd(t, 0));
        Assert.Equal(10, TextEditor.NextWordEnd(t, 5));
        Assert.Equal(16, TextEditor.NextWordEnd(t, 10));
        Assert.Equal(11, TextEditor.PreviousWordStart(t, 16));
        Assert.Equal(7, TextEditor.PreviousWordStart(t, 11));
        Assert.Equal(0, TextEditor.PreviousWordStart(t, 5));
        Assert.Equal(0, TextEditor.PreviousWordStart(t, 0));
    }

    [Fact]
    public void Double_click_selects_a_word_spaces_or_one_mark()
    {
        const string t = "don't  stop!";
        Assert.Equal((0, 5), TextEditor.WordRange(t, 2));
        Assert.Equal((5, 7), TextEditor.WordRange(t, 5));
        Assert.Equal((11, 12), TextEditor.WordRange(t, 12)); // past the end: the last character
        Assert.Equal((5, 7), TextEditor.WordRange(t, 6));
        Assert.Equal((11, 12), TextEditor.WordRange(t, 11));
        Assert.Equal((0, 0), TextEditor.WordRange("", 0));
    }

    [Fact]
    public void Characters_are_grapheme_clusters()
    {
        const string t = "a👍🏽e\u0301b"; // thumbs up with a skin tone (4 UTF-16 units), e + combining acute
        Assert.Equal(1, TextEditor.NextBoundary(t, 0));
        Assert.Equal(5, TextEditor.NextBoundary(t, 1));
        Assert.Equal(7, TextEditor.NextBoundary(t, 5));
        Assert.Equal(5, TextEditor.PreviousBoundary(t, 7));
        Assert.Equal(1, TextEditor.PreviousBoundary(t, 5));
        Assert.Equal(1, TextEditor.SnapToBoundary(t, 3));
        Assert.Equal(2, TextEditor.PreviousBoundary("a\nb", 3)); // a paragraph break is a character of its own
        Assert.Equal(1, TextEditor.PreviousBoundary("a\nb", 2));
    }

    [Fact]
    public void Paragraph_ranges_exclude_the_break()
    {
        const string t = "One\nTwo two\nThree";
        Assert.Equal((0, 3), TextEditor.ParagraphRange(t, 1));
        Assert.Equal((4, 11), TextEditor.ParagraphRange(t, 4));
        Assert.Equal((4, 11), TextEditor.ParagraphRange(t, 11));
        Assert.Equal((12, 17), TextEditor.ParagraphRange(t, 17));
    }

    [Fact]
    public void Typing_replaces_the_selection_and_backspace_deletes_whole_characters()
    {
        var e = Editor("Hello");
        Assert.Equal(5, e.Caret);
        e.Insert(" 👍🏽");
        Assert.Equal("Hello 👍🏽", e.Data.Text);
        e.Backspace();
        Assert.Equal("Hello ", e.Data.Text);
        e.Move(CaretMove.TextStart);
        e.Move(CaretMove.WordRight, extend: true);
        Assert.Equal("Hello", e.SelectedText);
        e.Insert("Bye");
        Assert.Equal("Bye ", e.Data.Text);
        Assert.Equal(3, e.Caret);
        e.DeleteForward();
        Assert.Equal("Bye", e.Data.Text);
        e.Backspace(CaretMove.WordLeft);
        Assert.Equal("", e.Data.Text);
        e.Insert("line\r\nnext\u0001");
        Assert.Equal("line\nnext", e.Data.Text); // line endings become paragraphs, control characters are dropped
        e.LineBreak();
        Assert.EndsWith(TextLayerData.LineBreak.ToString(), e.Data.Text);
    }

    [Fact]
    public void Shift_extends_from_the_anchor_and_arrows_collapse_a_selection()
    {
        var e = Editor("abcdef");
        e.SetCaret(3);
        e.Move(CaretMove.Right, extend: true);
        e.Move(CaretMove.Right, extend: true);
        Assert.Equal((3, 2), (e.SelectionStart, e.SelectionLength));
        e.Move(CaretMove.Left, extend: true);
        e.Move(CaretMove.Left, extend: true);
        e.Move(CaretMove.Left, extend: true);
        Assert.Equal((2, 1, 2, 3), (e.SelectionStart, e.SelectionLength, e.Caret, e.Anchor));
        e.Move(CaretMove.Right);
        Assert.Equal((3, 0), (e.Caret, e.SelectionLength)); // → puts the caret at the selection's right side
        e.SelectAll();
        Assert.Equal("abcdef", e.SelectedText);
        e.Move(CaretMove.Left);
        Assert.Equal(0, e.Caret);
    }

    [Fact]
    public void Line_keys_follow_the_laid_out_lines()
    {
        var e = Editor("First line\nSecond line here\nx");
        e.SetCaret(3);
        e.Move(CaretMove.LineEnd);
        Assert.Equal(10, e.Caret);
        e.Move(CaretMove.LineStart);
        Assert.Equal(0, e.Caret);
        e.SetCaret(5);
        e.Move(CaretMove.Down);
        Assert.InRange(e.Caret, 14, 18); // about the same x on the next line
        int second = e.Caret;
        e.Move(CaretMove.Down);
        Assert.Equal(e.Data.Text.Length, e.Caret); // the last line is shorter: its end
        e.Move(CaretMove.Up);
        Assert.Equal(second, e.Caret); // the remembered x brings it back
        e.Move(CaretMove.Up);
        e.Move(CaretMove.Up);
        Assert.Equal(0, e.Caret);
        e.Move(CaretMove.TextEnd);
        e.Move(CaretMove.ParagraphStart);
        Assert.Equal(28, e.Caret);
        e.Move(CaretMove.ParagraphStart);
        Assert.Equal(11, e.Caret);
    }

    [Fact]
    public void Box_text_line_end_stops_before_the_wrapping_space()
    {
        var e = Editor("aaaa bbbb cccc dddd eeee ffff gggg", TextKind.Paragraph, width: 200);
        Assert.True(e.Layout.Lines.Count > 1);
        e.SetCaret(0);
        e.Move(CaretMove.LineEnd);
        var first = e.Layout.Lines[0];
        Assert.Equal(first.End - 1, e.Caret);
        Assert.Equal(' ', e.Data.Text[e.Caret]);
        e.SelectLine(0);
        Assert.Equal((first.Start, first.Length), (e.SelectionStart, e.SelectionLength));
    }

    [Fact]
    public void Clicks_select_words_lines_and_paragraphs()
    {
        var e = Editor("One two\nThree four");
        e.SelectWord(5);
        Assert.Equal("two", e.SelectedText);
        e.SelectParagraph(10);
        Assert.Equal("Three four", e.SelectedText);
        double x4 = e.Layout.GetCaret(4).X, x5 = e.Layout.GetCaret(5).X;
        Assert.Equal(4, e.HitTest(x4 + 1, 0));
        Assert.Equal(3, e.CharacterAt(x4 - 1, 0)); // the space before "two"
        Assert.Equal(4, e.CharacterAt(x4 + 1, 0)); // "t"
        Assert.Equal(4, e.CharacterAt((x4 + x5) / 2 + 1, 0));
        Assert.Equal(6, e.CharacterAt(e.Layout.Lines[0].Right + 50, 0)); // past the line's end: its last character
    }

    [Fact]
    public void Styles_apply_to_the_selection_or_to_what_is_typed_next()
    {
        var e = Editor("abcdef");
        e.Select(1, 3);
        e.ApplyStyle(s => s with { FauxBold = true });
        Assert.Equal([1, 2, 3], e.Data.StyleRuns.Select(r => r.Length));
        Assert.True(e.Data.StyleAt(1).FauxBold && !e.Data.StyleAt(3).FauxBold);
        int changes = 0;
        e.Changed += () => changes++;
        e.Select(0, 4);
        Assert.Equal(2, e.SelectedStyles.Count); // mixed: the panel shows it blank
        e.Select(1, 4); // same caret, other anchor: still a change
        Assert.Equal(2, changes);

        e.SetCaret(6);
        e.ApplyStyle(s => s with { Underline = true });
        Assert.Equal("abcdef", e.Data.Text); // nothing changes until typing
        Assert.True(e.InsertionStyle.Underline);
        e.Insert("gh");
        Assert.True(e.Data.StyleAt(6).Underline && e.Data.StyleAt(7).Underline && !e.Data.StyleAt(5).Underline);

        e.ApplyParagraphStyle(p => p with { Justification = TextJustification.Center });
        Assert.Equal(TextJustification.Center, e.Data.ParagraphStyleAt(0).Justification);
    }

    [Fact]
    public void Typing_undoes_as_one_step_and_undo_restores_the_caret()
    {
        var e = Editor("Hi");
        e.Insert(" t");
        e.Insert("h");
        e.Insert("ere");
        e.Move(CaretMove.Left);
        e.Insert("X");
        Assert.Equal("Hi therXe", e.Data.Text);
        Assert.True(e.Undo());
        Assert.Equal("Hi there", e.Data.Text);
        Assert.Equal(7, e.Caret);
        Assert.True(e.Undo());
        Assert.Equal("Hi", e.Data.Text);
        Assert.Equal(2, e.Caret);
        Assert.False(e.Undo());
        Assert.False(e.IsModified);
        Assert.True(e.Redo());
        Assert.Equal("Hi there", e.Data.Text);
        Assert.True(e.IsModified);
        Assert.Equal("Hi", e.Original.Text);
    }

    [Fact]
    public void An_emptied_text_keeps_its_style_for_new_typing()
    {
        var e = Editor("ab");
        e.SelectAll();
        e.ApplyStyle(s => s with { FontSize = 72 });
        e.Backspace();
        Assert.Equal("", e.Data.Text);
        e.Insert("c");
        Assert.Equal(72, e.Data.StyleAt(0).FontSize);
        Assert.Single(e.Layout.Lines);
    }
}
