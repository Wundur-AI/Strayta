using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>One row of the History panel: the document after <see cref="Position"/> edits (0 is the oldest state kept).</summary>
public sealed partial class HistoryItem(int position, string name, string icon, IEdit? edit) : ObservableObject
{
    /// <summary>Edits applied in this state; changes when the oldest steps are dropped.</summary>
    [ObservableProperty] public partial int Position { get; set; } = position;

    public string Name { get; } = name;

    /// <summary>Resource key of the row's icon (Icons.axaml): the tool or command that made the step.</summary>
    public string Icon { get; } = icon;

    /// <summary>The edit that led to this state; null for the opened document.</summary>
    public IEdit? Edit { get; } = edit;

    /// <summary>A state after the current one: undone, shown dimmed until a new edit discards it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    public partial bool IsFuture { get; set; }

    public double RowOpacity => IsFuture ? 0.45 : 1;

    /// <summary>The History Brush paints from this state (the left column shows the brush).</summary>
    [ObservableProperty] public partial bool IsBrushSource { get; set; }

    /// <summary>The opened document's picture, on the "Open" row while it is the oldest state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    public partial Bitmap? Thumbnail { get; set; }

    public bool HasThumbnail => Thumbnail is not null;
}

/// <summary>A snapshot row at the top of the History panel.</summary>
public sealed partial class SnapshotItem(HistorySnapshot snapshot, bool isOpen) : ObservableObject
{
    public HistorySnapshot Snapshot { get; } = snapshot;

    /// <summary>The opened document's snapshot, which cannot be deleted.</summary>
    public bool CanDelete { get; } = !isOpen;

    [ObservableProperty] public partial bool IsBrushSource { get; set; }
}

/// <summary>
/// The History panel: the active document's snapshots (the opened document first, with a thumbnail each), then its
/// states from the oldest kept to the last edit, named as the Edit menu names them. Clicking a state undoes or redoes to
/// it; clicking a snapshot restores it as a new step. The left column picks the History Brush's source. The camera
/// button takes a snapshot; the number is the History States preference (steps kept per document).
/// </summary>
public sealed partial class HistoryToolViewModel : Tool, IDisposable
{
    private DocumentViewModel? _document;
    private bool _syncing, _disposed;

    public HistoryToolViewModel(EditorViewModel editor)
    {
        Editor = editor;
        editor.PropertyChanged += OnEditorChanged;
        Follow(editor.ActiveDocument);
    }

    public EditorViewModel Editor { get; }

    public ObservableCollection<HistoryItem> Items { get; } = [];

    public ObservableCollection<SnapshotItem> Snapshots { get; } = [];

    public bool HasDocument => _document is not null;

    /// <summary>True once the panel was replaced (Window › Reset Layout) and stopped following the editor.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>The current state. Choosing another row (a click, or the arrow keys) jumps the document to it.</summary>
    [ObservableProperty] public partial HistoryItem? Current { get; set; }

    partial void OnCurrentChanged(HistoryItem? value)
    {
        if (_syncing || value is null || _document is not { } doc || value.Position == doc.HistoryPosition) return;
        doc.GoToHistory(value.Position);
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.ActiveDocument)) Follow(Editor.ActiveDocument);
    }

    private void Follow(DocumentViewModel? document)
    {
        if (ReferenceEquals(document, _document)) return;
        if (_document is not null)
        {
            _document.HistoryChanged -= Refresh;
            _document.PropertyChanged -= OnDocumentChanged;
            _document.Snapshots.CollectionChanged -= OnSnapshotsChanged;
        }
        _document = document;
        if (document is not null)
        {
            document.HistoryChanged += Refresh;
            document.PropertyChanged += OnDocumentChanged;
            document.Snapshots.CollectionChanged += OnSnapshotsChanged;
        }
        Items.Clear();
        OnPropertyChanged(nameof(HasDocument));
        RefreshSnapshots();
        Refresh();
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.HistoryBrushSource)) MarkBrushSource();
    }

    private void OnSnapshotsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshSnapshots();

    private void RefreshSnapshots()
    {
        Snapshots.Clear();
        if (_document is { } doc)
            for (int i = 0; i < doc.Snapshots.Count; i++) Snapshots.Add(new SnapshotItem(doc.Snapshots[i], isOpen: i == 0));
        MarkBrushSource();
        if (Items.Count > 0) UpdateOpenThumbnail();
    }

    /// <summary>Brings the rows in line with the undo stack, keeping rows whose state is unchanged so the list does not jump.</summary>
    private void Refresh()
    {
        _syncing = true;
        try
        {
            if (_document is not { } doc)
            {
                Items.Clear();
                Current = null;
                return;
            }
            var edits = doc.HistoryEdits;
            // Row 0 is the oldest state kept: "Open", or once steps were dropped, the state the newest dropped one made.
            var baseEdit = doc.HistoryBaseEdit;
            if (Items.Count == 0 || !ReferenceEquals(Items[0].Edit, baseEdit))
            {
                var first = baseEdit is null ? new HistoryItem(0, "Open", "IconSnapshot", null) : new HistoryItem(0, baseEdit.Description, IconFor(baseEdit), baseEdit);
                // Rows of dropped steps go; the rest shift up.
                while (Items.Count > 1 && !edits.Contains(Items[1].Edit!)) Items.RemoveAt(1);
                if (Items.Count == 0) Items.Add(first);
                else Items[0] = first;
            }
            for (int i = 0; i < edits.Count; i++)
            {
                int row = i + 1;
                var edit = edits[i];
                // An edit that merged with the next one (a slider drag) keeps its row; a new one replaces it.
                if (row < Items.Count && ReferenceEquals(Items[row].Edit, edit) && Items[row].Name == edit.Description)
                {
                    Items[row].Position = row;
                    continue;
                }
                var item = new HistoryItem(row, edit.Description, IconFor(edit), edit);
                if (row < Items.Count) Items[row] = item;
                else Items.Add(item);
            }
            while (Items.Count > edits.Count + 1) Items.RemoveAt(Items.Count - 1);

            int position = doc.HistoryPosition;
            foreach (var item in Items) item.IsFuture = item.Position > position;
            Current = Items[Math.Min(position, Items.Count - 1)];
            UpdateOpenThumbnail();
            MarkBrushSource();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The "Open" row shows the opened document's thumbnail (its snapshot's) while it is still the oldest state.</summary>
    private void UpdateOpenThumbnail()
    {
        if (Items.Count == 0) return;
        var open = Items[0].Edit is null && _document?.Snapshots.FirstOrDefault() is { } s ? s : null;
        if (open is not null && open.Thumbnail is null)
        {
            open.PropertyChanged -= OnOpenSnapshotChanged;
            open.PropertyChanged += OnOpenSnapshotChanged;
        }
        Items[0].Thumbnail = open?.Thumbnail;
    }

    private void OnOpenSnapshotChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HistorySnapshot.Thumbnail) || sender is not HistorySnapshot s) return;
        s.PropertyChanged -= OnOpenSnapshotChanged;
        if (!_disposed) UpdateOpenThumbnail();
    }

    /// <summary>Shows the History Brush icon in the left column of the source row.</summary>
    private void MarkBrushSource()
    {
        var source = _document?.HistoryBrushSource;
        foreach (var s in Snapshots) s.IsBrushSource = ReferenceEquals(s.Snapshot, source);
        foreach (var item in Items)
            item.IsBrushSource = item.Edit is null || item.Position == 0
                ? ReferenceEquals(source, DocumentViewModel.HistoryBase) || (item.Edit is not null && ReferenceEquals(source, item.Edit))
                : ReferenceEquals(source, item.Edit);
    }

    // ---- Commands ----------------------------------------------------------------------------------------

    /// <summary>The camera button: a snapshot of the document as it is now.</summary>
    [RelayCommand]
    private void NewSnapshot() => _document?.NewSnapshot();

    /// <summary>Clicking a snapshot row: the document becomes that snapshot again, as a new step.</summary>
    [RelayCommand]
    private void RestoreSnapshot(SnapshotItem? item)
    {
        if (item is not null) _document?.RestoreSnapshot(item.Snapshot);
    }

    [RelayCommand]
    private void DeleteSnapshot(SnapshotItem? item)
    {
        if (item is not null) _document?.DeleteSnapshot(item.Snapshot);
    }

    /// <summary>The left column of a state row: the History Brush paints from that state.</summary>
    [RelayCommand]
    private void SetBrushSource(HistoryItem? item)
    {
        if (item is null || _document is not { } doc) return;

        doc.SetHistoryBrushSource(item.Position);
    }

    /// <summary>The left column of a snapshot row.</summary>
    [RelayCommand]
    private void SetSnapshotBrushSource(SnapshotItem? item)
    {
        if (item is not null) _document?.SetHistoryBrushSource(item.Snapshot);
    }

    /// <summary>Stops following the editor and the document (Window › Reset Layout made a new History panel).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Editor.PropertyChanged -= OnEditorChanged;
        Follow(null);
    }

    /// <summary>The icon of the tool or command that made an edit, as Photoshop shows beside each state.</summary>
    internal static string IconFor(IEdit edit) => edit.Description switch
    {
        "Brush" => "IconBrush",
        "Eraser" => "IconEraser",
        "History Brush" => "IconHistoryBrush",
        "Gradient" => "IconGradient",
        "Paint Bucket" => "IconBucket",
        "Move" => "IconMove",
        "Free Transform" => "IconFit",
        "Magic Wand" => "IconMagicWand",
        "Quick Selection" => "IconQuickSelect",
        "Lasso" => "IconLasso",
        "Elliptical Marquee" => "IconMarqueeEllipse",
        "Object Selection" or "Select Subject" => "IconObjectSelect",
        "Delete Layer" or "Delete Layers" or "Delete Hidden Layers" => "IconTrash",
        "Merge Down" or "Merge Layers" or "Merge Visible" or "Merge Group" or "Merge Clipping Mask" or "Flatten Image" or "Stamp Visible" => "IconNewLayer",
        "Group Layers" or "Ungroup Layers" => "IconNewGroup",
        "Hide Layer" or "Show Layer" or "Hide Layers" or "Show Layers" => "IconEye",
        "Lock Layer" or "Unlock Layer" => "IconLock",
        "Link Layers" or "Unlink Layers" => "IconLink",
        "Align" or "Distribute" or "Nudge" => "IconMove",
        "Duplicate Layer" or "Duplicate Layers" => "IconDuplicate",
        "New Group" => "IconNewGroup",
        "New Layer" or "Paste" => "IconNewLayer",
        "Rasterize Layer" => "IconChecker",
        "Change Visibility" => "IconEye",
        _ when edit is RestoreSnapshotEdit => "IconSnapshot",
        _ when edit is GuideEdit => "IconMove", // guides are moved with the Move tool
        var d when d.Contains("Mask") || d is "Reveal All" or "Hide All" => "IconMask",
        _ when edit is SelectionEdit => "IconMarqueeRect",
        var d when d.StartsWith("Change ", StringComparison.Ordinal) || d.StartsWith("New ", StringComparison.Ordinal) => "IconAdjust",
        _ => "IconHistoryStep",
    };
}
