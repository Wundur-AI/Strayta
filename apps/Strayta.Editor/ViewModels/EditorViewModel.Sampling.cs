using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

/// <summary>What a sampling tool looks at (Photoshop's Sample menu, in its order).</summary>
public enum LayerSample
{
    CurrentLayer,
    CurrentAndBelow,
    AllLayers,
    AllLayersNoAdjustments,
    CurrentAndBelowNoAdjustments,
}

/// <summary>Photoshop's Quick Selection modes: a new selection, add to it, subtract from it.</summary>
public enum QuickSelectionMode
{
    New,
    Add,
    Subtract,
}

/// <summary>Object Selection's Mode: drag a rectangle or draw a lasso around the object.</summary>
public enum ObjectSelectionShape
{
    Rectangle,
    Lasso,
}

// Sampling options of the selection tools and the Eyedropper: Magic Wand Sample Size, Photoshop's Sample menu, Quick
// Selection's New / Add / Subtract, and Object Selection's Mode and Object Finder.
public sealed partial class EditorViewModel
{
    public static IReadOnlyList<string> LayerSampleNames { get; } =
        ["Current Layer", "Current & Below", "All Layers", "All Layers No Adjustments", "Current & Below No Adjustments"];

    // ---- Magic Wand ----------------------------------------------------------------------------------------

    /// <summary>The wand's Sample Size: an index into the Eyedropper's list (Point Sample … 101 by 101 Average).</summary>
    [ObservableProperty] public partial int WandSampleSizeIndex { get; set; }

    public int WandSampleSize => EyedropperSizes[Math.Clamp(WandSampleSizeIndex, 0, EyedropperSizes.Count - 1)].Size;

    // ---- Quick Selection -------------------------------------------------------------------------------------

    /// <summary>
    /// The options bar's mode. New starts a new selection and, as in Photoshop, switches to Add after the first
    /// stroke; Shift adds and Option subtracts whatever the mode.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuickSelectNew), nameof(IsQuickSelectAdd), nameof(IsQuickSelectSubtract), nameof(QuickSelectDefaultMode))]
    public partial QuickSelectionMode QuickSelectMode { get; set; } = QuickSelectionMode.New;

    public bool IsQuickSelectNew { get => QuickSelectMode == QuickSelectionMode.New; set { if (value) QuickSelectMode = QuickSelectionMode.New; } }
    public bool IsQuickSelectAdd { get => QuickSelectMode == QuickSelectionMode.Add; set { if (value) QuickSelectMode = QuickSelectionMode.Add; } }
    public bool IsQuickSelectSubtract { get => QuickSelectMode == QuickSelectionMode.Subtract; set { if (value) QuickSelectMode = QuickSelectionMode.Subtract; } }

    /// <summary>What a stroke without modifier keys does, for the canvas: New replaces, Add adds, Subtract subtracts.</summary>
    public SelectionMode QuickSelectDefaultMode => QuickSelectMode switch
    {
        QuickSelectionMode.New => SelectionMode.Replace,
        QuickSelectionMode.Subtract => SelectionMode.Subtract,
        _ => SelectionMode.Add,
    };

    // ---- Object Selection ------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsObjectLassoMode))]
    public partial int ObjectModeIndex { get; set; }

    public bool IsObjectLassoMode => ObjectModeIndex == (int)ObjectSelectionShape.Lasso;

    public IReadOnlyList<string> ObjectModeNames { get; } = ["Rectangle", "Lasso"];

    /// <summary>
    /// Photoshop's Object Finder: the image's objects are found in the background once it has been analyzed, so hovering
    /// highlights the object under the pointer and a click selects it.
    /// </summary>
    [ObservableProperty] public partial bool ObjectFinder { get; set; } = true;

    /// <summary>
    /// Called when the tool or the active document changes: the Object Finder runs only for the active document while
    /// Object Selection is the tool.
    /// </summary>
    public void PauseObjectFinders()
    {
        foreach (var doc in Factory.OpenDocuments())
            if (!ReferenceEquals(doc, ActiveDocument) || !IsObjectSelectTool || !ObjectFinder) doc.PauseObjectFinder();
    }

    partial void OnObjectFinderChanged(bool value)
    {
        if (!value) ActiveDocument?.PauseObjectFinder();
        else if (IsObjectSelectTool) ActiveDocument?.PrepareObjectSelection();
    }
}
