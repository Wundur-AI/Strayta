using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Segmentation;

namespace Strayta.Editor.ViewModels;

// Quick Selection's Object-Aware option (DocumentViewModel.QuickSelectObjects.cs does the work).
public sealed partial class EditorViewModel
{
    /// <summary>
    /// Quick Selection asks the local SAM model which object is being brushed and keeps the growth to it: shaded
    /// objects fill in one stroke and the selection stops at their outline even where the image barely shows it.
    /// Off by default while it proves itself; it needs the object model (see <see cref="HasObjectModel"/>).
    /// </summary>
    [ObservableProperty] public partial bool QuickSelectObjectAware { get; set; }

    partial void OnQuickSelectObjectAwareChanged(bool value) => PrepareQuickSelectObjects();

    partial void OnQuickSelectSampleAllLayersChanged(bool value) => PrepareQuickSelectObjects();

    /// <summary>Starts analyzing the active document's image when Object-Aware Quick Selection will need it.</summary>
    public void PrepareQuickSelectObjects()
    {
        if (IsQuickSelectTool && QuickSelectObjectAware) ActiveDocument?.PrepareQuickSelectObjects();
    }

    /// <summary>False until the SAM model is fetched; Object-Aware is disabled then and its tooltip says how to get it.</summary>
    public bool HasObjectModel => SegmentationEngine.Shared.CanSelectObjects;

    public string QuickSelectObjectAwareTip => HasObjectModel
        ? "Uses the local AI model (SAM, runs on this computer) to find the object you brush over: the selection fills " +
          "shaded and detailed objects in one stroke and stops at their outline instead of leaking into similar " +
          "colors. Painting walls, sky or other large areas works as before."
        : SegmentationModels.FetchHint;
}
