using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// Bevel &amp; Emboss: Structure (style, technique, depth, direction, size, soften), Shading (angle and altitude with
/// the global light, gloss contour, highlight and shadow modes, colors and opacities), and the Contour and Texture
/// sub-pages, which are rows of their own under it with their own checkboxes, as in Photoshop.
/// </summary>
public sealed partial class BevelEntry : EffectEntry, IGlobalLightEntry
{
    private bool _showingGlobal;
    private RgbColor _highlight, _shadow;

    public BevelEntry(LayerStyleViewModel session, BevelEffect? original) : base(session, LayerStylePage.BevelEmboss, original)
    {
        var b = original ?? new BevelEffect { Angle = (float)session.GlobalAngle, Altitude = (float)session.GlobalAltitude };
        LoadCommon(original, BlendMode.Normal, 1f, default);
        StyleIndex = Array.IndexOf(StyleOrder, b.Style);
        (TechniqueIndex, Depth, DirectionIndex, Size, Soften) = ((int)b.Technique, b.Depth * 100.0, b.Up ? 0 : 1, b.Size, b.Soften);
        (Angle, Altitude, UseGlobalLight) = (b.Angle, b.Altitude, b.UseGlobalLight);
        if (UseGlobalLight) (Angle, Altitude) = (session.GlobalAngle, session.GlobalAltitude);
        Gloss = new ContourSetting(b.GlossContour, b.GlossAntiAliased, Edited, "Gloss Contour:");
        (HighlightMode, _highlight, HighlightOpacity) = (b.HighlightMode, b.HighlightColor, b.HighlightOpacity * 100.0);
        (ShadowMode, _shadow, ShadowOpacity) = (b.ShadowMode, b.ShadowColor, b.ShadowOpacity * 100.0);
        ContourPage = new BevelContourEntry(session, this, b);
        TexturePage = new BevelTextureEntry(session, this, b);
    }

    /// <summary>Photoshop's order of the Style menu.</summary>
    private static readonly BevelStyle[] StyleOrder = [BevelStyle.OuterBevel, BevelStyle.InnerBevel, BevelStyle.Emboss, BevelStyle.PillowEmboss, BevelStyle.StrokeEmboss];

    public static IReadOnlyList<string> Styles { get; } = ["Outer Bevel", "Inner Bevel", "Emboss", "Pillow Emboss", "Stroke Emboss"];
    public static IReadOnlyList<string> Techniques { get; } = ["Smooth", "Chisel Hard", "Chisel Soft"];
    public static IReadOnlyList<string> Directions { get; } = ["Up", "Down"];

    public BevelContourEntry ContourPage { get; }
    public BevelTextureEntry TexturePage { get; }

    [ObservableProperty] public partial int StyleIndex { get; set; }
    [ObservableProperty] public partial int TechniqueIndex { get; set; }
    [ObservableProperty] public partial double Depth { get; set; }
    [ObservableProperty] public partial int DirectionIndex { get; set; }
    [ObservableProperty] public partial double Size { get; set; }
    [ObservableProperty] public partial double Soften { get; set; }
    [ObservableProperty] public partial double Angle { get; set; }
    [ObservableProperty] public partial double Altitude { get; set; }
    [ObservableProperty] public partial bool UseGlobalLight { get; set; }
    [ObservableProperty] public partial BlendMode HighlightMode { get; set; }
    [ObservableProperty] public partial double HighlightOpacity { get; set; }
    [ObservableProperty] public partial BlendMode ShadowMode { get; set; }
    [ObservableProperty] public partial double ShadowOpacity { get; set; }

    /// <summary>Gloss Contour and its Anti-aliased checkbox.</summary>
    public ContourSetting Gloss { get; }

    public Color HighlightColor
    {
        get => LayerStyleViewModel.ToColor(_highlight);
        set
        {
            if (value == HighlightColor) return;
            _highlight = LayerStyleViewModel.ToRgb(value);
            OnPropertyChanged();
            Edited();
        }
    }

    public Color ShadowColor
    {
        get => LayerStyleViewModel.ToColor(_shadow);
        set
        {
            if (value == ShadowColor) return;
            _shadow = LayerStyleViewModel.ToRgb(value);
            OnPropertyChanged();
            Edited();
        }
    }

    void IGlobalLightEntry.ShowGlobalLight(double angle, double altitude)
    {
        _showingGlobal = true;
        (Angle, Altitude) = (angle, altitude);
        _showingGlobal = false;
    }

    partial void OnAngleChanged(double value)
    {
        if (_showingGlobal || Loading) return;
        if (UseGlobalLight) Session.GlobalAngle = value;
        Edited();
    }

    partial void OnAltitudeChanged(double value)
    {
        if (_showingGlobal || Loading) return;
        if (UseGlobalLight) Session.GlobalAltitude = value;
        Edited();
    }

    partial void OnUseGlobalLightChanged(bool value)
    {
        if (value && !Loading) ((IGlobalLightEntry)this).ShowGlobalLight(Session.GlobalAngle, Session.GlobalAltitude);
        Edited();
    }

    partial void OnStyleIndexChanged(int value) => Edited();
    partial void OnTechniqueIndexChanged(int value) => Edited();
    partial void OnDepthChanged(double value) => Edited();
    partial void OnDirectionIndexChanged(int value) => Edited();
    partial void OnSizeChanged(double value) => Edited();
    partial void OnSoftenChanged(double value) => Edited();
    partial void OnHighlightModeChanged(BlendMode value) => Edited();
    partial void OnHighlightOpacityChanged(double value) => Edited();
    partial void OnShadowModeChanged(BlendMode value) => Edited();
    partial void OnShadowOpacityChanged(double value) => Edited();

    /// <summary>A sub-page changed: the bevel is edited (and turned on) like any of its own settings.</summary>
    internal void SubPageEdited() => Edited();

    protected override LayerEffect Build() => (Original as BevelEffect ?? new BevelEffect()) with
    {
        Style = StyleOrder[Math.Clamp(StyleIndex, 0, StyleOrder.Length - 1)], Technique = (BevelTechnique)TechniqueIndex,
        Depth = Fraction(Depth), Up = DirectionIndex == 0, Size = (float)Size, Soften = (float)Soften,
        Angle = (float)Angle, Altitude = (float)Altitude, UseGlobalLight = UseGlobalLight,
        GlossContour = Gloss.Contour, GlossAntiAliased = Gloss.AntiAliased,
        HighlightMode = HighlightMode, HighlightColor = _highlight, HighlightOpacity = Fraction(HighlightOpacity),
        ShadowMode = ShadowMode, ShadowColor = _shadow, ShadowOpacity = Fraction(ShadowOpacity),
        UseContour = ContourPage.IsChecked, Contour = ContourPage.Contour.Contour, ContourAntiAliased = ContourPage.Contour.AntiAliased,
        ContourRange = Fraction(ContourPage.Range),
        UseTexture = TexturePage.IsChecked, Texture = TexturePage.Pattern.Fill, TextureDepth = Fraction(TexturePage.Depth), TextureInvert = TexturePage.Invert,
    };
}

/// <summary>A sub-page of Bevel &amp; Emboss: listed indented under it; its checkbox turns the sub-page's part on.</summary>
public abstract partial class BevelSubEntry(LayerStyleViewModel session, BevelEntry bevel) : StyleEntry(session, LayerStylePage.BevelEmboss)
{
    public BevelEntry Bevel { get; } = bevel;
    public override double Indent => 18;

    protected override void CheckedChanged(bool value)
    {
        if (Loading) return;
        // Turning a sub-page on turns the bevel on, as in Photoshop.
        if (value && !Bevel.IsChecked) Bevel.IsChecked = true;
        Session.Changed();
    }

    /// <summary>A setting on the sub-page changed: turn it (and the bevel) on and show it.</summary>
    protected void SubEdited()
    {
        if (Loading) return;
        if (!IsChecked) IsChecked = true;
        Bevel.SubPageEdited();
    }
}

/// <summary>Bevel &amp; Emboss › Contour: reshapes the bevel's profile.</summary>
public sealed partial class BevelContourEntry : BevelSubEntry
{
    public BevelContourEntry(LayerStyleViewModel session, BevelEntry bevel, BevelEffect b) : base(session, bevel)
    {
        IsChecked = b.UseContour;
        Contour = new ContourSetting(b.Contour, b.ContourAntiAliased, SubEdited);
        Range = b.ContourRange * 100.0;
    }

    public override string Name => "Contour";
    public ContourSetting Contour { get; }
    [ObservableProperty] public partial double Range { get; set; }

    partial void OnRangeChanged(double value) => SubEdited();
}

/// <summary>Bevel &amp; Emboss › Texture: a pattern pressed into the surface.</summary>
public sealed partial class BevelTextureEntry : BevelSubEntry
{
    public BevelTextureEntry(LayerStyleViewModel session, BevelEntry bevel, BevelEffect b) : base(session, bevel)
    {
        IsChecked = b.UseTexture;
        Pattern = new PatternSetting(b.Texture, session.DocumentPatterns, SubEdited);
        (Depth, Invert) = (b.TextureDepth * 100.0, b.TextureInvert);
    }

    public override string Name => "Texture";
    public PatternSetting Pattern { get; }
    [ObservableProperty] public partial double Depth { get; set; }
    [ObservableProperty] public partial bool Invert { get; set; }

    partial void OnDepthChanged(double value) => SubEdited();
    partial void OnInvertChanged(bool value) => SubEdited();
}

/// <summary>Satin: interior shading from two offset, blurred copies of the shape.</summary>
public sealed partial class SatinEntry : EffectEntry
{
    public SatinEntry(LayerStyleViewModel session, SatinEffect? original) : base(session, LayerStylePage.Satin, original)
    {
        // Photoshop's defaults for a new satin.
        var s = original ?? new SatinEffect { BlendMode = BlendMode.Multiply, Opacity = 0.5f, Color = RgbColor.Black };
        LoadCommon(original, s.BlendMode, s.Opacity, s.Color);
        (Angle, Distance, Size, Invert) = (s.Angle, s.Distance, s.Size, s.Invert);
        Contour = new ContourSetting(s.Contour, s.AntiAliased, Edited);
    }

    [ObservableProperty] public partial double Angle { get; set; }
    [ObservableProperty] public partial double Distance { get; set; }
    [ObservableProperty] public partial double Size { get; set; }
    [ObservableProperty] public partial bool Invert { get; set; }
    public ContourSetting Contour { get; }

    partial void OnAngleChanged(double value) => Edited();
    partial void OnDistanceChanged(double value) => Edited();
    partial void OnSizeChanged(double value) => Edited();
    partial void OnInvertChanged(bool value) => Edited();

    protected override LayerEffect Build() => (Original as SatinEffect ?? new SatinEffect()) with
    {
        BlendMode = BlendMode, Opacity = Fraction(Opacity), Color = Rgb, Angle = (float)Angle, Distance = (float)Distance, Size = (float)Size,
        Contour = Contour.Contour, AntiAliased = Contour.AntiAliased, Invert = Invert,
    };
}
