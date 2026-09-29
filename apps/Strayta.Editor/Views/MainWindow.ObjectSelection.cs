using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Strayta.Editor.ViewModels;
using Strayta.Segmentation;

namespace Strayta.Editor.Views;

// Select › Subject, and warming Object Selection up in the background.
public partial class MainWindow
{
    /// <summary>
    /// Adds Subject to the Select menu (after Inverse, where Photoshop groups its AI selections) and starts
    /// analyzing the image whenever Object Selection is picked or another document becomes active with it.
    /// </summary>
    private void AddObjectSelectionMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var select = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Select").Menu!;
        select.Items.Add(new NativeMenuItemSeparator());
        select.Items.Add(item("Subject", Editor.SelectSubjectCommand, null, null));

        Editor.PropertyChanged += OnEditorChangedForObjectSelection;
        // The inference sessions hold native threads; release them before the runtime tears down, or ONNX Runtime
        // can abort the process on exit.
        Closed += (_, _) => SegmentationEngine.DisposeShared();
    }

    private void OnEditorChangedForObjectSelection(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.Tool) or nameof(EditorViewModel.ActiveDocument)) Editor.PauseObjectFinders(); // EditorViewModel.Sampling.cs
        if (e.PropertyName is nameof(EditorViewModel.Tool) or nameof(EditorViewModel.ActiveDocument) && Editor.IsObjectSelectTool)
            Editor.ActiveDocument?.PrepareObjectSelection();
        // Object-Aware Quick Selection uses the same analysis (EditorViewModel.QuickSelectObjects.cs).
        if (e.PropertyName is nameof(EditorViewModel.Tool) or nameof(EditorViewModel.ActiveDocument))
            Editor.PrepareQuickSelectObjects();
    }
}
