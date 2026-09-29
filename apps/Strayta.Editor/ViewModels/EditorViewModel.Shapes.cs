using CommunityToolkit.Mvvm.Input;
using Strayta.Core.Painting;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

// The Pen group (P), Path Selection (A), the shape tools (U), their options bar (ShapeOptions) and the Paths panel's
// commands; the drawing itself is DocumentViewModel.Paths.cs, the canvas side ImageCanvas.Paths.cs.
public sealed partial class EditorViewModel
{
    /// <summary>The shape and pen tools' options bar.</summary>
    public ShapeOptions Shapes => field ??= new ShapeOptions(this);

    public bool IsShapeTool => Tool is CanvasTool.Rectangle or CanvasTool.Ellipse or CanvasTool.Triangle or CanvasTool.Polygon or CanvasTool.Line or CanvasTool.CustomShape;
    public bool IsPenTool => Tool is CanvasTool.Pen or CanvasTool.AddAnchor or CanvasTool.DeleteAnchor or CanvasTool.ConvertPoint;
    public bool IsPathSelectTool => Tool is CanvasTool.PathSelect or CanvasTool.DirectSelect;

    /// <summary>A tool whose options bar has fill, stroke and Shape / Path mode (shapes and the pen).</summary>
    public bool IsShapeOrPenTool => IsShapeTool || IsPenTool;

    public bool IsRectangleTool => Tool == CanvasTool.Rectangle;
    public bool IsPolygonTool => Tool == CanvasTool.Polygon;
    public bool IsTriangleTool => Tool == CanvasTool.Triangle;
    public bool IsLineTool => Tool == CanvasTool.Line;
    public bool IsCustomShapeTool => Tool == CanvasTool.CustomShape;
    public bool HasCornerRadius => Tool is CanvasTool.Rectangle or CanvasTool.Triangle or CanvasTool.Polygon;

    private ToolGroup _penGroup = null!, _pathSelectGroup = null!, _shapeGroup = null!;

    /// <summary>The P, A and U slots, in Photoshop's order and with its icons.</summary>
    private (ToolGroup Pen, ToolGroup PathSelect, ToolGroup Shape) CreatePathToolGroups()
    {
        _penGroup = new ToolGroup(this, "P",
            new(CanvasTool.Pen, "Pen", "IconPen"),
            new(CanvasTool.AddAnchor, "Add Anchor Point", "IconPenAdd"),
            new(CanvasTool.DeleteAnchor, "Delete Anchor Point", "IconPenDelete"),
            new(CanvasTool.ConvertPoint, "Convert Point", "IconConvertPoint"));
        _pathSelectGroup = new ToolGroup(this, "A",
            new(CanvasTool.PathSelect, "Path Selection", "IconPathSelect"),
            new(CanvasTool.DirectSelect, "Direct Selection", "IconDirectSelect"));
        _shapeGroup = new ToolGroup(this, "U",
            new(CanvasTool.Rectangle, "Rectangle", "IconShapeRect"),
            new(CanvasTool.Ellipse, "Ellipse", "IconShapeEllipse"),
            new(CanvasTool.Triangle, "Triangle", "IconShapeTriangle"),
            new(CanvasTool.Polygon, "Polygon", "IconShapePolygon"),
            new(CanvasTool.Line, "Line", "IconShapeLine"),
            new(CanvasTool.CustomShape, "Custom Shape", "IconShapeCustom"));
        return (_penGroup, _pathSelectGroup, _shapeGroup);
    }

    /// <summary>Called when the tool changes (EditorViewModel.Tools.cs).</summary>
    private void SyncPathTools()
    {
        OnPropertyChanged(nameof(IsShapeTool));
        OnPropertyChanged(nameof(IsPenTool));
        OnPropertyChanged(nameof(IsPathSelectTool));
        OnPropertyChanged(nameof(IsShapeOrPenTool));
        OnPropertyChanged(nameof(IsRectangleTool));
        OnPropertyChanged(nameof(IsPolygonTool));
        OnPropertyChanged(nameof(IsTriangleTool));
        OnPropertyChanged(nameof(IsLineTool));
        OnPropertyChanged(nameof(IsCustomShapeTool));
        OnPropertyChanged(nameof(HasCornerRadius));
        if (ActiveDocument is not { } doc) return;
        if (Tool != CanvasTool.Pen) doc.FinishPen();
        if (IsShapeOrPenTool && doc.SelectedLayer?.Node is { } node && Editing.ShapeLayers.Read(doc.Model, node) is { } data)
            Shapes.LoadFrom(data);
        doc.RefreshPathOverlay();
    }

    // ---- Paths panel commands ----------------------------------------------------------------------

    /// <summary>Fill Path with the foreground color.</summary>
    [RelayCommand] private Task FillPath() => ActiveDocument?.FillPathAsync(new FillOptions(new FillSource.Color(CurrentColor))) ?? Task.CompletedTask;

    /// <summary>Stroke Path with the current brush and foreground color.</summary>
    [RelayCommand] private Task StrokePath() => ActiveDocument?.StrokePathAsync(CurrentBrush, CurrentColor) ?? Task.CompletedTask;

    /// <summary>⌘Return: load the path as a selection.</summary>
    [RelayCommand] private void LoadPathAsSelection() => ActiveDocument?.LoadPathAsSelection();

    /// <summary>Make Work Path from the selection (the panel's button uses Photoshop's default tolerance, 2 pixels).</summary>
    [RelayCommand] private void MakeWorkPath() => ActiveDocument?.MakeWorkPathFromSelection(2);

    [RelayCommand] private void NewPath() => ActiveDocument?.SavePath();

    [RelayCommand]
    private void DeletePath()
    {
        if (ActiveDocument is { SelectedPathIndex: >= 0 } doc) doc.DeletePath(doc.SelectedPathIndex);
    }

    /// <summary>Asks for the feather radius, then makes the selection (the Paths panel menu's Make Selection…).</summary>
    public Func<Task<float?>>? AskFeather { get; set; }

    /// <summary>Asks for Make Work Path's tolerance.</summary>
    public Func<Task<double?>>? AskTolerance { get; set; }

    [RelayCommand]
    private async Task MakeSelectionWithFeather()
    {
        if (ActiveDocument is not { } doc) return;
        float? feather = AskFeather is { } ask ? await ask() : 0;
        if (feather is { } f) doc.LoadPathAsSelection(f);
    }

    [RelayCommand]
    private async Task MakeWorkPathWithTolerance()
    {
        if (ActiveDocument is not { } doc) return;
        double? tolerance = AskTolerance is { } ask ? await ask() : 2;
        if (tolerance is { } t) doc.MakeWorkPathFromSelection(t);
    }
}
