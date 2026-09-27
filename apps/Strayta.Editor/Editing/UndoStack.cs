namespace Strayta.Editor.Editing;

/// <summary>A reversible change to a document.</summary>
public interface IEdit
{
    string Description { get; }

    /// <summary>True when the edit changes the layer tree's shape (order, parents), not just properties.</summary>
    bool ChangesStructure { get; }

    void Do();
    void Undo();

    /// <summary>
    /// Folds a following edit into this one (e.g. every step of a slider drag), returning true if merged.
    /// </summary>
    bool TryMerge(IEdit next) => false;
}

public sealed class UndoStack
{
    private static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(800);

    private readonly List<IEdit> _done = [];
    private readonly List<IEdit> _undone = [];
    private DateTime _lastPush;

    /// <summary>Number of edits since the last save point; 0 means the document matches the saved file.</summary>
    public int DistanceFromSave { get; private set; }

    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;
    public string? UndoDescription => _done.Count > 0 ? _done[^1].Description : null;
    public string? RedoDescription => _undone.Count > 0 ? _undone[^1].Description : null;

    public void Push(IEdit edit)
    {
        edit.Do();
        var now = DateTime.UtcNow;
        bool merged = _done.Count > 0 && now - _lastPush < MergeWindow && DistanceFromSave != 0 && _done[^1].TryMerge(edit);
        if (!merged)
        {
            _done.Add(edit);
            DistanceFromSave++;
        }
        _undone.Clear();
        _lastPush = now;
    }

    public IEdit? Undo()
    {
        if (_done.Count == 0) return null;
        var edit = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        edit.Undo();
        _undone.Add(edit);
        DistanceFromSave--;
        _lastPush = default;
        return edit;
    }

    public IEdit? Redo()
    {
        if (_undone.Count == 0) return null;
        var edit = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        edit.Do();
        _done.Add(edit);
        DistanceFromSave++;
        _lastPush = default;
        return edit;
    }

    public void MarkSaved() => DistanceFromSave = 0;
}
