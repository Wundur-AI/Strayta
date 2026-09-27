using Strayta.Core.Selection;

namespace Strayta.Editor.Editing;

/// <summary>
/// Changes the document's selection. Selections are immutable, so undo just restores the previous one; like
/// Photoshop, every selection change is a history step. The selection isn't saved in the file, so it doesn't mark
/// the document as edited.
/// </summary>
public sealed class SelectionEdit(SelectionMask? before, SelectionMask? after, Action<SelectionMask?> set, string description) : IEdit
{
    public string Description => description;
    public bool ChangesStructure => false;
    public bool ChangesContent => false;

    public void Do() => set(after);
    public void Undo() => set(before);
}

/// <summary>Several edits applied and undone as one history step (e.g. Paste: new layer, then deselect).</summary>
public sealed class CompositeEdit(string description, params IEdit[] edits) : IEdit
{
    public string Description => description;
    public bool ChangesStructure => edits.Any(e => e.ChangesStructure);
    public bool ChangesContent => edits.Any(e => e.ChangesContent);

    public void Do()
    {
        foreach (var e in edits) e.Do();
    }

    public void Undo()
    {
        for (int i = edits.Length - 1; i >= 0; i--) edits[i].Undo();
    }
}
