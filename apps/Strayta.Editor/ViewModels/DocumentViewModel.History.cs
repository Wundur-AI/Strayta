using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

// The History panel's view of the undo stack: every state from the opened document to the last edit, and jumping to
// one by undoing or redoing the edits in between.
public sealed partial class DocumentViewModel
{
    /// <summary>Raised after every edit, undo and redo (and so after every jump), for the History panel.</summary>
    public event Action? HistoryChanged;

    /// <summary>
    /// The history, oldest first: the edits applied so far, then the undone ones that Redo would bring back, in the
    /// order they were made. <see cref="HistoryPosition"/> of them are applied.
    /// </summary>
    public IReadOnlyList<IEdit> HistoryEdits => [.. _undo.Done, .. _undo.Undone.Reverse()];

    /// <summary>How many edits are applied: 0 is the document as opened.</summary>
    public int HistoryPosition => _undo.Done.Count;

    /// <summary>
    /// Makes the document look as it did after <paramref name="position"/> edits (0: as opened), undoing or redoing
    /// the edits in between. Later edits stay available until a new edit discards them, as in Photoshop.
    /// </summary>
    public void GoToHistory(int position)
    {
        if (IsTransforming) CancelTransform(); // like Undo, stepping back leaves an open transform first
        position = Math.Clamp(position, 0, _undo.Done.Count + _undo.Undone.Count);
        while (_undo.Done.Count > position && _undo.CanUndo) Undo();
        while (_undo.Done.Count < position && _undo.CanRedo) Redo();
    }
}
