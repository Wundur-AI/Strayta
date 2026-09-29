using CommunityToolkit.Mvvm.Input;

namespace Strayta.Editor.ViewModels;

// Closing several documents: one review of every unsaved document (quitting, File › Close All) instead of a dialog each.
public sealed partial class EditorViewModel
{
    /// <summary>
    /// Shows the review of unsaved documents and returns when it is decided (the main window by default; the self-test
    /// scripts it). Without it, each document asks on its own.
    /// </summary>
    public Func<UnsavedDocumentsViewModel, Task>? ReviewUnsaved { get; set; }

    /// <summary>
    /// Settles unsaved changes before <paramref name="documents"/> close: one edited document asks as usual, several
    /// get one review listing them all. True when they may close (saved or discarded as chosen).
    /// </summary>
    internal async Task<bool> ResolveUnsavedAsync(IReadOnlyList<DocumentViewModel> documents)
    {
        var edited = documents.Where(d => d.IsModified).ToList();
        if (edited.Count == 0) return true;
        if (edited.Count == 1 || ReviewUnsaved is null)
        {
            foreach (var doc in edited)
            {
                Factory.SetActiveDockable(doc);
                if (!await ConfirmCloseAsync(doc)) return false;
            }
            return true;
        }

        var review = new UnsavedDocumentsViewModel(edited, doc => Factory.SetActiveDockable(doc));
        await ReviewUnsaved(review);
        switch (review.Decision)
        {
            case UnsavedDecision.DiscardAll:
                return true;
            case UnsavedDecision.SaveChecked:
                foreach (var item in review.Items.Where(i => i.Save))
                {
                    Factory.SetActiveDockable(item.Document);
                    // A cancelled Save As stops here: what was saved stays saved, and nothing closes.
                    if (!await SaveDocumentAsync(item.Document, saveAs: false)) return false;
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>Called when the window closes; true if every document may be discarded.</summary>
    public Task<bool> ConfirmQuitAsync() => ResolveUnsavedAsync(Factory.OpenDocuments().ToList());

    /// <summary>File › Close All (⌥⌘W): settles unsaved changes once for all documents, then closes them.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task CloseAll()
    {
        var documents = Factory.OpenDocuments().ToList();
        if (!await ResolveUnsavedAsync(documents)) return;
        foreach (var doc in documents) doc.CloseWithoutAsking();
    }
}
