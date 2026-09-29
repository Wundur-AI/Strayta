using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Editor.Editing;
using Strayta.Text;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// Character and paragraph settings as the Type tool's options bar, the Character and Paragraph panels and the
/// Properties panel show them. Like Photoshop, one set of controls acts on whatever type is current:
/// <list type="bullet">
/// <item>while typing, the selected characters (with nothing selected, what is typed next) and the paragraphs the
/// selection touches, inside the edit (so ⌘Z steps back and the commit is one history step);</item>
/// <item>with a type layer selected but not being edited, the whole layer, as one undoable "Edit Type Layer" step each;</item>
/// <item>otherwise the settings new text starts with.</item>
/// </list>
/// Values that differ across the selection read as mixed: NaN for numbers (the fields show blank), null for
/// toggles, -1 for menus. Sizes and distances show in points: points = text units × the layer's scale × 72 / the
/// document's resolution (pixels per inch), which is how Photoshop relates points to pixels.
/// </summary>
public sealed partial class TypeOptions : ObservableObject
{
    private readonly EditorViewModel _editor;
    private IReadOnlyList<TextStyle> _styles = [];
    private IReadOnlyList<ParagraphStyle> _paragraphs = [];
    private TextAntiAlias? _antiAlias;
    private double _pointsPerUnit = 1;
    private object? _target;
    private bool _refreshing;
    private Dictionary<string, List<FontFace>>? _facesByFamily;

    public TypeOptions(EditorViewModel editor)
    {
        _editor = editor;
        editor.DocumentChanged += Refresh; // another document, undo, redo
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.ForegroundColor) && Target == TypeTarget.NewText) Notify(nameof(Color), nameof(ColorBrush));
        };
        Refresh();
    }

    /// <summary>Settings for new text; sizes in points (text units at 72 pixels per inch). The color is the foreground color.</summary>
    public TextStyle DefaultStyle { get; private set; } = new() { FontSize = 12 };

    public ParagraphStyle DefaultParagraph { get; private set; } = new();
    public TextAntiAlias DefaultAntiAlias { get; private set; } = TextAntiAlias.Sharp;

    private bool _defaultFontChosen;

    /// <summary>What a change applies to right now.</summary>
    public TypeTarget Target => _target switch
    {
        TypeSession => TypeTarget.Editing,
        PixelLayer => TypeTarget.Layer,
        _ => TypeTarget.NewText,
    };

    private DocumentViewModel? Document => _editor.ActiveDocument;

    /// <summary>The selected type layer when it is not being edited.</summary>
    private (PixelLayer Layer, TextLayerData Data)? SelectedTypeLayer =>
        Document?.SelectedLayer?.Node is PixelLayer layer && layer.Tags.Contains("text") && TypeLayers.Read(layer) is { } data ? (layer, data) : null;

    /// <summary>
    /// Re-reads the current type (after typing, a caret move, another layer, undo). Cheap when nothing the controls
    /// show changed, which is the case for most keystrokes.
    /// </summary>
    public void Refresh()
    {
        IReadOnlyList<TextStyle> styles;
        IReadOnlyList<ParagraphStyle> paragraphs;
        TextAntiAlias? antiAlias;
        double ppu;
        object? target;
        if (Document?.TypeSession is { } session)
        {
            styles = session.Editor.SelectedStyles;
            paragraphs = session.Editor.SelectedParagraphStyles;
            antiAlias = session.Editor.Data.AntiAlias;
            ppu = PointsPerUnit(session.Editor.Data, Document.Model);
            target = session;
        }
        else if (SelectedTypeLayer is { } selected)
        {
            styles = selected.Data.StyleRuns.Select(r => r.Style).Distinct().ToList();
            paragraphs = selected.Data.ParagraphRuns.Select(r => r.Style).Distinct().ToList();
            antiAlias = selected.Data.AntiAlias;
            ppu = PointsPerUnit(selected.Data, Document!.Model);
            target = selected.Layer;
        }
        else
        {
            EnsureDefaultFont();
            styles = [DefaultStyle];
            paragraphs = [DefaultParagraph];
            antiAlias = DefaultAntiAlias;
            ppu = 1;
            target = null;
        }
        bool same = ReferenceEquals(target, _target) && styles.SequenceEqual(_styles, ReferenceEqualityComparer.Instance)
            && paragraphs.SequenceEqual(_paragraphs, ReferenceEqualityComparer.Instance) && antiAlias == _antiAlias && ppu == _pointsPerUnit;
        if (same) return;
        (_styles, _paragraphs, _antiAlias, _pointsPerUnit, _target) = (styles, paragraphs, antiAlias, ppu, target);
        _refreshing = true;
        try
        {
            OnPropertyChanged(string.Empty);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Points per text unit: the transform's vertical scale × 72 / resolution.</summary>
    public static double PointsPerUnit(TextLayerData data, Document doc)
    {
        var t = data.Transform;
        double scale = Math.Sqrt(t.YX * t.YX + t.YY * t.YY);
        return (scale > 0 ? scale : 1) * 72 / doc.Resolution;
    }

    /// <summary>The style new text starts with in <paramref name="doc"/>: the defaults in pixels, in the foreground color.</summary>
    public (TextStyle Style, ParagraphStyle Paragraph, TextAntiAlias AntiAlias) NewTextSettings(Document doc)
    {
        EnsureDefaultFont();
        double k = doc.Resolution / 72;
        var c = _editor.ForegroundColor;
        var style = DefaultStyle with
        {
            FontSize = DefaultStyle.FontSize * k, Leading = DefaultStyle.Leading * k, BaselineShift = DefaultStyle.BaselineShift * k,
            FillColor = TextColor.FromRgb8(c.R, c.G, c.B), SourceData = null,
        };
        var p = DefaultParagraph;
        var paragraph = p with
        {
            FirstLineIndent = p.FirstLineIndent * k, StartIndent = p.StartIndent * k, EndIndent = p.EndIndent * k,
            SpaceBefore = p.SpaceBefore * k, SpaceAfter = p.SpaceAfter * k, SourceData = null,
        };
        return (style, paragraph, DefaultAntiAlias);
    }

    /// <summary>After a commit, new text continues in the settings last typed with (the options bar keeps showing them, as in Photoshop).</summary>
    public void RememberFrom(TextStyle style, ParagraphStyle paragraph, TextAntiAlias antiAlias, double pointsPerUnit)
    {
        double k = pointsPerUnit;
        DefaultStyle = style with { FontSize = style.FontSize * k, Leading = style.Leading * k, BaselineShift = style.BaselineShift * k, SourceData = null };
        DefaultParagraph = paragraph with
        {
            FirstLineIndent = paragraph.FirstLineIndent * k, StartIndent = paragraph.StartIndent * k, EndIndent = paragraph.EndIndent * k,
            SpaceBefore = paragraph.SpaceBefore * k, SpaceAfter = paragraph.SpaceAfter * k, SourceData = null,
        };
        DefaultAntiAlias = antiAlias;
        _defaultFontChosen = true;
    }

    private void EnsureDefaultFont()
    {
        if (_defaultFontChosen) return;
        _defaultFontChosen = true;
        // Photoshop starts with Myriad Pro; the catalogue's fallback is Myriad Pro when installed, else a common sans serif.
        if (FontCatalog.System.FallbackPostScriptName is { } font) DefaultStyle = DefaultStyle with { FontPostScriptName = font };
    }

    // ---- Applying a change ----------------------------------------------------------------------------------

    private void Apply(Func<TextStyle, TextStyle> change)
    {
        if (_refreshing) return;
        var doc = Document;
        if (doc?.TypeSession is { } session) session.Editor.ApplyStyle(change);
        else if (SelectedTypeLayer is { } selected) doc!.EditText(selected.Layer, selected.Data.ApplyStyle(change));
        else
        {
            var changed = change(DefaultStyle);
            var c = changed.FillColor;
            if (c != DefaultStyle.FillColor) _editor.ForegroundColor = Color.FromRgb(To8(c.R), To8(c.G), To8(c.B)); // new text uses the foreground color
            DefaultStyle = changed;
        }
        Refresh();
    }

    private void ApplyParagraph(Func<ParagraphStyle, ParagraphStyle> change)
    {
        if (_refreshing) return;
        var doc = Document;
        if (doc?.TypeSession is { } session) session.Editor.ApplyParagraphStyle(change);
        else if (SelectedTypeLayer is { } selected) doc!.EditText(selected.Layer, selected.Data.ApplyParagraphStyle(change));
        else DefaultParagraph = change(DefaultParagraph);
        Refresh();
    }

    private void ApplyAntiAlias(TextAntiAlias value)
    {
        if (_refreshing) return;
        var doc = Document;
        if (doc?.TypeSession is { } session) session.Editor.Replace(session.Editor.Data.WithLayout(antiAlias: value));
        else if (SelectedTypeLayer is { } selected) doc!.EditText(selected.Layer, selected.Data.WithLayout(antiAlias: value));
        else DefaultAntiAlias = value;
        Refresh();
    }

    private static byte To8(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    private void Notify(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }

    /// <summary>The value every selected style shares, or null when they differ.</summary>
    private T? Common<T>(Func<TextStyle, T> read) where T : struct
    {
        if (_styles.Count == 0) return null;
        var first = read(_styles[0]);
        return _styles.Skip(1).All(s => EqualityComparer<T>.Default.Equals(read(s), first)) ? first : null;
    }

    private T? CommonParagraph<T>(Func<ParagraphStyle, T> read) where T : struct
    {
        if (_paragraphs.Count == 0) return null;
        var first = read(_paragraphs[0]);
        return _paragraphs.Skip(1).All(s => EqualityComparer<T>.Default.Equals(read(s), first)) ? first : null;
    }

    private double Points(Func<TextStyle, double> read) => Common(read) is { } v ? Math.Round(v * _pointsPerUnit, 2) : double.NaN;
    private double ParagraphPoints(Func<ParagraphStyle, double> read) => CommonParagraph(read) is { } v ? Math.Round(v * _pointsPerUnit, 2) : double.NaN;
    private double Units(double points) => points / _pointsPerUnit;

    // ---- Font ------------------------------------------------------------------------------------------------

    /// <summary>Installed font families, sorted.</summary>
    public IReadOnlyList<string> Families => FontCatalog.System.Families;

    private IReadOnlyList<FontFace> FacesOf(string family)
    {
        _facesByFamily ??= FontCatalog.System.Faces.GroupBy(f => f.FamilyName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        return _facesByFamily.TryGetValue(family, out var faces) ? faces : [];
    }

    /// <summary>The family shown for a font: its family name, or the PostScript name in brackets when it is missing (as Photoshop shows it).</summary>
    private static string FamilyOf(string postScriptName) =>
        FontCatalog.System.Find(postScriptName) is { } face ? face.FamilyName : $"[{postScriptName}]";

    private static string StyleOf(string postScriptName) => FontCatalog.System.Find(postScriptName)?.StyleName ?? "";

    /// <summary>The font family (null when the selection mixes families).</summary>
    public string? FontFamily
    {
        get => _styles.Count == 0 ? null : _styles.Select(s => FamilyOf(s.FontPostScriptName)).Distinct().Count() == 1 ? FamilyOf(_styles[0].FontPostScriptName) : null;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == FontFamily) return;
            var faces = FacesOf(value);
            if (faces.Count == 0) return;
            Apply(s => s with { FontPostScriptName = ClosestFace(faces, s.FontPostScriptName).PostScriptName });
        }
    }

    /// <summary>The face within the family, e.g. "Bold Italic" (null when mixed).</summary>
    public string? FontStyle
    {
        get => _styles.Count == 0 ? null : _styles.Select(s => StyleOf(s.FontPostScriptName)).Distinct().Count() == 1 ? StyleOf(_styles[0].FontPostScriptName) : null;
        set
        {
            if (string.IsNullOrEmpty(value) || value == FontStyle || FontFamily is not { } family) return;
            if (FacesOf(family).FirstOrDefault(f => f.StyleName == value) is not { } face) return;
            Apply(s => s with { FontPostScriptName = face.PostScriptName });
        }
    }

    /// <summary>The styles of the current family, for the style menu.</summary>
    public IReadOnlyList<string> FontStyles => FontFamily is { } family ? FacesOf(family).Select(f => f.StyleName).Distinct().ToList() : [];

    /// <summary>The PostScript name a family change picks: the same style name, else the nearest weight and slant.</summary>
    private static FontFace ClosestFace(IReadOnlyList<FontFace> faces, string current)
    {
        var now = FontCatalog.System.Find(current);
        if (now is not null && faces.FirstOrDefault(f => f.StyleName == now.StyleName) is { } same) return same;
        int weight = now?.Weight ?? 400;
        bool italic = now?.Italic ?? false;
        return faces.OrderBy(f => f.Italic != italic ? 1 : 0).ThenBy(f => Math.Abs(f.Weight - weight)).ThenBy(f => Math.Abs(f.Width - 5)).First();
    }

    // ---- Character ---------------------------------------------------------------------------------------------

    /// <summary>Photoshop's font size menu, in points.</summary>
    public static IReadOnlyList<double> SizePresets { get; } = [6, 7, 8, 9, 10, 11, 12, 14, 18, 24, 30, 36, 48, 60, 72];

    /// <summary>Photoshop's tracking menu (1/1000 em).</summary>
    public static IReadOnlyList<double> TrackingPresets { get; } = [-100, -75, -50, -25, -10, -5, 0, 5, 10, 25, 50, 75, 100, 200];

    public double FontSize
    {
        get => Points(s => s.FontSize);
        set
        {
            if (!double.IsFinite(value) || value <= 0 || value == FontSize) return;
            double units = Units(Math.Clamp(value, 0.01, 1296));
            Apply(s => s with { FontSize = units });
        }
    }

    [RelayCommand] private void SetFontSize(double points) => FontSize = points;

    /// <summary>Line spacing in points; NaN when Auto (the field then says so) or mixed.</summary>
    public double Leading
    {
        get => Common(s => s.AutoLeading) == false ? Points(s => s.Leading) : double.NaN;
        set
        {
            if (!double.IsFinite(value) || value <= 0 || value == Leading) return;
            double units = Units(value);
            Apply(s => s with { AutoLeading = false, Leading = units });
        }
    }

    /// <summary>Shown in the empty leading field.</summary>
    public string LeadingPlaceholder => Common(s => s.AutoLeading) == true ? "(Auto)" : "";

    [RelayCommand] private void SetLeading(double points) => Leading = points;
    [RelayCommand] private void SetAutoLeading() => Apply(s => s with { AutoLeading = true });

    /// <summary>Kerning menu: 0 Metrics, 1 Optical, 2 "0" (none); -1 when mixed.</summary>
    public int KerningIndex
    {
        get => Common(s => s.Kerning) is { } k ? (int)k : -1;
        set
        {
            if (value < 0 || value > 2 || value == KerningIndex) return;
            Apply(s => s with { Kerning = (TextKerning)value });
        }
    }

    public static IReadOnlyList<string> KerningNames { get; } = ["Metrics", "Optical", "0"];

    /// <summary>Manual kerning before the selected characters, 1/1000 em.</summary>
    public double ManualKerning
    {
        get => Common(s => s.ManualKerning) is { } v ? v : double.NaN;
        set
        {
            if (!double.IsFinite(value) || value == ManualKerning) return;
            int k = (int)Math.Round(Math.Clamp(value, -1000, 10000));
            Apply(s => s with { ManualKerning = k });
        }
    }

    /// <summary>Tracking, 1/1000 em.</summary>
    public double Tracking
    {
        get => Common(s => s.Tracking) is { } v ? v : double.NaN;
        set
        {
            if (!double.IsFinite(value) || value == Tracking) return;
            int t = (int)Math.Round(Math.Clamp(value, -1000, 10000));
            Apply(s => s with { Tracking = t });
        }
    }

    [RelayCommand] private void SetTracking(double value) => Tracking = value;

    /// <summary>Vertical scale in percent.</summary>
    public double VerticalScale
    {
        get => Common(s => s.VerticalScale) is { } v ? Math.Round(v * 100, 2) : double.NaN;
        set
        {
            if (!double.IsFinite(value) || value == VerticalScale) return;
            double f = Math.Clamp(value, 0, 1000) / 100;
            Apply(s => s with { VerticalScale = f });
        }
    }

    /// <summary>Horizontal scale in percent.</summary>
    public double HorizontalScale
    {
        get => Common(s => s.HorizontalScale) is { } v ? Math.Round(v * 100, 2) : double.NaN;
        set
        {
            if (!double.IsFinite(value) || value == HorizontalScale) return;
            double f = Math.Clamp(value, 0, 1000) / 100;
            Apply(s => s with { HorizontalScale = f });
        }
    }

    /// <summary>Baseline shift in points, positive up.</summary>
    public double BaselineShift
    {
        get => Points(s => s.BaselineShift);
        set
        {
            if (!double.IsFinite(value) || value == BaselineShift) return;
            double units = Units(value);
            Apply(s => s with { BaselineShift = units });
        }
    }

    /// <summary>The text color (the foreground color for new text). Mixed colors show the first one.</summary>
    public Color Color
    {
        get
        {
            if (Target == TypeTarget.NewText) return _editor.ForegroundColor;
            var c = _styles.Count > 0 ? _styles[0].FillColor : TextColor.Black;
            return Color.FromRgb(To8(c.R), To8(c.G), To8(c.B));
        }
        set
        {
            if (value == Color && Common(s => s.FillColor) is not null) return;
            var c = new TextColor(value.R / 255.0, value.G / 255.0, value.B / 255.0);
            if (Target == TypeTarget.NewText)
            {
                _editor.ForegroundColor = value;
                DefaultStyle = DefaultStyle with { FillColor = c };
                Notify(nameof(Color), nameof(ColorBrush));
                return;
            }
            Apply(s => s with { FillColor = c });
        }
    }

    public IBrush ColorBrush => new SolidColorBrush(Color);

    /// <summary>True when the selected characters have different colors.</summary>
    public bool IsColorMixed => Common(s => s.FillColor) is null;

    public bool? FauxBold { get => Common(s => s.FauxBold); set { if (value is { } v) Apply(s => s with { FauxBold = v }); } }
    public bool? FauxItalic { get => Common(s => s.FauxItalic); set { if (value is { } v) Apply(s => s with { FauxItalic = v }); } }
    public bool? Underline { get => Common(s => s.Underline); set { if (value is { } v) Apply(s => s with { Underline = v }); } }
    public bool? Strikethrough { get => Common(s => s.Strikethrough); set { if (value is { } v) Apply(s => s with { Strikethrough = v }); } }

    public bool? AllCaps
    {
        get => Common(s => s.Caps) is { } c ? c == TextCaps.AllCaps : null;
        set { if (value is { } v) Apply(s => s with { Caps = v ? TextCaps.AllCaps : s.Caps == TextCaps.AllCaps ? TextCaps.Normal : s.Caps }); }
    }

    public bool? SmallCaps
    {
        get => Common(s => s.Caps) is { } c ? c == TextCaps.SmallCaps : null;
        set { if (value is { } v) Apply(s => s with { Caps = v ? TextCaps.SmallCaps : s.Caps == TextCaps.SmallCaps ? TextCaps.Normal : s.Caps }); }
    }

    public bool? Superscript
    {
        get => Common(s => s.BaselinePosition) is { } p ? p == TextBaselinePosition.Superscript : null;
        set
        {
            if (value is { } v)
                Apply(s => s with { BaselinePosition = v ? TextBaselinePosition.Superscript : s.BaselinePosition == TextBaselinePosition.Superscript ? TextBaselinePosition.Normal : s.BaselinePosition });
        }
    }

    public bool? Subscript
    {
        get => Common(s => s.BaselinePosition) is { } p ? p == TextBaselinePosition.Subscript : null;
        set
        {
            if (value is { } v)
                Apply(s => s with { BaselinePosition = v ? TextBaselinePosition.Subscript : s.BaselinePosition == TextBaselinePosition.Subscript ? TextBaselinePosition.Normal : s.BaselinePosition });
        }
    }

    /// <summary>Anti-aliasing menu: None, Sharp, Crisp, Strong, Smooth (-1 for the platform methods Strayta draws as Smooth).</summary>
    public int AntiAliasIndex
    {
        get => _antiAlias is { } a && (int)a <= (int)TextAntiAlias.Smooth ? (int)a : -1;
        set
        {
            if (value < 0 || value > (int)TextAntiAlias.Smooth || value == AntiAliasIndex) return;
            ApplyAntiAlias((TextAntiAlias)value);
        }
    }

    public static IReadOnlyList<string> AntiAliasNames { get; } = ["None", "Sharp", "Crisp", "Strong", "Smooth"];

    // ---- Paragraph -------------------------------------------------------------------------------------------

    private bool Is(TextJustification j) => CommonParagraph(p => p.Justification) == j;

    private void Justify(TextJustification j, bool on)
    {
        if (on) ApplyParagraph(p => p with { Justification = j });
    }

    public bool AlignLeft { get => Is(TextJustification.Left); set => Justify(TextJustification.Left, value); }
    public bool AlignCenter { get => Is(TextJustification.Center); set => Justify(TextJustification.Center, value); }
    public bool AlignRight { get => Is(TextJustification.Right); set => Justify(TextJustification.Right, value); }
    public bool JustifyLastLeft { get => Is(TextJustification.JustifyLastLeft); set => Justify(TextJustification.JustifyLastLeft, value); }
    public bool JustifyLastCenter { get => Is(TextJustification.JustifyLastCenter); set => Justify(TextJustification.JustifyLastCenter, value); }
    public bool JustifyLastRight { get => Is(TextJustification.JustifyLastRight); set => Justify(TextJustification.JustifyLastRight, value); }
    public bool JustifyAll { get => Is(TextJustification.JustifyAll); set => Justify(TextJustification.JustifyAll, value); }

    public double StartIndent
    {
        get => ParagraphPoints(p => p.StartIndent);
        set { if (double.IsFinite(value) && value != StartIndent) { double u = Units(value); ApplyParagraph(p => p with { StartIndent = u }); } }
    }

    public double EndIndent
    {
        get => ParagraphPoints(p => p.EndIndent);
        set { if (double.IsFinite(value) && value != EndIndent) { double u = Units(value); ApplyParagraph(p => p with { EndIndent = u }); } }
    }

    public double FirstLineIndent
    {
        get => ParagraphPoints(p => p.FirstLineIndent);
        set { if (double.IsFinite(value) && value != FirstLineIndent) { double u = Units(value); ApplyParagraph(p => p with { FirstLineIndent = u }); } }
    }

    public double SpaceBefore
    {
        get => ParagraphPoints(p => p.SpaceBefore);
        set { if (double.IsFinite(value) && value != SpaceBefore) { double u = Units(value); ApplyParagraph(p => p with { SpaceBefore = u }); } }
    }

    public double SpaceAfter
    {
        get => ParagraphPoints(p => p.SpaceAfter);
        set { if (double.IsFinite(value) && value != SpaceAfter) { double u = Units(value); ApplyParagraph(p => p with { SpaceAfter = u }); } }
    }

    /// <summary>Hyphenate (stored in the file; Strayta's line breaking does not hyphenate yet).</summary>
    public bool? Hyphenate { get => CommonParagraph(p => p.Hyphenate); set { if (value is { } v) ApplyParagraph(p => p with { Hyphenate = v }); } }

    /// <summary>A short note on what the controls change, for the panels.</summary>
    public string TargetText => Target switch
    {
        TypeTarget.Editing => "Applies to the selected characters",
        TypeTarget.Layer => "Applies to the whole type layer",
        _ => "Settings for new text",
    };
}

/// <summary>What the type settings change: the text being edited, a selected type layer, or new text.</summary>
public enum TypeTarget
{
    NewText,
    Layer,
    Editing,
}
