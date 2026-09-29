namespace Strayta.Editor.Editing;

/// <summary>A reversible change to a document.</summary>
public interface IEdit
{
    string Description { get; }

    /// <summary>True when the edit changes the layer tree's shape (order, parents), not just properties.</summary>
    bool ChangesStructure { get; }

    /// <summary>
    /// False for history steps that don't change what is saved (selections): they are undoable but don't mark the
    /// document as edited.
    /// </summary>
    bool ChangesContent => true;

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

    // The save point is the last content-changing edit applied when the file was saved (null: none). It stays
    // reachable while it is in the done list, or in the undone list that Redo can bring back.
    private IEdit? _savedEdit;
    private bool _saveReachable = true;

    /// <summary>True when the document differs from the saved file; steps that change nothing saved don't count.</summary>
    public bool IsModified => !_saveReachable || LastContentEdit() != _savedEdit;

    private IEdit? LastContentEdit()
    {
        for (int i = _done.Count - 1; i >= 0; i--)
            if (_done[i].ChangesContent) return _done[i];
        return null;
    }

    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;
    public string? UndoDescription => _done.Count > 0 ? _done[^1].Description : null;
    public string? RedoDescription => _undone.Count > 0 ? _undone[^1].Description : null;

    /// <summary>Applied edits, oldest first (the History panel's states up to the current one).</summary>
    public IReadOnlyList<IEdit> Done => _done;

    /// <summary>Undone edits that Redo would bring back, the next one last.</summary>
    public IReadOnlyList<IEdit> Undone => _undone;

    public void Push(IEdit edit)
    {
        edit.Do();
        var now = DateTime.UtcNow;
        // Never fold into the saved edit: the document would change while still looking saved.
        bool merged = _done.Count > 0 && now - _lastPush < MergeWindow && _done[^1] != _savedEdit && _done[^1].TryMerge(edit);
        if (!merged) _done.Add(edit);
        if (_savedEdit is not null && _undone.Contains(_savedEdit)) _saveReachable = false;
        _undone.Clear();
        _lastPush = now;
        PushCount++;
        Trim();
    }

    /// <summary>Photoshop's History States preference: how many steps are kept; older ones are dropped (at least 1).</summary>
    public int Limit
    {
        get => _limit;
        set
        {
            _limit = Math.Max(1, value);
            Trim();
        }
    }

    private int _limit = int.MaxValue;

    /// <summary>Incremented by every push (a new or merged edit), so observers can tell a push from an undo or redo.</summary>
    public int PushCount { get; private set; }

    /// <summary>The newest edit dropped by <see cref="Limit"/>: the oldest state still reachable is the one after it (null: the opened document).</summary>
    public IEdit? BaseEdit { get; private set; }

    private void Trim()
    {
        while (_done.Count > _limit)
        {
            var dropped = _done[0];
            _done.RemoveAt(0);
            // The saved state was the old base: once a content edit after it is dropped, it can never be reached again.
            if (_savedEdit is null && dropped.ChangesContent) _saveReachable = false;
            if (ReferenceEquals(dropped, _savedEdit)) _savedEdit = null; // saved exactly at the new base
            BaseEdit = dropped;
        }
    }

    public IEdit? Undo()
    {
        if (_done.Count == 0) return null;
        var edit = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        edit.Undo();
        _undone.Add(edit);
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
        _lastPush = default;
        return edit;
    }

    public void MarkSaved()
    {
        _savedEdit = LastContentEdit();
        _saveReachable = true;
    }
}
