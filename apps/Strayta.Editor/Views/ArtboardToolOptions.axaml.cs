using Avalonia.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>The options bar while the Artboard tool is active (its DataContext is the editor).</summary>
public partial class ArtboardToolOptions : UserControl
{
    private DocumentViewModel? _document;

    public ArtboardToolOptions()
    {
        InitializeComponent();
        // The fields show the selected artboard: follow the document, its selection and its undo steps.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not EditorViewModel editor) return;
            editor.DocumentChanged += editor.NotifyArtboardOptions;
            editor.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(EditorViewModel.ActiveDocument)) return;
                if (_document is not null) _document.PropertyChanged -= OnDocumentChanged;
                _document = editor.ActiveDocument;
                if (_document is not null) _document.PropertyChanged += OnDocumentChanged;
                editor.NotifyArtboardOptions();
            };
        };
    }

    private void OnDocumentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.SelectedLayer) or nameof(DocumentViewModel.Display) && DataContext is EditorViewModel editor)
            editor.NotifyArtboardOptions();
    }
}
