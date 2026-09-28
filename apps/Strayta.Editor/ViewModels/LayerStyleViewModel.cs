using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Editor.Editing;
using DropShadowEffect = Strayta.Core.DropShadowEffect;

namespace Strayta.Editor.ViewModels;

/// <summary>The pages of the Layer Style dialog, in Photoshop's list order (top of the stack first).</summary>
public enum LayerStylePage
{
    BlendingOptions,
    BevelEmboss,
    Stroke,
    InnerShadow,
    InnerGlow,
    Satin,
    ColorOverlay,
    GradientOverlay,
    PatternOverlay,
    OuterGlow,
    DropShadow,
}

/// <summary>
/// One Layer Style dialog session on one layer. Every change is shown on the canvas right away by writing the
/// settings into the layer (and, when the global light moves, into every layer whose shadows follow it) without
/// recording anything; <see cref="Commit"/> puts the original state back and applies the result as one "Layer Style"
/// undo step, <see cref="Cancel"/> just puts it back. The document renders a screen-resolution preview after each
/// change and full resolution once changes pause, so dragging a slider stays interactive on large layers.
/// </summary>
public sealed partial class LayerStyleViewModel : ObservableObject
{
    private readonly LayerStyleState _original;
    private readonly float _originalAngle;
    private readonly Dictionary<LayerNode, LayerEffects> _globalLightLayers = new(ReferenceEqualityComparer.Instance);
    private bool _loading = true, _closed;

    public LayerStyleViewModel(DocumentViewModel document, LayerNode layer, LayerStylePage page)
    {
        Document = document;
        Layer = layer;
        _original = LayerStyleState.Of(layer);
        _originalAngle = document.Model.GlobalLightAngle;
        GlobalAngle = _originalAngle;
        foreach (var node in document.Model.Root.Descendants())
            if (!ReferenceEquals(node, layer) && node.Effects is { } fx && fx.Items.Any(UsesGlobalLight))
                _globalLightLayers[node] = fx;

        Blending = new BlendingEntry(this, layer);
        Entries.Add(Blending);
        BuildEntries(layer.Effects);
        foreach (var entry in Entries) entry.Loaded();
        SelectedEntry = Entries.FirstOrDefault(e => e.Page == page && e.IsEditable) ?? Blending;
        if (SelectedEntry is EffectEntry { IsChecked: false } chosen) chosen.IsChecked = true; // choosing a style from the menu turns it on
        _loading = false;
        Changed();
    }

    public DocumentViewModel Document { get; }
    public LayerNode Layer { get; }
    public string Title => $"Layer Style — {Layer.Name}";

    /// <summary>The left column: Blending Options, then one row per effect (several for repeated effects).</summary>
    public ObservableCollection<StyleEntry> Entries { get; } = [];

    public BlendingEntry Blending { get; }

    [ObservableProperty] public partial StyleEntry? SelectedEntry { get; set; }

    /// <summary>Photoshop's Preview checkbox: off shows the layer as it was while the dialog stays open.</summary>
    [ObservableProperty] public partial bool Preview { get; set; } = true;

    /// <summary>The document's global light while the dialog is open; shadows with "Use Global Light" follow it.</summary>
    [ObservableProperty] public partial double GlobalAngle { get; set; }

    partial void OnPreviewChanged(bool value) => Changed();

    /// <summary>Selecting a style by its name turns it on, as in Photoshop (the checkbox alone does not select).</summary>
    partial void OnSelectedEntryChanged(StyleEntry? value)
    {
        if (!_loading && value is EffectEntry { IsEditable: true, IsChecked: false } e) e.IsChecked = true;
    }

    partial void OnGlobalAngleChanged(double value)
    {
        foreach (var shadow in Entries.OfType<ShadowEntry>().Where(s => s.UseGlobalLight)) shadow.ShowGlobalAngle(value);
        Changed();
    }

    private static bool UsesGlobalLight(LayerEffect e) => e is DropShadowEffect { UseGlobalLight: true } or InnerShadowEffect { UseGlobalLight: true };

    // ---- Entries ------------------------------------------------------------------------------------

    private void BuildEntries(LayerEffects? effects)
    {
        var items = effects?.Items ?? [];
        foreach (var page in Enum.GetValues<LayerStylePage>().Skip(1))
        {
            var existing = items.Where(e => PageOf(e) == page).ToList();
            foreach (var effect in existing) Entries.Add(EntryFor(page, effect));
            if (existing.Count == 0) Entries.Add(EntryFor(page, null));
        }

        StyleEntry EntryFor(LayerStylePage page, LayerEffect? effect) => (page, effect) switch
        {
            (_, UnsupportedEffect u) => new UnsupportedEntry(this, page, u),
            (LayerStylePage.DropShadow or LayerStylePage.InnerShadow, _) => new ShadowEntry(this, page, effect),
            (LayerStylePage.OuterGlow or LayerStylePage.InnerGlow, _) => new GlowEntry(this, page, effect),
            (LayerStylePage.ColorOverlay, _) => new ColorOverlayEntry(this, effect as ColorOverlayEffect),
            (LayerStylePage.GradientOverlay, _) => new GradientOverlayEntry(this, effect as GradientOverlayEffect),
            (LayerStylePage.Stroke, _) => new StrokeEntry(this, effect as StrokeEffect),
            _ => new UnavailableEntry(this, page),
        };
    }

    /// <summary>Which page shows an effect; unsupported ones by the kind they were read as.</summary>
    public static LayerStylePage PageOf(LayerEffect effect) => effect switch
    {
        DropShadowEffect => LayerStylePage.DropShadow,
        InnerShadowEffect => LayerStylePage.InnerShadow,
        OuterGlowEffect => LayerStylePage.OuterGlow,
        InnerGlowEffect => LayerStylePage.InnerGlow,
        ColorOverlayEffect => LayerStylePage.ColorOverlay,
        GradientOverlayEffect => LayerStylePage.GradientOverlay,
        StrokeEffect => LayerStylePage.Stroke,
        _ => Psd.PsdEffectsWriter.TypeOf(effect) switch
        {
            "DrSh" => LayerStylePage.DropShadow,
            "IrSh" => LayerStylePage.InnerShadow,
            "OrGl" => LayerStylePage.OuterGlow,
            "IrGl" => LayerStylePage.InnerGlow,
            "SoFi" => LayerStylePage.ColorOverlay,
            "GrFl" => LayerStylePage.GradientOverlay,
            "FrFX" => LayerStylePage.Stroke,
            "ChFX" => LayerStylePage.Satin,
            "patternFill" => LayerStylePage.PatternOverlay,
            _ => LayerStylePage.BevelEmboss,
        },
    };

    public static string NameOf(LayerStylePage page) => page switch
    {
        LayerStylePage.BlendingOptions => "Blending Options",
        LayerStylePage.BevelEmboss => "Bevel & Emboss",
        LayerStylePage.Stroke => "Stroke",
        LayerStylePage.InnerShadow => "Inner Shadow",
        LayerStylePage.InnerGlow => "Inner Glow",
        LayerStylePage.Satin => "Satin",
        LayerStylePage.ColorOverlay => "Color Overlay",
        LayerStylePage.GradientOverlay => "Gradient Overlay",
        LayerStylePage.PatternOverlay => "Pattern Overlay",
        LayerStylePage.OuterGlow => "Outer Glow",
        _ => "Drop Shadow",
    };

    // ---- The result ----------------------------------------------------------------------------------

    /// <summary>The layer's style as the dialog now describes it.</summary>
    public LayerStyleState Current()
    {
        // Model order is the stack from the bottom up, like the renderer draws it.
        var items = Entries.OfType<EffectEntry>().Reverse().Select(e => e.Result()).OfType<LayerEffect>().ToList();
        // Turning an effect on also turns on the layer's "Effects" switch if it was off.
        bool anyOn = Entries.OfType<EffectEntry>().Any(e => e.IsChecked && e.IsEditable);
        LayerEffects? effects = items.Count == 0 ? null
            : _original.Effects is { } fx ? fx with { Items = items, Enabled = fx.Enabled || anyOn }
            : new LayerEffects(items);
        if (effects is not null && _original.Effects is { } before && before.Equals(effects)) effects = before;
        return new LayerStyleState(effects, (float)(Blending.Opacity / 100), (float)(Blending.Fill / 100), Blending.BlendMode);
    }

    /// <summary>Other layers' effects with the dialog's global light.</summary>
    private IEnumerable<(LayerNode Node, LayerEffects Before, LayerEffects? After)> GlobalLightChanges() =>
        _globalLightLayers.Select(kv => (kv.Key, kv.Value, LayerStyleEdit.WithGlobalAngle(kv.Value, (float)GlobalAngle)));

    /// <summary>Shows the current settings on the canvas (or the original ones with Preview off).</summary>
    internal void Changed()
    {
        if (_loading || _closed) return;
        if (Preview)
        {
            Current().ApplyTo(Layer);
            foreach (var (node, _, after) in GlobalLightChanges()) node.Effects = after;
            Document.Model.GlobalLightAngle = (float)GlobalAngle;
        }
        else Restore();
        Document.RequestRender();
    }

    private void Restore()
    {
        _original.ApplyTo(Layer);
        foreach (var (node, effects) in _globalLightLayers) node.Effects = effects;
        Document.Model.GlobalLightAngle = _originalAngle;
    }

    /// <summary>OK: one "Layer Style" undo step, or nothing if the style is unchanged.</summary>
    public void Commit()
    {
        if (_closed) return;
        var result = Current();
        var others = GlobalLightChanges().Where(c => !ReferenceEquals(c.Before, c.After)).ToList();
        _closed = true;
        Restore();

        var changes = new List<(LayerNode, LayerStyleState, LayerStyleState)>();
        if (result != _original) changes.Add((Layer, _original, result)); // value equality: effects compare by their settings
        foreach (var (node, before, after) in others)
            changes.Add((node, LayerStyleState.Of(node), LayerStyleState.Of(node) with { Effects = after }));
        if (changes.Count > 0 || (float)GlobalAngle != _originalAngle)
            Document.Apply(new LayerStyleEdit("Layer Style", Document.Model, changes, (float)GlobalAngle));
        else
            Document.RequestRender();
    }

    /// <summary>Cancel: the layer and the global light exactly as they were.</summary>
    public void Cancel()
    {
        if (_closed) return;
        _closed = true;
        Restore();
        Document.RequestRender();
    }

    // ---- Shared choices ------------------------------------------------------------------------------

    /// <summary>Blend modes an effect can use (every layer mode except Pass Through).</summary>
    public static IReadOnlyList<BlendMode> EffectBlendModes { get; } = Enum.GetValues<BlendMode>().Where(m => m != BlendMode.PassThrough).ToArray();

    public static IReadOnlyList<string> StrokePositions { get; } = ["Outside", "Inside", "Center"];
    public static IReadOnlyList<string> GradientStyles { get; } = ["Linear", "Radial", "Angle", "Reflected", "Diamond"];
    public static IReadOnlyList<string> GlowSources { get; } = ["Edge", "Center"];

    internal static Color ToColor(RgbColor c) => Color.FromRgb(Byte(c.R), Byte(c.G), Byte(c.B));
    internal static RgbColor ToRgb(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);
    private static byte Byte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
}

// ---- Rows of the dialog's list ----------------------------------------------------------------------------

/// <summary>A row in the dialog's list and the page it shows.</summary>
public abstract partial class StyleEntry(LayerStyleViewModel session, LayerStylePage page) : ObservableObject
{
    protected LayerStyleViewModel Session { get; } = session;
    public LayerStylePage Page { get; } = page;
    public virtual string Name => LayerStyleViewModel.NameOf(Page);

    /// <summary>False for effects Strayta cannot edit yet: their row is shown, greyed.</summary>
    public virtual bool IsEditable => true;
    public virtual bool HasCheckBox => true;

    /// <summary>The checkbox can be used (styles Strayta does not have yet cannot be turned on).</summary>
    public virtual bool CanToggle => true;

    /// <summary>Greyed text for rows that cannot be edited.</summary>
    public double NameOpacity => IsEditable ? 1 : 0.55;

    /// <summary>The row's checkbox: the effect is on the layer and visible.</summary>
    [ObservableProperty] public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => CheckedChanged(value);

    protected virtual void CheckedChanged(bool value)
    {
    }

    /// <summary>True while the constructors fill in the settings, which must not count as edits.</summary>
    protected bool Loading { get; private set; } = true;

    internal void Loaded() => Loading = false;

    /// <summary>Settings changed: turn the effect on (as Photoshop does) and show it.</summary>
    protected void Edited()
    {
        if (Loading) return;
        if (this is EffectEntry { IsChecked: false, IsEditable: true } e) e.IsChecked = true;
        Session.Changed();
    }
}

/// <summary>Blending Options: the layer's own opacity, fill opacity and blend mode.</summary>
public sealed partial class BlendingEntry : StyleEntry
{
    public BlendingEntry(LayerStyleViewModel session, LayerNode layer) : base(session, LayerStylePage.BlendingOptions)
    {
        Opacity = layer.Opacity * 100.0;
        Fill = layer.FillOpacity * 100.0;
        BlendMode = layer.BlendMode;
    }

    public override bool HasCheckBox => false;

    [ObservableProperty] public partial double Opacity { get; set; }
    [ObservableProperty] public partial double Fill { get; set; }
    [ObservableProperty] public partial BlendMode BlendMode { get; set; }

    partial void OnOpacityChanged(double value) => Edited();
    partial void OnFillChanged(double value) => Edited();
    partial void OnBlendModeChanged(BlendMode value) => Edited();
}

/// <summary>A row for one effect: its checkbox and its settings.</summary>
public abstract partial class EffectEntry : StyleEntry
{
    private readonly bool _wasHidden;
    private bool _toggled;

    protected EffectEntry(LayerStyleViewModel session, LayerStylePage page, LayerEffect? original) : base(session, page)
    {
        Original = original;
        _wasHidden = original is { Enabled: false } || session.Layer.Effects is { Enabled: false };
        IsChecked = original is not null && !_wasHidden;
    }

    /// <summary>The effect as it was on the layer (null for a new one); edits start from it and keep its file data.</summary>
    public LayerEffect? Original { get; }

    /// <summary>The person used the checkbox (or turned the effect on by editing it).</summary>
    protected bool Toggled => _toggled;

    protected override void CheckedChanged(bool value)
    {
        if (Loading) return;
        _toggled = true;
        Session.Changed();
    }

    /// <summary>The effect for the layer: on when checked; an effect whose eye was closed stays, hidden, until its checkbox is used; otherwise none.</summary>
    internal virtual LayerEffect? Result()
    {
        if (IsChecked) return Build() with { Enabled = true };
        if (Original is not null && _wasHidden && !_toggled) return Build() with { Enabled = Original.Enabled };
        return null;
    }

    /// <summary>The page's settings as an effect.</summary>
    protected abstract LayerEffect Build();

    // Shared settings.
    [ObservableProperty] public partial BlendMode BlendMode { get; set; }
    [ObservableProperty] public partial double Opacity { get; set; }

    partial void OnBlendModeChanged(BlendMode value) => Edited();
    partial void OnOpacityChanged(double value) => Edited();

    /// <summary>
    /// The effect color as the picker shows it. The exact color read from a file (which may fall between 8-bit
    /// values) is kept until the person picks another one, so opening and closing the dialog changes nothing.
    /// </summary>
    public Color Color
    {
        get => LayerStyleViewModel.ToColor(Rgb);
        set
        {
            if (value == Color) return;
            Rgb = LayerStyleViewModel.ToRgb(value);
            OnPropertyChanged();
            Edited();
        }
    }

    protected RgbColor Rgb { get; set; }

    protected void LoadCommon(LayerEffect? effect, BlendMode mode, float opacity, RgbColor color)
    {
        BlendMode = effect?.BlendMode ?? mode;
        Opacity = (effect?.Opacity ?? opacity) * 100.0;
        Rgb = color;
    }

    protected float Fraction(double percent) => (float)(percent / 100);
}

/// <summary>Drop Shadow and Inner Shadow.</summary>
public sealed partial class ShadowEntry : EffectEntry
{
    private bool _showingGlobal;

    public ShadowEntry(LayerStyleViewModel session, LayerStylePage page, LayerEffect? original) : base(session, page, original)
    {
        switch (original)
        {
            case DropShadowEffect d:
                LoadCommon(d, d.BlendMode, d.Opacity, d.Color);
                (Angle, UseGlobalLight, Distance, Spread, Size, Knockout) = (d.Angle, d.UseGlobalLight, d.Distance, d.Spread * 100.0, d.Size, d.Knockout);
                break;
            case InnerShadowEffect s:
                LoadCommon(s, s.BlendMode, s.Opacity, s.Color);
                (Angle, UseGlobalLight, Distance, Spread, Size) = (s.Angle, s.UseGlobalLight, s.Distance, s.Choke * 100.0, s.Size);
                break;
            default:
                // Photoshop's defaults for a new shadow.
                LoadCommon(null, BlendMode.Multiply, 0.35f, RgbColor.Black);
                (Angle, UseGlobalLight, Distance, Spread, Size, Knockout) = (session.GlobalAngle, true, 5, 0, 5, true);
                break;
        }
        if (UseGlobalLight) Angle = session.GlobalAngle;
    }

    public bool IsDropShadow => Page == LayerStylePage.DropShadow;
    public string SpreadLabel => IsDropShadow ? "Spread (%)" : "Choke (%)";

    [ObservableProperty] public partial double Angle { get; set; }
    [ObservableProperty] public partial bool UseGlobalLight { get; set; }
    [ObservableProperty] public partial double Distance { get; set; }
    [ObservableProperty] public partial double Spread { get; set; }
    [ObservableProperty] public partial double Size { get; set; }
    [ObservableProperty] public partial bool Knockout { get; set; }

    /// <summary>The global light moved (from this or another shadow): show its angle without counting it as an edit here.</summary>
    internal void ShowGlobalAngle(double angle)
    {
        _showingGlobal = true;
        Angle = angle;
        _showingGlobal = false;
    }

    partial void OnAngleChanged(double value)
    {
        if (_showingGlobal || Loading) return;
        if (UseGlobalLight) Session.GlobalAngle = value; // moves every shadow that uses it, here and on other layers
        Edited();
    }

    partial void OnUseGlobalLightChanged(bool value)
    {
        if (value && !Loading) ShowGlobalAngle(Session.GlobalAngle);
        Edited();
    }

    partial void OnDistanceChanged(double value) => Edited();
    partial void OnSpreadChanged(double value) => Edited();
    partial void OnSizeChanged(double value) => Edited();
    partial void OnKnockoutChanged(bool value) => Edited();

    protected override LayerEffect Build() => IsDropShadow
        ? (Original as DropShadowEffect ?? new DropShadowEffect()) with
        {
            BlendMode = BlendMode, Opacity = Fraction(Opacity), Color = Rgb, Angle = (float)Angle, UseGlobalLight = UseGlobalLight,
            Distance = (float)Distance, Spread = Fraction(Spread), Size = (float)Size, Knockout = Knockout,
        }
        : (Original as InnerShadowEffect ?? new InnerShadowEffect()) with
        {
            BlendMode = BlendMode, Opacity = Fraction(Opacity), Color = Rgb, Angle = (float)Angle, UseGlobalLight = UseGlobalLight,
            Distance = (float)Distance, Choke = Fraction(Spread), Size = (float)Size,
        };
}

/// <summary>Outer Glow and Inner Glow (solid color; gradient glows are kept as they are but not edited).</summary>
public sealed partial class GlowEntry : EffectEntry
{
    public GlowEntry(LayerStyleViewModel session, LayerStylePage page, LayerEffect? original) : base(session, page, original)
    {
        switch (original)
        {
            case OuterGlowEffect g:
                LoadCommon(g, g.BlendMode, g.Opacity, g.Color);
                (Spread, Size) = (g.Spread * 100.0, g.Size);
                break;
            case InnerGlowEffect g:
                LoadCommon(g, g.BlendMode, g.Opacity, g.Color);
                (Spread, Size, SourceIndex) = (g.Choke * 100.0, g.Size, g.FromCenter ? 1 : 0);
                break;
            default:
                LoadCommon(null, BlendMode.Screen, 0.75f, new RgbColor(1, 1, 190 / 255f));
                (Spread, Size) = (0, 5);
                break;
        }
    }

    public bool IsInnerGlow => Page == LayerStylePage.InnerGlow;
    public string SpreadLabel => IsInnerGlow ? "Choke (%)" : "Spread (%)";

    [ObservableProperty] public partial double Spread { get; set; }
    [ObservableProperty] public partial double Size { get; set; }

    /// <summary>Inner Glow's Source: 0 Edge, 1 Center.</summary>
    [ObservableProperty] public partial int SourceIndex { get; set; }

    partial void OnSpreadChanged(double value) => Edited();
    partial void OnSizeChanged(double value) => Edited();
    partial void OnSourceIndexChanged(int value) => Edited();

    protected override LayerEffect Build() => IsInnerGlow
        ? (Original as InnerGlowEffect ?? new InnerGlowEffect()) with
        {
            BlendMode = BlendMode, Opacity = Fraction(Opacity), Color = Rgb, Choke = Fraction(Spread), Size = (float)Size, FromCenter = SourceIndex == 1,
        }
        : (Original as OuterGlowEffect ?? new OuterGlowEffect()) with
        {
            BlendMode = BlendMode, Opacity = Fraction(Opacity), Color = Rgb, Spread = Fraction(Spread), Size = (float)Size,
        };
}

public sealed partial class ColorOverlayEntry : EffectEntry
{
    public ColorOverlayEntry(LayerStyleViewModel session, ColorOverlayEffect? original)
        : base(session, LayerStylePage.ColorOverlay, original) =>
        LoadCommon(original, BlendMode.Normal, 1f, original?.Color ?? new RgbColor(1, 0, 0));

    protected override LayerEffect Build() => (Original as ColorOverlayEffect ?? new ColorOverlayEffect()) with
    {
        BlendMode = BlendMode, Opacity = Fraction(Opacity), Color = Rgb,
    };
}

public sealed partial class GradientOverlayEntry : EffectEntry
{
    private readonly List<Gradient> _gradients = [];

    public GradientOverlayEntry(LayerStyleViewModel session, GradientOverlayEffect? original) : base(session, LayerStylePage.GradientOverlay, original)
    {
        LoadCommon(original, BlendMode.Normal, 1f, default);
        var fg = LayerStyleViewModel.ToRgb(session.Document.Editor.ForegroundColor);
        var bg = LayerStyleViewModel.ToRgb(session.Document.Editor.BackgroundColor);
        var names = new List<string>();
        void Add(string name, Gradient g)
        {
            names.Add(name);
            _gradients.Add(g);
        }
        if (original is not null) Add($"Current ({DisplayName(original.Gradient.Name)})", original.Gradient);
        Add("Foreground to Background", Two(fg, 1, bg, 1, "Foreground to Background"));
        Add("Foreground to Transparent", Two(fg, 1, fg, 0, "Foreground to Transparent"));
        Add("Black, White", Two(RgbColor.Black, 1, new RgbColor(1, 1, 1), 1, "Black, White"));
        Add("White, Black", Two(new RgbColor(1, 1, 1), 1, RgbColor.Black, 1, "White, Black"));
        GradientNames = names;

        if (original is not null)
            (StyleIndex, Angle, Scale, Reverse, Align) = ((int)original.Style, original.Angle, original.Scale * 100.0, original.Reverse, original.AlignWithLayer);
        else
            (GradientIndex, Angle, Scale, Align) = (2, 90, 100, true);
    }

    private static Gradient Two(RgbColor a, float aOpacity, RgbColor b, float bOpacity, string name) =>
        new([new(0, 0.5f, a), new(1, 0.5f, b)], [new(0, 0.5f, aOpacity), new(1, 0.5f, bOpacity)]) { Name = name };

    /// <summary>Photoshop stores preset names as "$$$/Key=Display Name".</summary>
    private static string DisplayName(string name) => name.Contains('=') ? name[(name.IndexOf('=') + 1)..] : name;

    public IReadOnlyList<string> GradientNames { get; }

    [ObservableProperty] public partial int GradientIndex { get; set; }
    [ObservableProperty] public partial int StyleIndex { get; set; }
    [ObservableProperty] public partial double Angle { get; set; }
    [ObservableProperty] public partial double Scale { get; set; }
    [ObservableProperty] public partial bool Reverse { get; set; }
    [ObservableProperty] public partial bool Align { get; set; }

    /// <summary>A preview of the chosen gradient for the swatch.</summary>
    public IBrush GradientBrush
    {
        get
        {
            var g = _gradients[Math.Clamp(GradientIndex, 0, _gradients.Count - 1)];
            var brush = new LinearGradientBrush { StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative), EndPoint = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative) };
            for (int i = 0; i <= 16; i++)
            {
                float t = i / 16f;
                var (c, a) = g.Sample(Reverse ? 1 - t : t);
                brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(a * 255), (byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255)), t));
            }
            return brush;
        }
    }

    partial void OnGradientIndexChanged(int value) { OnPropertyChanged(nameof(GradientBrush)); Edited(); }
    partial void OnStyleIndexChanged(int value) => Edited();
    partial void OnAngleChanged(double value) => Edited();
    partial void OnScaleChanged(double value) => Edited();
    partial void OnReverseChanged(bool value) { OnPropertyChanged(nameof(GradientBrush)); Edited(); }
    partial void OnAlignChanged(bool value) => Edited();

    protected override LayerEffect Build()
    {
        var gradient = _gradients[Math.Clamp(GradientIndex, 0, _gradients.Count - 1)];
        return Original is GradientOverlayEffect o
            ? o with
            {
                BlendMode = BlendMode, Opacity = Fraction(Opacity), Gradient = gradient, Style = (GradientStyle)StyleIndex,
                Angle = (float)Angle, Scale = Fraction(Scale), Reverse = Reverse, AlignWithLayer = Align,
            }
            : new GradientOverlayEffect
            {
                BlendMode = BlendMode, Opacity = Fraction(Opacity), Gradient = gradient, Style = (GradientStyle)StyleIndex,
                Angle = (float)Angle, Scale = Fraction(Scale), Reverse = Reverse, AlignWithLayer = Align,
            };
    }
}

public sealed partial class StrokeEntry : EffectEntry
{
    public StrokeEntry(LayerStyleViewModel session, StrokeEffect? original) : base(session, LayerStylePage.Stroke, original)
    {
        LoadCommon(original, BlendMode.Normal, 1f, original?.Color ?? new RgbColor(1, 0, 0));
        (Size, PositionIndex) = original is null ? (3, 0) : (original.Size, (int)original.Position);
    }

    [ObservableProperty] public partial double Size { get; set; }

    /// <summary>0 Outside, 1 Inside, 2 Center (the order of <see cref="StrokePosition"/>).</summary>
    [ObservableProperty] public partial int PositionIndex { get; set; }

    partial void OnSizeChanged(double value) => Edited();
    partial void OnPositionIndexChanged(int value) => Edited();

    protected override LayerEffect Build() => (Original as StrokeEffect ?? new StrokeEffect()) with
    {
        BlendMode = BlendMode, Opacity = Fraction(Opacity), Color = Rgb, Size = (float)Size, Position = (StrokePosition)PositionIndex,
    };
}

/// <summary>An effect on the layer that Strayta cannot edit yet: it stays as it is, and its checkbox shows or hides it.</summary>
public sealed partial class UnsupportedEntry(LayerStyleViewModel session, LayerStylePage page, UnsupportedEffect effect)
    : EffectEntry(session, page, effect)
{
    public override string Name => effect.Name;
    public override bool IsEditable => false;
    public string Message => $"{effect.Name} is kept exactly as it is in the file and saved unchanged. Strayta cannot render or edit it yet; " +
                             "the checkbox shows or hides it.";

    internal override LayerEffect? Result() => Toggled ? effect with { Enabled = IsChecked } : effect;

    protected override LayerEffect Build() => effect;
}

/// <summary>A style Strayta does not have yet (Bevel &amp; Emboss, Satin, Pattern Overlay): listed, greyed, off.</summary>
public sealed partial class UnavailableEntry(LayerStyleViewModel session, LayerStylePage page) : EffectEntry(session, page, null)
{
    public override bool IsEditable => false;
    public override bool CanToggle => false;
    public string Message => $"{Name} is not available in Strayta yet. Layers that have it keep it, and it is saved unchanged.";

    protected override LayerEffect Build() => throw new InvalidOperationException($"{Name} cannot be added yet.");
}
