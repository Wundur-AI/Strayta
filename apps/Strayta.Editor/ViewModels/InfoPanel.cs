using System.ComponentModel;
using Dock.Model.Mvvm.Controls;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// Window › Info (F8): the pointer's position in ruler units, the color under it, the size of the selection or of what
/// is being drawn or transformed, and the document size (<see cref="InfoReadout"/>, kept by the active document).
/// </summary>
public sealed class InfoToolViewModel : Tool
{
    public InfoToolViewModel(EditorViewModel editor)
    {
        Editor = editor;
        // The document size follows the active document and the unit even while the pointer is elsewhere.
        editor.PropertyChanged += OnEditorChanged;
        editor.ActiveDocument?.RefreshDocumentInfo();
    }

    public EditorViewModel Editor { get; }

    public InfoReadout Info => Editor.Info;

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.ActiveDocument) or nameof(EditorViewModel.RulerUnit))
        {
            if (Editor.ActiveDocument is { } doc) doc.RefreshDocumentInfo();
            else Info.DocumentSize = "";
        }
    }
}
