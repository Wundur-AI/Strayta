using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>One row of the History panel: the document after <see cref="Position"/> edits (0 is the opened document).</summary>
public sealed partial class HistoryItem(int position, string name, string icon, IEdit? edit) : ObservableObject
{
    public int Position { get; } = position;
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
}

/// <summary>
/// The History panel: the active document's states, from "Open" to the last edit, named as the Edit menu names them.
/// Clicking a state undoes or redoes to it; the states after it are dimmed and a new edit discards them.
/// </summary>
public sealed partial class HistoryToolViewModel : Tool
{
    private DocumentViewModel? _document;
    private bool _syncing;

    public HistoryToolViewModel(EditorViewModel editor)
    {
        Editor = editor;
        editor.PropertyChanged += OnEditorChanged;
        Follow(editor.ActiveDocument);
    }

    public EditorViewModel Editor { get; }

    public ObservableCollection<HistoryItem> Items { get; } = [];

    public bool HasDocument => _document is not null;

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
        if (_document is not null) _document.HistoryChanged -= Refresh;
        _document = document;
        if (document is not null) document.HistoryChanged += Refresh;
        Items.Clear();
        OnPropertyChanged(nameof(HasDocument));
        Refresh();
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
            if (Items.Count == 0) Items.Add(new HistoryItem(0, "Open", "IconSnapshot", null));
            for (int i = 0; i < edits.Count; i++)
            {
                int row = i + 1;
                var edit = edits[i];
                // An edit that merged with the next one (a slider drag) keeps its row; a new one replaces it.
                if (row < Items.Count && ReferenceEquals(Items[row].Edit, edit) && Items[row].Name == edit.Description) continue;
                var item = new HistoryItem(row, edit.Description, IconFor(edit), edit);
                if (row < Items.Count) Items[row] = item;
                else Items.Add(item);
            }
            while (Items.Count > edits.Count + 1) Items.RemoveAt(Items.Count - 1);

            int position = doc.HistoryPosition;
            foreach (var item in Items) item.IsFuture = item.Position > position;
            Current = Items[Math.Min(position, Items.Count - 1)];
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The icon of the tool or command that made an edit, as Photoshop shows beside each state.</summary>
    internal static string IconFor(IEdit edit) => edit.Description switch
    {
        "Brush" => "IconBrush",
        "Eraser" => "IconEraser",
        "Gradient" => "IconGradient",
        "Paint Bucket" => "IconBucket",
        "Move" => "IconMove",
        "Free Transform" => "IconFit",
        "Magic Wand" => "IconMagicWand",
        "Quick Selection" => "IconQuickSelect",
        "Lasso" => "IconLasso",
        "Elliptical Marquee" => "IconMarqueeEllipse",
        "Object Selection" or "Select Subject" => "IconObjectSelect",
        "Delete Layer" => "IconTrash",
        "Duplicate Layer" => "IconDuplicate",
        "New Group" => "IconNewGroup",
        "New Layer" or "Paste" => "IconNewLayer",
        "Rasterize Layer" => "IconChecker",
        "Change Visibility" => "IconEye",
        var d when d.Contains("Mask") || d is "Reveal All" or "Hide All" => "IconMask",
        _ when edit is SelectionEdit => "IconMarqueeRect",
        var d when d.StartsWith("Change ", StringComparison.Ordinal) || d.StartsWith("New ", StringComparison.Ordinal) => "IconAdjust",
        _ => "IconHistoryStep",
    };
}
