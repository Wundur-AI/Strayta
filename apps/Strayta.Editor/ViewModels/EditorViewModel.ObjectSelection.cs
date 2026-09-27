using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Editor.Controls;
using Strayta.Segmentation;

namespace Strayta.Editor.ViewModels;

// Object Selection tool options and Select › Subject.
public sealed partial class EditorViewModel
{
    public bool IsObjectSelectTool { get => Tool == CanvasTool.ObjectSelect; set { if (value) Tool = CanvasTool.ObjectSelect; } }

    /// <summary>
    /// Object Selection and Select Subject look at the whole image rather than the selected layer. Photoshop starts
    /// with this off; it is on here because documents opened in Strayta are mostly layered PSDs whose selected layer
    /// is often text or a small element, where sampling it alone surprises.
    /// </summary>
    [ObservableProperty] public partial bool ObjectSampleAllLayers { get; set; } = true;

    partial void OnObjectSampleAllLayersChanged(bool value)
    {
        if (IsObjectSelectTool) ActiveDocument?.PrepareObjectSelection();
    }

    /// <summary>False until the models are fetched; the options bar then explains how.</summary>
    public bool HasSelectionModels => SegmentationEngine.Shared.CanSelectObjects && SegmentationEngine.Shared.CanSelectSubject;

    public string SelectionModelsHint => SegmentationModels.FetchHint;

    [RelayCommand]
    private Task SelectSubject() => ActiveDocument?.SelectSubjectAsync() ?? Task.CompletedTask;
}
