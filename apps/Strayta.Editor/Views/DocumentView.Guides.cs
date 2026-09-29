using System.ComponentModel;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Rulers, guides, grid and snapping between the canvas and the document (DocumentViewModel.Guides.cs): the canvas gets
// the view settings and the document's guides, and its guide gestures become history steps.
public partial class DocumentView
{
    private EditorViewModel? _viewEditor;
    private DocumentViewModel? _viewDocument;

    private void WireGuides()
    {
        Canvas.SnapperFactory = request => _vm?.CreateSnapper(request) ?? Snapper.Off;
        Canvas.MoveBounds = () => _vm?.MovingBounds();
        Canvas.GuideAdded += guide =>
        {
            if (_vm is null) return;
            _vm.Editor.ShowGuides = true; // a guide dragged out while guides are hidden shows them, as in Photoshop
            _vm.AddGuide(guide);
        };
        Canvas.GuideMoved += (index, position) => _vm?.MoveGuide(index, position);
        Canvas.GuideDeleted += index => _vm?.DeleteGuide(index);
        Canvas.RulerOriginChanged += origin =>
        {
            if (_vm is not null) _vm.RulerOrigin = origin;
        };
        Canvas.RulerUnitChosen += unit =>
        {
            if (_vm is not null) _vm.Editor.RulerUnit = unit;
        };
        Canvas.PointerInfo += (image, box) => _vm?.UpdatePointerInfo(image, box);
        DataContextChanged += (_, _) => AttachViewSettings();
        AttachedToVisualTree += (_, _) => AttachViewSettings();
        DetachedFromVisualTree += (_, _) => DetachViewSettings();
    }

    private void AttachViewSettings()
    {
        DetachViewSettings();
        if (_vm is null || VisualRoot is null) return;
        _viewEditor = _vm.Editor;
        _viewDocument = _vm;
        _viewEditor.ViewSettingsChanged += SyncViewSettings;
        _viewDocument.PropertyChanged += OnDocumentViewChanged;
        SyncViewSettings();
    }

    private void DetachViewSettings()
    {
        if (_viewEditor is not null) _viewEditor.ViewSettingsChanged -= SyncViewSettings;
        if (_viewDocument is not null) _viewDocument.PropertyChanged -= OnDocumentViewChanged;
        _viewEditor = null;
        _viewDocument = null;
    }

    private void OnDocumentViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.Guides) or nameof(DocumentViewModel.RulerOrigin) or nameof(DocumentViewModel.Resolution)
            or nameof(DocumentViewModel.DocumentSize))
            SyncViewSettings();
    }

    /// <summary>Copies the view settings and the document's guides to the canvas (cheap: a handful of properties).</summary>
    private void SyncViewSettings()
    {
        if (_vm is not { } doc) return;
        var e = doc.Editor;
        bool rulersChanged = Canvas.ShowRulers != e.ShowRulers;
        Canvas.ShowRulers = e.ShowRulers;
        Canvas.ShowGuides = e.ShowGuides;
        Canvas.LockGuides = e.LockGuides;
        Canvas.ShowGrid = e.ShowGrid;
        Canvas.RulerUnit = e.RulerUnit;
        Canvas.DocumentResolution = doc.Resolution;
        Canvas.RulerOrigin = doc.RulerOrigin;
        Canvas.GridSpacing = e.GridSpacingPixels(doc.Model);
        Canvas.GridSubdivisions = Math.Max(1, e.GridSubdivisions);
        Canvas.Guides = doc.Guides;
        if (rulersChanged) Canvas.KeepViewForRulers(e.ShowRulers);
    }
}
