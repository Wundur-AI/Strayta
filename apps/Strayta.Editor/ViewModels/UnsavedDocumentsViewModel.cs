using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Strayta.Editor.ViewModels;

/// <summary>One unsaved document in the review list.</summary>
public sealed partial class UnsavedDocumentItem(DocumentViewModel document) : ObservableObject
{
    public DocumentViewModel Document { get; } = document;
    public string Title => Document.Title;

    /// <summary>Where it would be saved: its file, or a note that it has never been saved (Save asks where).</summary>
    public string Detail => Document.FilePath is { } path ? path : "Not saved yet: you'll be asked where to save it";

    /// <summary>Checked documents are saved; unchecked ones are closed without saving.</summary>
    [ObservableProperty] public partial bool Save { get; set; } = true;
}

/// <summary>
/// What the review window decided: save the checked documents and discard the rest, discard every document, or cancel.
/// </summary>
public enum UnsavedDecision
{
    Cancel,
    SaveChecked,
    DiscardAll,
}

/// <summary>
/// Closing several edited documents at once (quitting, File › Close All): one window lists them all instead of a
/// dialog per document. Clicking a document shows it in the main window; each has a checkbox for whether to save it.
/// </summary>
public sealed partial class UnsavedDocumentsViewModel : ObservableObject
{
    private readonly Action<DocumentViewModel> _show;

    public UnsavedDocumentsViewModel(IEnumerable<DocumentViewModel> documents, Action<DocumentViewModel> show)
    {
        _show = show;
        Items = documents.Select(d => new UnsavedDocumentItem(d)).ToList();
        foreach (var item in Items) item.PropertyChanged += (_, _) => Update();
    }

    public IReadOnlyList<UnsavedDocumentItem> Items { get; }

    public string Heading => $"{Items.Count} documents have unsaved changes";

    public int CheckedCount => Items.Count(i => i.Save);

    /// <summary>The main button: "Save All", "Save 3", or (nothing checked) "Close Without Saving".</summary>
    public string SaveText => CheckedCount == Items.Count ? "Save All" : CheckedCount == 0 ? "Close Without Saving" : $"Save {CheckedCount}";

    /// <summary>Checks or unchecks every document (the header checkbox); null while they differ.</summary>
    public bool? AllChecked
    {
        get => CheckedCount == Items.Count ? true : CheckedCount == 0 ? false : null;
        set
        {
            bool on = value ?? true;
            foreach (var item in Items) item.Save = on;
        }
    }

    [ObservableProperty] public partial UnsavedDocumentItem? Selected { get; set; }

    /// <summary>Selecting a row brings that document to the front of the main window, to look at before deciding.</summary>
    partial void OnSelectedChanged(UnsavedDocumentItem? value)
    {
        if (value is not null) _show(value.Document);
    }

    public UnsavedDecision Decision { get; private set; } = UnsavedDecision.Cancel;

    /// <summary>Raised when a button decides; the window closes.</summary>
    public event Action? Decided;

    [RelayCommand]
    private void SaveChecked() => Decide(UnsavedDecision.SaveChecked);

    [RelayCommand]
    private void DiscardAll() => Decide(UnsavedDecision.DiscardAll);

    [RelayCommand]
    private void Cancel() => Decide(UnsavedDecision.Cancel);

    private void Decide(UnsavedDecision decision)
    {
        Decision = decision;
        Decided?.Invoke();
    }

    private void Update()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(AllChecked));
    }
}
