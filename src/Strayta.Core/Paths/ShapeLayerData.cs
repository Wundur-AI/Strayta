namespace Strayta.Core.Paths;

/// <summary>What fills a shape or its stroke: a color, a gradient or a pattern (Photoshop's fill layer kinds).</summary>
public abstract record ShapeContent
{
    public sealed record SolidColor(RgbColor Color) : ShapeContent;

    public sealed record GradientContent(GradientFill Fill) : ShapeContent;

    public sealed record PatternContent(PatternFill Fill) : ShapeContent;

    public static ShapeContent Solid(RgbColor c) => new SolidColor(c);
}

/// <summary>
/// A shape's stroke (Photoshop's 'vstk' stroke style): whether it is drawn, its width in pixels, where it lies, caps,
/// joins, dashes, what it is filled with, its opacity and blend mode.
/// </summary>
public sealed record ShapeStroke
{
    public bool Enabled { get; init; }
    public double Width { get; init; } = 1;
    public StrokeAlignment Alignment { get; init; } = StrokeAlignment.Inside;
    public LineCap Cap { get; init; } = LineCap.Butt;
    public LineJoin Join { get; init; } = LineJoin.Miter;

    /// <summary>As Photoshop stores it (100 by default: miters are practically never cut off).</summary>
    public double MiterLimit { get; init; } = 100;

    /// <summary>Dash and gap lengths in multiples of the width; empty for a solid line.</summary>
    public IReadOnlyList<double> Dashes { get; init; } = [];
    public double DashOffset { get; init; }
    public ShapeContent Content { get; init; } = ShapeContent.Solid(RgbColor.Black);
    public float Opacity { get; init; } = 1f;
    public BlendMode BlendMode { get; init; } = BlendMode.Normal;

    /// <summary>Format data the stroke was read from (for PSD, the 'vstk' descriptor), so unknown settings survive.</summary>
    public object? SourceData { get; init; }

    public bool Equals(ShapeStroke? o) => o is not null && Enabled == o.Enabled && Width == o.Width && Alignment == o.Alignment && Cap == o.Cap
        && Join == o.Join && MiterLimit == o.MiterLimit && Dashes.SequenceEqual(o.Dashes) && DashOffset == o.DashOffset
        && Equals(Content, o.Content) && Opacity == o.Opacity && BlendMode == o.BlendMode;

    public override int GetHashCode() => HashCode.Combine(Enabled, Width, Alignment, Content);

    public StrokeGeometry Geometry(double width) => new(width, Cap, Join, MiterLimit, Dashes, DashOffset);

    /// <summary>Photoshop's dash presets: solid, dashed (4 2), dotted (0 2 with round caps).</summary>
    public static IReadOnlyList<(string Name, double[] Dashes, LineCap Cap)> Presets { get; } =
    [
        ("Solid", [], LineCap.Butt),
        ("Dashed", [4, 2], LineCap.Butt),
        ("Dotted", [0, 2], LineCap.Round),
    ];
}

/// <summary>Kinds of live shape: the ones Photoshop records (rectangle, rounded rectangle, ellipse) and Strayta's others.</summary>
public enum LiveShapeKind
{
    Rectangle,
    RoundedRectangle,
    Ellipse,
    Polygon,
    Triangle,
    Line,
    Custom,

    /// <summary>A shape the file describes in a way Strayta does not model; kept as it was.</summary>
    Other,
}

/// <summary>
/// The parameters a shape was drawn from (Photoshop's "live shape" origination, 'vogk'), so its box and corner radii
/// stay editable. <see cref="Index"/> links it to the subpaths it made (their origin index).
/// </summary>
public sealed record LiveShape(LiveShapeKind Kind, double Left, double Top, double Right, double Bottom)
{
    public CornerRadii Radii { get; init; }
    public int Sides { get; init; } = 5;
    public double StarIndent { get; init; }
    public double CornerRadius { get; init; }
    public PathPoint LineStart { get; init; }
    public PathPoint LineEnd { get; init; }
    public double LineWeight { get; init; } = 1;
    public ArrowHead? StartArrow { get; init; }
    public ArrowHead? EndArrow { get; init; }
    public string? CustomName { get; init; }

    /// <summary>Photoshop's "keyShapeInvalidated": the path was edited, so these parameters no longer describe it.</summary>
    public bool Invalidated { get; init; }

    public int Index { get; init; }

    /// <summary>The descriptor this entry was read from, for round trips.</summary>
    public object? SourceData { get; init; }

    public double Width => Right - Left;
    public double Height => Bottom - Top;

    /// <summary>The subpaths this shape draws.</summary>
    public IReadOnlyList<Subpath> Build(PathOperation op = PathOperation.Combine)
    {
        var list = Kind switch
        {
            LiveShapeKind.Rectangle => [ShapeGeometry.Rectangle(Left, Top, Right, Bottom, op)],
            LiveShapeKind.RoundedRectangle => [ShapeGeometry.RoundedRectangle(Left, Top, Right, Bottom, Radii, op)],
            LiveShapeKind.Ellipse => [ShapeGeometry.Ellipse(Left, Top, Right, Bottom, op)],
            LiveShapeKind.Polygon => [ShapeGeometry.Polygon(Left, Top, Right, Bottom, Sides, StarIndent, CornerRadius, op)],
            LiveShapeKind.Triangle => [ShapeGeometry.Triangle(Left, Top, Right, Bottom, CornerRadius, op)],
            LiveShapeKind.Line => [ShapeGeometry.Line(LineStart, LineEnd, LineWeight, StartArrow, EndArrow, op)],
            LiveShapeKind.Custom => ShapeGeometry.CustomShape(CustomName ?? "Heart", Left, Top, Right, Bottom, op),
            _ => (IReadOnlyList<Subpath>)[],
        };
        return list.Select(s => s with { OriginIndex = Index }).ToArray();
    }

    /// <summary>The same shape moved and scaled into another box (lines map their end points).</summary>
    public LiveShape WithBox(double left, double top, double right, double bottom)
    {
        double sx = Width == 0 ? 1 : (right - left) / Width, sy = Height == 0 ? 1 : (bottom - top) / Height;
        PathPoint Map(PathPoint p) => new(left + (p.X - Left) * sx, top + (p.Y - Top) * sy);
        return this with { Left = left, Top = top, Right = right, Bottom = bottom, LineStart = Map(LineStart), LineEnd = Map(LineEnd) };
    }
}

/// <summary>
/// The editable content of a shape layer: its outline (the vector mask), what fills it, its stroke and the live shapes
/// it was drawn from. Immutable; the writer compares with what it read to keep unedited data byte for byte.
/// </summary>
public sealed record ShapeLayerData
{
    public required VectorPath Path { get; init; }
    public ShapeContent Fill { get; init; } = ShapeContent.Solid(RgbColor.Black);

    /// <summary>False for a shape with fill "none": only its stroke shows.</summary>
    public bool FillEnabled { get; init; } = true;

    public ShapeStroke Stroke { get; init; } = new();
    public IReadOnlyList<LiveShape> LiveShapes { get; init; } = [];

    /// <summary>The vector mask is inverted (flag bit 0 of 'vmsk' / 'vsms').</summary>
    public bool Inverted { get; init; }

    /// <summary>The vector mask is not linked to the layer (flag bit 1).</summary>
    public bool Unlinked { get; init; }

    /// <summary>The vector mask is disabled (flag bit 2): the fill shows everywhere.</summary>
    public bool Disabled { get; init; }

    /// <summary>Format data the layer was read from (for PSD, the source record), so unmodelled settings survive.</summary>
    public object? SourceData { get; init; }

    /// <summary>A copy with every coordinate mapped (a move or transform of the layer).</summary>
    public ShapeLayerData Map(Func<PathPoint, PathPoint> f) => this with
    {
        Path = Path.Map(f),
        LiveShapes = LiveShapes.Select(s =>
        {
            var a = f(new(s.Left, s.Top));
            var b = f(new(s.Right, s.Bottom));
            return s.WithBox(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }).ToArray(),
    };

    public ShapeLayerData Offset(double dx, double dy) => Map(p => new PathPoint(p.X + dx, p.Y + dy));
}
