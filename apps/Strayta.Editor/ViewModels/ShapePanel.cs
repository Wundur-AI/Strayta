using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Paths;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// The Properties panel's Live Shape section for a shape layer, as in Photoshop: the shape's W / H / X / Y (its
/// outline's bounds, fractional pixels allowed), the live rectangle's corner radii (one field for all corners while
/// linked, four when not), and the fill and stroke. Each change is one undo step.
/// </summary>
public sealed partial class ShapePanel(DocumentViewModel document, PixelLayer layer) : PropertiesPanel(document, layer)
{
    public override string Title => "Live Shape Properties";
    public override string Icon => "IconShapeRect";

    private PixelLayer Layer { get; } = layer;

    /// <summary>Makes an edit and shows its result (the panel does not re-read on its own changes).</summary>
    private void Edit(Action edit)
    {
        Change(edit);
        OnPropertyChanged(string.Empty);
    }

    private ShapeLayerData? Data => ShapeLayers.Read(Document.Model, Layer);

    private (double L, double T, double R, double B) Box => Data?.Path.CurveBounds() ?? (0, 0, 0, 0);

    public double X { get => Math.Round(Box.L, 2); set => MoveTo(value, Box.T); }
    public double Y { get => Math.Round(Box.T, 2); set => MoveTo(Box.L, value); }
    public double W { get => Math.Round(Box.R - Box.L, 2); set => Resize(value, Box.B - Box.T); }
    public double H { get => Math.Round(Box.B - Box.T, 2); set => Resize(Box.R - Box.L, value); }

    private void MoveTo(double x, double y)
    {
        var (l, t, _, _) = Box;
        if (Data is not { } d || (Math.Abs(x - l) < 1e-6 && Math.Abs(y - t) < 1e-6)) return;
        Edit(() => Document.Apply(ShapeLayers.Edit(Document.Model, Layer, d.Offset(x - l, y - t), "Move Shape")));
    }

    private void Resize(double w, double h)
    {
        var (l, t, r, b) = Box;
        if (Data is not { } d || w <= 0 || h <= 0 || r - l <= 0 || b - t <= 0) return;
        double sx = w / (r - l), sy = h / (b - t);
        if (Math.Abs(sx - 1) < 1e-9 && Math.Abs(sy - 1) < 1e-9) return;
        Edit(() => Document.Apply(ShapeLayers.Edit(Document.Model, Layer, d.Map(p => new PathPoint(l + (p.X - l) * sx, t + (p.Y - t) * sy)), "Change Shape Size")));
    }

    // ---- Live rectangle ------------------------------------------------------------------------------

    private int LiveIndex => Data?.LiveShapes.ToList().FindIndex(s => !s.Invalidated && s.Kind is LiveShapeKind.Rectangle or LiveShapeKind.RoundedRectangle) ?? -1;

    private LiveShape? Live => LiveIndex is >= 0 and var i ? Data!.LiveShapes[i] : null;

    /// <summary>True for a live rectangle, whose corners can be rounded here.</summary>
    public bool HasCorners => Live is not null;

    /// <summary>What the live shape is (or that the outline was edited into an ordinary path).</summary>
    public string Kind => Data?.LiveShapes.FirstOrDefault() is { } s
        ? s.Invalidated ? "Path (edited)" : s.Kind switch
        {
            LiveShapeKind.RoundedRectangle or LiveShapeKind.Rectangle => "Rectangle",
            LiveShapeKind.Ellipse => "Ellipse",
            LiveShapeKind.Polygon => "Polygon",
            LiveShapeKind.Triangle => "Triangle",
            LiveShapeKind.Line => "Line",
            LiveShapeKind.Custom => "Custom Shape",
            _ => "Shape",
        }
        : "Path";

    /// <summary>One radius for every corner (the link between the fields).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSeparateCorners))]
    public partial bool LinkCorners { get; set; } = true;

    public bool ShowSeparateCorners => HasCorners && !LinkCorners;

    public double Radius { get => Live?.Radii.TopLeft ?? 0; set => SetRadii(CornerRadii.All(Math.Max(0, value))); }
    public double TopLeft { get => Live?.Radii.TopLeft ?? 0; set => SetRadii(Live?.Radii is { } r ? r with { TopLeft = Math.Max(0, value) } : default); }
    public double TopRight { get => Live?.Radii.TopRight ?? 0; set => SetRadii(Live?.Radii is { } r ? r with { TopRight = Math.Max(0, value) } : default); }
    public double BottomRight { get => Live?.Radii.BottomRight ?? 0; set => SetRadii(Live?.Radii is { } r ? r with { BottomRight = Math.Max(0, value) } : default); }
    public double BottomLeft { get => Live?.Radii.BottomLeft ?? 0; set => SetRadii(Live?.Radii is { } r ? r with { BottomLeft = Math.Max(0, value) } : default); }

    private void SetRadii(CornerRadii radii)
    {
        if (Live is not { } live || live.Radii == radii) return;
        var kind = radii.IsZero ? LiveShapeKind.Rectangle : LiveShapeKind.RoundedRectangle;
        Edit(() => Document.EditLiveShape(Layer, LiveIndex, live with { Kind = kind, Radii = radii }, "Change Corner Radius"));
        OnPropertyChanged(string.Empty);
    }

    // ---- Fill and stroke ----------------------------------------------------------------------------

    public bool HasFillColor => Data is { FillEnabled: true, Fill: ShapeContent.SolidColor };

    public Color FillColor
    {
        get => Data?.Fill is ShapeContent.SolidColor s ? ShapeOptions.ToColor(s.Color) : Colors.Gray;
        set
        {
            if (Data is not { } d || d.Fill is ShapeContent.SolidColor s && ShapeOptions.ToColor(s.Color) == value) return;
            Edit(() => Document.Apply(ShapeLayers.Edit(Document.Model, Layer, d with { Fill = ShapeContent.Solid(ShapeOptions.ToRgb(value)), FillEnabled = true }, "Change Shape Fill")));
            OnPropertyChanged(nameof(FillBrush));
        }
    }

    public IBrush FillBrush => new SolidColorBrush(FillColor);

    public bool StrokeEnabled
    {
        get => Data?.Stroke.Enabled == true;
        set
        {
            if (Data is not { } d || d.Stroke.Enabled == value) return;
            Edit(() => Document.Apply(ShapeLayers.Edit(Document.Model, Layer, d with { Stroke = d.Stroke with { Enabled = value } }, "Change Shape Stroke")));
            OnPropertyChanged(string.Empty);
        }
    }

    public double StrokeWidth
    {
        get => Math.Round(Data?.Stroke.Width ?? 0, 2);
        set
        {
            if (Data is not { } d || value < 0 || Math.Abs(d.Stroke.Width - value) < 1e-9) return;
            Edit(() => Document.Apply(ShapeLayers.Edit(Document.Model, Layer, d with { Stroke = d.Stroke with { Width = value } }, "Change Shape Stroke")));
        }
    }

    public int StrokeAlignmentIndex
    {
        get => (int)(Data?.Stroke.Alignment ?? StrokeAlignment.Inside);
        set
        {
            if (Data is not { } d || (int)d.Stroke.Alignment == value || value is < 0 or > 2) return;
            Edit(() => Document.Apply(ShapeLayers.Edit(Document.Model, Layer, d with { Stroke = d.Stroke with { Alignment = (StrokeAlignment)value } }, "Change Shape Stroke")));
        }
    }

    public Color StrokeColor
    {
        get => Data?.Stroke.Content is ShapeContent.SolidColor s ? ShapeOptions.ToColor(s.Color) : Colors.Black;
        set
        {
            if (Data is not { } d || d.Stroke.Content is ShapeContent.SolidColor s && ShapeOptions.ToColor(s.Color) == value) return;
            Edit(() => Document.Apply(ShapeLayers.Edit(Document.Model, Layer, d with { Stroke = d.Stroke with { Content = ShapeContent.Solid(ShapeOptions.ToRgb(value)) } }, "Change Shape Stroke")));
            OnPropertyChanged(nameof(StrokeBrush));
        }
    }

    public IBrush StrokeBrush => new SolidColorBrush(StrokeColor);
}
