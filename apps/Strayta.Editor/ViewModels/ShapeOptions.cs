using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Paths;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>What the shape tools and the Pen make: shape layers, or paths.</summary>
public enum ShapeToolMode
{
    Shape,
    Path,
}

/// <summary>What fills a shape or its stroke in the options bar (Photoshop's No Color / Solid Color / Gradient / Pattern).</summary>
public enum ShapePaintKind
{
    None,
    Color,
    Gradient,
    Pattern,
}

/// <summary>
/// The shape and pen tools' options bar (Photoshop's): Shape or Path mode, fill and stroke (none, a color, a gradient
/// or a pattern, with the stroke's width, alignment, dashes, caps and joins), W and H, the path operation (a new layer,
/// or combine / subtract / intersect / exclude with the selected shape), the rectangle's corner radius, the polygon's
/// sides and star ratio, the line's weight and arrowheads, and the custom shape. With a shape layer selected, fill and
/// stroke changes restyle it (one undo step per change, a slider drag merged), as in Photoshop.
/// </summary>
public sealed partial class ShapeOptions : ObservableObject
{
    private readonly EditorViewModel _editor;
    private bool _loading;

    public ShapeOptions(EditorViewModel editor)
    {
        _editor = editor;
        FillGradient = GradientModel.TwoColor("Black, White", RgbColor.Black, new RgbColor(1, 1, 1));
        StrokeGradient = FillGradient;
    }

    public static IReadOnlyList<string> OperationNames { get; } = ["New Layer", "Combine Shapes", "Subtract Front Shape", "Intersect Shape Areas", "Exclude Overlapping Shapes"];
    public static IReadOnlyList<string> AlignmentNames { get; } = ["Inside", "Center", "Outside"];
    public static IReadOnlyList<string> CapNames { get; } = ["Butt", "Round", "Square"];
    public static IReadOnlyList<string> JoinNames { get; } = ["Miter", "Round", "Bevel"];
    public static IReadOnlyList<string> DashNames { get; } = ShapeStroke.Presets.Select(p => p.Name).ToList();
    public static IReadOnlyList<string> PaintKindNames { get; } = ["None", "Color", "Gradient", "Pattern"];
    public static IReadOnlyList<string> CustomShapeNames => ShapeGeometry.CustomShapeNames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShapeMode), nameof(IsPathMode), nameof(ModeIndex))]
    public partial ShapeToolMode Mode { get; set; }

    /// <summary>The options bar's Shape / Path choice.</summary>
    public int ModeIndex { get => (int)Mode; set => Mode = (ShapeToolMode)Math.Clamp(value, 0, 1); }

    public bool IsShapeMode { get => Mode == ShapeToolMode.Shape; set { if (value) Mode = ShapeToolMode.Shape; } }
    public bool IsPathMode { get => Mode == ShapeToolMode.Path; set { if (value) Mode = ShapeToolMode.Path; } }

    // ---- Fill ----------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FillBrush), nameof(FillIsColor), nameof(FillIsGradient), nameof(FillIsPattern))]
    public partial int FillKindIndex { get; set; } = (int)ShapePaintKind.Color;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FillBrush))]
    public partial Color FillColor { get; set; } = Color.FromRgb(0x7F, 0x7F, 0x7F);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FillBrush))]
    public partial Gradient FillGradient { get; set; }

    [ObservableProperty] public partial Pattern? FillPattern { get; set; }

    public bool FillIsColor => FillKindIndex == (int)ShapePaintKind.Color;
    public bool FillIsGradient => FillKindIndex == (int)ShapePaintKind.Gradient;
    public bool FillIsPattern => FillKindIndex == (int)ShapePaintKind.Pattern;

    public IBrush FillBrush => SwatchBrush((ShapePaintKind)FillKindIndex, FillColor, FillGradient);

    // ---- Stroke --------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrokeBrush), nameof(StrokeIsColor), nameof(StrokeIsGradient), nameof(StrokeIsPattern))]
    public partial int StrokeKindIndex { get; set; } = (int)ShapePaintKind.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrokeBrush))]
    public partial Color StrokeColor { get; set; } = Colors.Black;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrokeBrush))]
    public partial Gradient StrokeGradient { get; set; }

    [ObservableProperty] public partial Pattern? StrokePattern { get; set; }

    public bool StrokeIsColor => StrokeKindIndex == (int)ShapePaintKind.Color;
    public bool StrokeIsGradient => StrokeKindIndex == (int)ShapePaintKind.Gradient;
    public bool StrokeIsPattern => StrokeKindIndex == (int)ShapePaintKind.Pattern;

    public IBrush StrokeBrush => SwatchBrush((ShapePaintKind)StrokeKindIndex, StrokeColor, StrokeGradient);

    /// <summary>Stroke width in pixels.</summary>
    [ObservableProperty] public partial double StrokeWidth { get; set; } = 3;

    [ObservableProperty] public partial int StrokeAlignmentIndex { get; set; }
    [ObservableProperty] public partial int DashIndex { get; set; }
    [ObservableProperty] public partial int CapIndex { get; set; }
    [ObservableProperty] public partial int JoinIndex { get; set; }

    // ---- Geometry ------------------------------------------------------------------------------------

    /// <summary>The last shape's (or the selected shape's) width and height; typing resizes the selected shape.</summary>
    [ObservableProperty] public partial double Width { get; set; } = 100;
    [ObservableProperty] public partial double Height { get; set; } = 100;

    /// <summary>0 new layer, then Photoshop's path operations.</summary>
    [ObservableProperty] public partial int OperationIndex { get; set; }

    [ObservableProperty] public partial double CornerRadius { get; set; }
    [ObservableProperty] public partial double Sides { get; set; } = 5;

    /// <summary>Photoshop's Star Ratio in percent: 100 is a plain polygon, less pulls every other point in.</summary>
    [ObservableProperty] public partial double StarRatio { get; set; } = 100;

    [ObservableProperty] public partial double LineWeight { get; set; } = 1;
    [ObservableProperty] public partial bool ArrowStart { get; set; }
    [ObservableProperty] public partial bool ArrowEnd { get; set; }
    [ObservableProperty] public partial string CustomShape { get; set; } = "Heart";

    /// <summary>The operation a new shape or path component gets, or null for a new layer.</summary>
    public PathOperation? Operation => OperationIndex <= 0 ? null : (PathOperation)(OperationIndex switch { 1 => 1, 2 => 2, 3 => 3, _ => 0 });

    // ---- Building shapes --------------------------------------------------------------------------------

    public ShapeContent FillContent => Content((ShapePaintKind)FillKindIndex, FillColor, FillGradient, FillPattern);

    public ShapeStroke Stroke
    {
        get
        {
            var preset = ShapeStroke.Presets[Math.Clamp(DashIndex, 0, ShapeStroke.Presets.Count - 1)];
            return new ShapeStroke
            {
                Enabled = StrokeKindIndex != (int)ShapePaintKind.None,
                Width = Math.Max(0, StrokeWidth),
                Alignment = (StrokeAlignment)Math.Clamp(StrokeAlignmentIndex, 0, 2),
                Cap = DashIndex == 2 ? preset.Cap : (LineCap)Math.Clamp(CapIndex, 0, 2),
                Join = (LineJoin)Math.Clamp(JoinIndex, 0, 2),
                Dashes = preset.Dashes,
                Content = Content(StrokeKindIndex == 0 ? ShapePaintKind.Color : (ShapePaintKind)StrokeKindIndex, StrokeColor, StrokeGradient, StrokePattern),
            };
        }
    }

    /// <summary>A new shape layer's data for <paramref name="path"/>.</summary>
    public ShapeLayerData NewShape(VectorPath path, IReadOnlyList<LiveShape> live) => new()
    {
        Path = path,
        Fill = FillContent,
        FillEnabled = FillKindIndex != (int)ShapePaintKind.None,
        Stroke = Stroke,
        LiveShapes = live,
    };

    /// <summary>The live shape a tool draws into a box (or, for the line, between two points).</summary>
    public LiveShape LiveShapeFor(CanvasTool tool, double left, double top, double right, double bottom, PathPoint lineStart, PathPoint lineEnd) => tool switch
    {
        CanvasTool.Rectangle when CornerRadius > 0 => new LiveShape(LiveShapeKind.RoundedRectangle, left, top, right, bottom) { Radii = CornerRadii.All(CornerRadius) },
        CanvasTool.Rectangle => new LiveShape(LiveShapeKind.Rectangle, left, top, right, bottom),
        CanvasTool.Ellipse => new LiveShape(LiveShapeKind.Ellipse, left, top, right, bottom),
        CanvasTool.Triangle => new LiveShape(LiveShapeKind.Triangle, left, top, right, bottom) { CornerRadius = CornerRadius },
        CanvasTool.Polygon => new LiveShape(LiveShapeKind.Polygon, left, top, right, bottom)
        {
            Sides = (int)Math.Clamp(Math.Round(Sides), 3, 100), StarIndent = Math.Clamp(1 - StarRatio / 100, 0, 0.99), CornerRadius = CornerRadius,
        },
        CanvasTool.Line => new LiveShape(LiveShapeKind.Line, left, top, right, bottom)
        {
            LineStart = lineStart, LineEnd = lineEnd, LineWeight = Math.Max(0.1, LineWeight),
            StartArrow = ArrowStart ? new ArrowHead() : null, EndArrow = ArrowEnd ? new ArrowHead() : null,
        },
        _ => new LiveShape(LiveShapeKind.Custom, left, top, right, bottom) { CustomName = CustomShape },
    };

    // ---- Following the selected shape ------------------------------------------------------------------

    /// <summary>Shows a shape layer's fill, stroke and size (when one is selected), without restyling it.</summary>
    public void LoadFrom(ShapeLayerData data)
    {
        _loading = true;
        try
        {
            (FillKindIndex, FillColor, FillGradient, FillPattern) = Split(data.FillEnabled ? data.Fill : null, FillColor, FillGradient, FillPattern);
            var s = data.Stroke;
            (StrokeKindIndex, StrokeColor, StrokeGradient, StrokePattern) = Split(s.Enabled ? s.Content : null, StrokeColor, StrokeGradient, StrokePattern);
            StrokeWidth = Math.Round(s.Width, 2);
            StrokeAlignmentIndex = (int)s.Alignment;
            CapIndex = (int)s.Cap;
            JoinIndex = (int)s.Join;
            DashIndex = s.Dashes.Count == 0 ? 0 : s.Cap == LineCap.Round && s.Dashes[0] == 0 ? 2 : 1;
            if (data.Path.CurveBounds() is var (l, t, r, b))
            {
                Width = Math.Round(r - l, 2);
                Height = Math.Round(b - t, 2);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private static (int, Color, Gradient, Pattern?) Split(ShapeContent? c, Color color, Gradient gradient, Pattern? pattern) => c switch
    {
        null => ((int)ShapePaintKind.None, color, gradient, pattern),
        ShapeContent.SolidColor s => ((int)ShapePaintKind.Color, ToColor(s.Color), gradient, pattern),
        ShapeContent.GradientContent g => ((int)ShapePaintKind.Gradient, color, g.Fill.Gradient, pattern),
        ShapeContent.PatternContent p => ((int)ShapePaintKind.Pattern, color, gradient, p.Fill.Pattern.Resolved ?? pattern),
        _ => ((int)ShapePaintKind.Color, color, gradient, pattern),
    };

    partial void OnFillKindIndexChanged(int value) => Restyle("Change Shape Fill");
    partial void OnFillColorChanged(Color value) => Restyle("Change Shape Fill");
    partial void OnFillGradientChanged(Gradient value) => Restyle("Change Shape Fill");
    partial void OnFillPatternChanged(Pattern? value) => Restyle("Change Shape Fill");
    partial void OnStrokeKindIndexChanged(int value) => Restyle("Change Shape Stroke");
    partial void OnStrokeColorChanged(Color value) => Restyle("Change Shape Stroke");
    partial void OnStrokeGradientChanged(Gradient value) => Restyle("Change Shape Stroke");
    partial void OnStrokePatternChanged(Pattern? value) => Restyle("Change Shape Stroke");
    partial void OnStrokeWidthChanged(double value) => Restyle("Change Shape Stroke");
    partial void OnStrokeAlignmentIndexChanged(int value) => Restyle("Change Shape Stroke");
    partial void OnDashIndexChanged(int value) => Restyle("Change Shape Stroke");
    partial void OnCapIndexChanged(int value) => Restyle("Change Shape Stroke");
    partial void OnJoinIndexChanged(int value) => Restyle("Change Shape Stroke");
    partial void OnWidthChanged(double value) => Resize();
    partial void OnHeightChanged(double value) => Resize();

    /// <summary>With a shape tool active and a shape layer selected, a fill or stroke change restyles that layer.</summary>
    private void Restyle(string description)
    {
        if (_loading || !_editor.IsShapeOrPenTool) return;
        var fill = FillContent;
        bool fillOn = FillKindIndex != (int)ShapePaintKind.None;
        var stroke = Stroke;
        _editor.ActiveDocument?.RestyleSelectedShape(d => d with
        {
            Fill = fillOn ? fill : d.Fill,
            FillEnabled = fillOn,
            Stroke = stroke with { SourceData = d.Stroke.SourceData, MiterLimit = d.Stroke.MiterLimit, Opacity = d.Stroke.Opacity, BlendMode = d.Stroke.BlendMode },
        }, description);
    }

    /// <summary>Shows a size (the shape being drawn) in W and H without resizing anything.</summary>
    public void ShowSizeWithoutResizing(double width, double height)
    {
        _loading = true;
        try
        {
            Width = width;
            Height = height;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Resize()
    {
        if (_loading || !_editor.IsShapeOrPenTool) return;
        _editor.ActiveDocument?.ResizeSelectedShape(Width, Height);
    }

    [RelayCommand]
    private void SetDefaultStyle()
    {
        FillKindIndex = (int)ShapePaintKind.Color;
        FillColor = ToColor(_editor.ForegroundRgb);
        StrokeKindIndex = (int)ShapePaintKind.None;
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private static ShapeContent Content(ShapePaintKind kind, Color color, Gradient gradient, Pattern? pattern) => kind switch
    {
        ShapePaintKind.Gradient => new ShapeContent.GradientContent(new GradientFill(gradient) { Angle = 90 }),
        ShapePaintKind.Pattern when pattern is not null => new ShapeContent.PatternContent(new PatternFill(PatternReference.To(pattern))),
        _ => ShapeContent.Solid(ToRgb(color)),
    };

    public static RgbColor ToRgb(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    public static Color ToColor(RgbColor c) =>
        Color.FromRgb((byte)Math.Round(Math.Clamp(c.R, 0f, 1f) * 255), (byte)Math.Round(Math.Clamp(c.G, 0f, 1f) * 255), (byte)Math.Round(Math.Clamp(c.B, 0f, 1f) * 255));

    private static IBrush SwatchBrush(ShapePaintKind kind, Color color, Gradient gradient)
    {
        switch (kind)
        {
            case ShapePaintKind.None:
                return new LinearGradientBrush
                {
                    StartPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
                    EndPoint = new Avalonia.RelativePoint(1, 0, Avalonia.RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Colors.White, 0.46), new GradientStop(Colors.Red, 0.47), new GradientStop(Colors.Red, 0.53), new GradientStop(Colors.White, 0.54) },
                };
            case ShapePaintKind.Gradient:
                var brush = new LinearGradientBrush { StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative), EndPoint = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative) };
                foreach (var s in gradient.Colors) brush.GradientStops.Add(new GradientStop(ToColor(s.Color), s.Location));
                return brush;
            case ShapePaintKind.Pattern:
                return new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
            default:
                return new SolidColorBrush(color);
        }
    }
}
