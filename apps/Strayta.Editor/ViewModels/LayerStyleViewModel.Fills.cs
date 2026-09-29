using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// A contour choice shared by every page that has one (shadows, glows, satin, bevel gloss and the bevel's Contour
/// sub-page): Photoshop's presets by name, or a custom curve drawn in the curve editor (the Curves dialog's control),
/// plus Anti-aliased. A contour read from a file stays exactly as it was (corner points included) until it is edited.
/// </summary>
public sealed partial class ContourSetting : ObservableObject
{
    private readonly Action _changed;
    private int _presetIndex;
    private bool _antiAliased;

    public ContourSetting(Contour contour, bool antiAliased, Action changed, string label = "Contour:")
    {
        _changed = changed;
        Label = label;
        Contour = contour;
        _antiAliased = antiAliased;
        _presetIndex = IndexOf(contour);
    }

    /// <summary>"Contour:", or "Gloss Contour:" for a bevel's gloss.</summary>
    public string Label { get; }

    /// <summary>The preset names, then "Custom".</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Contour.Presets.Select(c => c.Name), "Custom"];

    private static int CustomIndex => Contour.Presets.Count;

    private static int IndexOf(Contour c)
    {
        for (int i = 0; i < Contour.Presets.Count; i++)
            if (Contour.Presets[i].Equals(c) || (c.Name == Contour.Presets[i].Name && c.Points.SequenceEqual(Contour.Presets[i].Points))) return i;
        return c.IsIdentity && c.Name == "Linear" ? 0 : CustomIndex;
    }

    public Contour Contour { get; private set; }

    /// <summary>The chosen preset; "Custom" keeps the current curve.</summary>
    public int PresetIndex
    {
        get => _presetIndex;
        set
        {
            if (value < 0 || value == _presetIndex) return;
            _presetIndex = value;
            if (value < CustomIndex) Contour = Contour.Presets[value];
            OnPropertyChanged();
            OnPropertyChanged(nameof(Points));
            _changed();
        }
    }

    /// <summary>The curve as the curve editor shows and edits it (0..255 points).</summary>
    public IReadOnlyList<CurvePoint> Points
    {
        get => Contour.Points.Select(p => new CurvePoint((int)MathF.Round(p.X), (int)MathF.Round(p.Y))).ToList();
        set
        {
            if (value is not { Count: >= 2 } || value.SequenceEqual(Points)) return;
            // Corner points stay corners where the editor kept them.
            var corners = Contour.Points.Where(p => p.Corner).Select(p => (int)MathF.Round(p.X)).ToHashSet();
            Contour = new Contour(value.Select(p => new ContourPoint(p.Input, p.Output, corners.Contains(p.Input))).ToList()) { Name = "Custom" };
            _presetIndex = CustomIndex;
            OnPropertyChanged(nameof(PresetIndex));
            OnPropertyChanged();
            _changed();
        }
    }

    public bool AntiAliased
    {
        get => _antiAliased;
        set
        {
            if (value == _antiAliased) return;
            _antiAliased = value;
            OnPropertyChanged();
            _changed();
        }
    }
}

/// <summary>
/// A pattern choice with its layout (Pattern Overlay, pattern strokes, the bevel's Texture): the pattern (built-in,
/// the person's own or the document's, from the pattern picker), Scale, Link with Layer and Snap to Origin. A pattern
/// the file names but does not contain is kept as it was until another one is chosen.
/// </summary>
public sealed partial class PatternSetting : ObservableObject
{
    private readonly Action _changed;
    private PatternReference? _reference;
    private bool _loading = true;

    public PatternSetting(PatternFill? fill, IEnumerable<Pattern> documentPatterns, Action changed)
    {
        _changed = changed;
        DocumentPatterns = documentPatterns;
        _reference = fill?.Pattern;
        Pattern = fill?.Pattern.Resolved ?? (fill is null ? documentPatterns.FirstOrDefault() ?? PatternLibrary.BuiltIn[0] : null);
        _reference ??= Pattern is null ? null : PatternReference.To(Pattern);
        Scale = (fill?.Scale ?? 1f) * 100.0;
        Link = fill?.LinkWithLayer ?? true;
        (PhaseX, PhaseY) = (fill?.PhaseX ?? 0, fill?.PhaseY ?? 0);
        _loading = false;
    }

    /// <summary>The document's patterns, which the picker lists after the built-in and the person's own.</summary>
    public IEnumerable<Pattern> DocumentPatterns { get; }

    /// <summary>The chosen pattern (null while it is one the file lacks).</summary>
    [ObservableProperty] public partial Pattern? Pattern { get; set; }
    [ObservableProperty] public partial double Scale { get; set; }
    [ObservableProperty] public partial bool Link { get; set; }
    public float PhaseX { get; private set; }
    public float PhaseY { get; private set; }

    /// <summary>What the page says about a pattern the file names but does not contain.</summary>
    public string? Missing => Pattern is null && _reference is { } r ? $"\"{r.Name}\" is not in this document, so it is not drawn." : null;

    partial void OnPatternChanged(Pattern? value)
    {
        if (value is not null) _reference = PatternReference.To(value);
        OnPropertyChanged(nameof(Missing));
        if (!_loading) _changed();
    }

    partial void OnScaleChanged(double value) { if (!_loading) _changed(); }
    partial void OnLinkChanged(bool value) { if (!_loading) _changed(); }

    /// <summary>Photoshop's Snap to Origin: the tiles start at the layer's (or canvas's) corner again.</summary>
    [RelayCommand]
    private void SnapToOrigin()
    {
        if (PhaseX == 0 && PhaseY == 0) return;
        (PhaseX, PhaseY) = (0, 0);
        _changed();
    }

    public PatternFill? Fill => _reference is null ? null : new PatternFill(_reference)
    {
        Scale = (float)(Scale / 100), LinkWithLayer = Link, PhaseX = PhaseX, PhaseY = PhaseY,
    };
}

/// <summary>Stroke: size, position, blend mode, opacity and a color, gradient or pattern fill.</summary>
public sealed partial class StrokeEntry : EffectEntry
{
    public StrokeEntry(LayerStyleViewModel session, StrokeEffect? original) : base(session, LayerStylePage.Stroke, original)
    {
        LoadCommon(original, BlendMode.Normal, 1f, original?.Color ?? new RgbColor(1, 0, 0));
        (Size, PositionIndex, FillTypeIndex) = original is null ? (3, 0, 0) : (original.Size, (int)original.Position, (int)original.FillType);
        var g = original?.GradientFill;
        (Gradient, GradientStyleIndex, GradientAngle, GradientScale, GradientReverse, GradientAlign) = g is null
            ? (Editing.GradientPresets.BlackToWhite, 0, 90.0, 100.0, false, true)
            : (g.Gradient, (int)g.Style, g.Angle, g.Scale * 100.0, g.Reverse, g.AlignWithLayer);
        _gradientOffset = (g?.OffsetX ?? 0, g?.OffsetY ?? 0);
        Pattern = new PatternSetting(original?.PatternFill, session.DocumentPatterns, Edited);
    }

    private readonly (float X, float Y) _gradientOffset;

    [ObservableProperty] public partial double Size { get; set; }

    /// <summary>0 Outside, 1 Inside, 2 Center (the order of <see cref="StrokePosition"/>).</summary>
    [ObservableProperty] public partial int PositionIndex { get; set; }

    /// <summary>0 Color, 1 Gradient, 2 Pattern (the order of <see cref="StrokeFillType"/>).</summary>
    [ObservableProperty] public partial int FillTypeIndex { get; set; }

    public bool IsColorFill => FillTypeIndex == 0;
    public bool IsGradientFill => FillTypeIndex == 1;
    public bool IsPatternFill => FillTypeIndex == 2;

    [ObservableProperty] public partial Gradient Gradient { get; set; }
    /// <summary>Linear … Diamond, then Shape Burst (strokes only).</summary>
    [ObservableProperty] public partial int GradientStyleIndex { get; set; }
    [ObservableProperty] public partial double GradientAngle { get; set; }
    [ObservableProperty] public partial double GradientScale { get; set; }
    [ObservableProperty] public partial bool GradientReverse { get; set; }
    [ObservableProperty] public partial bool GradientAlign { get; set; }

    public PatternSetting Pattern { get; }

    partial void OnSizeChanged(double value) => Edited();
    partial void OnPositionIndexChanged(int value) => Edited();

    partial void OnFillTypeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsColorFill));
        OnPropertyChanged(nameof(IsGradientFill));
        OnPropertyChanged(nameof(IsPatternFill));
        Edited();
    }

    partial void OnGradientChanged(Gradient value) => Edited();
    partial void OnGradientStyleIndexChanged(int value) => Edited();
    partial void OnGradientAngleChanged(double value) => Edited();
    partial void OnGradientScaleChanged(double value) => Edited();
    partial void OnGradientReverseChanged(bool value) => Edited();
    partial void OnGradientAlignChanged(bool value) => Edited();

    protected override LayerEffect Build()
    {
        var type = (StrokeFillType)FillTypeIndex;
        // Only the chosen fill type's settings are kept, as Photoshop stores them.
        return (Original as StrokeEffect ?? new StrokeEffect()) with
        {
            BlendMode = BlendMode, Opacity = Fraction(Opacity), Color = Rgb, Size = (float)Size, Position = (StrokePosition)PositionIndex,
            FillType = type,
            GradientFill = type != StrokeFillType.Gradient ? null : new GradientFill(Resolved(Gradient))
            {
                Style = (GradientStyle)GradientStyleIndex, Angle = (float)GradientAngle, Scale = Fraction(GradientScale),
                Reverse = GradientReverse, AlignWithLayer = GradientAlign, OffsetX = _gradientOffset.X, OffsetY = _gradientOffset.Y,
            },
            PatternFill = type != StrokeFillType.Pattern ? null : Pattern.Fill,
        };
    }
}

/// <summary>Pattern Overlay: blend mode, opacity and the pattern with its scale, link and origin.</summary>
public sealed partial class PatternOverlayEntry : EffectEntry
{
    public PatternOverlayEntry(LayerStyleViewModel session, PatternOverlayEffect? original) : base(session, LayerStylePage.PatternOverlay, original)
    {
        LoadCommon(original, BlendMode.Normal, 1f, default);
        Pattern = new PatternSetting(original?.Fill, session.DocumentPatterns, Edited);
    }

    public PatternSetting Pattern { get; }

    protected override LayerEffect Build() => (Original as PatternOverlayEffect ?? new PatternOverlayEffect()) with
    {
        BlendMode = BlendMode, Opacity = Fraction(Opacity), Fill = Pattern.Fill,
    };
}
