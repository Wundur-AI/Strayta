using CommunityToolkit.Mvvm.Input;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

// The Horizontal Type tool (T): its options (TypeOptions, shared with the Character, Paragraph and Properties
// panels), commit / cancel, and the Edit menu's clipboard and Select All commands acting on the text being typed.
public sealed partial class EditorViewModel
{
    public bool IsTypeTool { get => Tool == CanvasTool.Type; set { if (value) Tool = CanvasTool.Type; } }

    /// <summary>Character and paragraph settings for the type being edited, the selected type layer, or new text.</summary>
    public TypeOptions Type => field ??= new TypeOptions(this);

    /// <summary>True while type is being edited on the active document's canvas: single-key shortcuts type instead.</summary>
    public bool IsEditingType => ActiveDocument?.TypeSession is not null;

    /// <summary>
    /// Asks which installed font should replace fonts a layer needs that are missing (Photoshop's prompt when editing
    /// such a layer); null cancels the edit. Set by the window; the self-test answers it directly.
    /// </summary>
    public Func<IReadOnlyList<string>, Task<string?>>? AskReplaceMissingFonts { get; set; }

    /// <summary>Puts plain text on the system clipboard (set by the window).</summary>
    public Func<string, Task>? SetClipboardText { get; set; }

    /// <summary>Reads plain text from the system clipboard (set by the window).</summary>
    public Func<Task<string?>>? GetClipboardText { get; set; }

    private void SyncTypeTool()
    {
        OnPropertyChanged(nameof(IsTypeTool));
        // Choosing another tool commits the typing, as in Photoshop.
        if (Tool != CanvasTool.Type) ActiveDocument?.CommitType();
    }

    /// <summary>The options bar's ✓ (also ⌘Return and Enter on the keypad).</summary>
    [RelayCommand] private void CommitType() => ActiveDocument?.CommitType();

    /// <summary>The options bar's ⊘ (also Esc).</summary>
    [RelayCommand] private void CancelType() => ActiveDocument?.CancelType();

    /// <summary>The options bar's panel button: brings up the Character and Paragraph panels.</summary>
    [RelayCommand]
    private void ToggleTypePanels()
    {
        ShowPanel("Paragraph");
        ShowPanel("Character");
    }

    /// <summary>⌘A while typing: every character of the text.</summary>
    private bool SelectAllText()
    {
        if (ActiveDocument?.TypeSession is not { } s) return false;
        s.Editor.SelectAll();
        return true;
    }

    /// <summary>⌘C / ⌘X while typing: the selected characters as plain text.</summary>
    private async Task<bool> CopyTextAsync(bool cut)
    {
        if (ActiveDocument?.TypeSession is not { } s) return false;
        if (!s.Editor.HasSelection) return true;
        // Photoshop's forced line breaks and paragraph ends become ordinary line breaks elsewhere.
        string text = s.Editor.SelectedText.Replace(Core.Text.TextLayerData.LineBreak, '\n');
        if (SetClipboardText is { } set) await set(text);
        if (cut) s.Editor.DeleteSelection();
        return true;
    }

    /// <summary>⌘V while typing: plain text from the clipboard, typed over the selection.</summary>
    private async Task<bool> PasteTextAsync()
    {
        if (ActiveDocument?.TypeSession is not { } s) return false;
        if (GetClipboardText is { } get && await get() is { Length: > 0 } text && ActiveDocument?.TypeSession == s) s.Editor.Insert(text);
        return true;
    }
}
