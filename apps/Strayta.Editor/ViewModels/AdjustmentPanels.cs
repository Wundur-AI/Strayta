using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Editing;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

/// <summary>Which Properties panel edits which adjustment.</summary>
public static class AdjustmentPanels
{
    public static Type? TypeOf(Adjustment adjustment) => adjustment switch
    {
        LevelsAdjustment => typeof(LevelsPanel),
        CurvesAdjustment => typeof(CurvesPanel),
        HueSaturationAdjustment => typeof(HueSaturationPanel),
        BrightnessContrastAdjustment => typeof(BrightnessContrastPanel),
        ThresholdAdjustment => typeof(ThresholdPanel),
        PosterizeAdjustment => typeof(PosterizePanel),
        InvertAdjustment => typeof(InvertPanel),
        ExposureAdjustment => typeof(ExposurePanel),
        VibranceAdjustment => typeof(VibrancePanel),
        ColorBalanceAdjustment => typeof(ColorBalancePanel),
        BlackWhiteAdjustment => typeof(BlackWhitePanel),
        PhotoFilterAdjustment => typeof(PhotoFilterPanel),
        ChannelMixerAdjustment => typeof(ChannelMixerPanel),
        SelectiveColorAdjustment => typeof(SelectiveColorPanel),
        GradientMapAdjustment => typeof(GradientMapPanel),
        ColorLookupAdjustment => typeof(ColorLookupPanel),
        _ => null,
    };

    public static PropertiesPanel Create(DocumentViewModel document, AdjustmentLayer layer) =>
        (PropertiesPanel)Activator.CreateInstance(TypeOf(layer.Adjustment!)!, document, layer)!;

    internal static Color ToColor(RgbColor c) =>
        Color.FromRgb((byte)Math.Round(Math.Clamp(c.R, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.G, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.B, 0, 1) * 255));

    internal static RgbColor ToRgb(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);
}

/// <summary>Photoshop's Auto for Levels and Curves: the range holding all but the darkest and lightest 0.1% of pixels.</summary>
public static class AutoContrast
{
    public static (int Low, int High)? Range(int[] histogram, double clip = 0.001)
    {
        long total = histogram.Sum(v => (long)v);
        if (total == 0) return null;
        long cut = (long)(total * clip);
        int lo = 0, hi = 255;
        for (long seen = 0; lo < 255 && (seen += histogram[lo]) <= cut; lo++) { }
        for (long seen = 0; hi > 0 && (seen += histogram[hi]) <= cut; hi--) { }
        if (hi - lo < 2) return null;
        return (lo, hi);
    }
}

/// <summary>
/// What every adjustment's panel shares with Photoshop's: a presets menu (where Photoshop has one) and the bottom row —
/// clip to the layer below, reset to the defaults, and the layer's visibility.
/// </summary>
public abstract partial class AdjustmentPanelBase(DocumentViewModel document, AdjustmentLayer layer) : PropertiesPanel(document, layer)
{
    public AdjustmentLayer Layer { get; } = layer;
    public override string Title => Layer.Kind;

    public virtual bool HasPresets => false;
    public virtual IReadOnlyList<string> PresetNames => [];
    public virtual string Preset { get => ""; set { } }

    /// <summary>Clipped to the layer below: the adjustment then only affects that layer.</summary>
    public bool IsClipped
    {
        get => Layer.Clipped;
        set
        {
            Change(() => Document.SetClipped(Layer, value));
            OnPropertyChanged(nameof(IsClipped));
            OnPropertyChanged(nameof(ClipTip));
        }
    }

    public string ClipTip => IsClipped ? "Release the clipping mask: affect all layers below" : "Clip to layer: affect only the layer below";

    public bool IsLayerVisible
    {
        get => Layer.Visible;
        set
        {
            Change(() => Document.SetVisible(Layer, value));
            OnPropertyChanged(nameof(IsLayerVisible));
        }
    }

    /// <summary>Back to the adjustment's default settings (one undo step).</summary>
    public abstract void ResetSettings();

    [RelayCommand] private void Reset() => ResetSettings();
}

public sealed class InvertPanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<InvertAdjustment>(document, layer)
{
    public string Message => "Invert has no settings.";
}

public sealed class ExposurePanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<ExposureAdjustment>(document, layer)
{
    protected override IReadOnlyList<(string, ExposureAdjustment)> PresetList { get; } =
    [
        ("Minus 1.0", new(-1f, 0f, 1f)), ("Minus 2.0", new(-2f, 0f, 1f)), ("Plus 1.0", new(1f, 0f, 1f)), ("Plus 2.0", new(2f, 0f, 1f)),
    ];

    /// <summary>Stops, -20..20.</summary>
    public double Exposure
    {
        get => Settings.Exposure;
        set => Set("Exposure", Settings with { Exposure = (float)Math.Round(Math.Clamp(value, -20, 20), 2) }, nameof(Exposure), nameof(Preset));
    }

    /// <summary>-0.5..0.5.</summary>
    public double Offset
    {
        get => Settings.Offset;
        set => Set("Offset", Settings with { Offset = (float)Math.Round(Math.Clamp(value, -0.5, 0.5), 4) }, nameof(Offset), nameof(Preset));
    }

    /// <summary>Gamma Correction, 9.99 (left, darkest) to 0.01 (right, brightest), as in Photoshop.</summary>
    public double Gamma
    {
        get => Settings.Gamma;
        set => Set("Gamma Correction", Settings with { Gamma = (float)Math.Round(Math.Clamp(value, 0.01, 9.99), 2) }, nameof(Gamma), nameof(Preset));
    }
}

public sealed class VibrancePanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<VibranceAdjustment>(document, layer)
{
    public double Vibrance
    {
        get => Settings.Vibrance;
        set => Set("Vibrance", Settings with { Vibrance = (int)Math.Round(Math.Clamp(value, -100, 100)) }, nameof(Vibrance));
    }

    public double Saturation
    {
        get => Settings.Saturation;
        set => Set("Saturation", Settings with { Saturation = (int)Math.Round(Math.Clamp(value, -100, 100)) }, nameof(Saturation));
    }
}

public sealed class ColorBalancePanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<ColorBalanceAdjustment>(document, layer)
{
    private int _tone = 1;

    public IReadOnlyList<string> Tones { get; } = ["Shadows", "Midtones", "Highlights"];

    /// <summary>0 shadows, 1 midtones (Photoshop's starting choice), 2 highlights.</summary>
    public int Tone
    {
        get => _tone;
        set
        {
            if (value is < 0 or > 2 || value == _tone) return;
            _tone = value;
            OnPropertyChanged(string.Empty);
        }
    }

    private ColorBalanceTone Current => _tone switch { 0 => Settings.Shadows, 2 => Settings.Highlights, _ => Settings.Midtones };

    private void SetCurrent(string control, ColorBalanceTone t, string property) => Set(control, _tone switch
    {
        0 => Settings with { Shadows = t },
        2 => Settings with { Highlights = t },
        _ => Settings with { Midtones = t },
    }, property);

    private static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, -100, 100));

    public double CyanRed { get => Current.CyanRed; set => SetCurrent("Cyan-Red", Current with { CyanRed = Clamp(value) }, nameof(CyanRed)); }
    public double MagentaGreen { get => Current.MagentaGreen; set => SetCurrent("Magenta-Green", Current with { MagentaGreen = Clamp(value) }, nameof(MagentaGreen)); }
    public double YellowBlue { get => Current.YellowBlue; set => SetCurrent("Yellow-Blue", Current with { YellowBlue = Clamp(value) }, nameof(YellowBlue)); }

    public bool PreserveLuminosity
    {
        get => Settings.PreserveLuminosity;
        set => Set("Preserve Luminosity", Settings with { PreserveLuminosity = value }, nameof(PreserveLuminosity));
    }
}

public sealed class BlackWhitePanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<BlackWhiteAdjustment>(document, layer)
{
    private static BlackWhiteAdjustment P(int r, int y, int g, int c, int b, int m) => BlackWhiteAdjustment.Default with
    {
        Reds = r, Yellows = y, Greens = g, Cyans = c, Blues = b, Magentas = m,
    };

    /// <summary>
    /// Photoshop's preset names. Infrared, Maximum Black and Maximum White use Photoshop's values; the others are
    /// Strayta's filter-like mixes under the same names.
    /// </summary>
    protected override IReadOnlyList<(string, BlackWhiteAdjustment)> PresetList { get; } =
    [
        ("Blue Filter", P(0, 0, 0, 110, 110, 110)),
        ("Darker", P(30, 40, 30, 40, 10, 50)),
        ("Green Filter", P(40, 120, 120, 60, 20, 20)),
        ("High Contrast Blue Filter", P(-50, -50, -50, 150, 150, 150)),
        ("High Contrast Red Filter", P(120, 120, -10, -50, -50, 120)),
        ("Infrared", P(-40, 235, 144, -68, -3, -107)),
        ("Lighter", P(55, 80, 55, 80, 35, 100)),
        ("Maximum Black", P(0, 0, 0, 0, 0, 0)),
        ("Maximum White", P(100, 100, 100, 100, 100, 100)),
        ("Neutral Density", P(128, 128, 100, 100, 128, 100)),
        ("Red Filter", P(120, 110, -10, -50, -50, 120)),
        ("Yellow Filter", P(120, 110, 40, 30, -10, 50)),
    ];

    private static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, -200, 300));

    public double Reds { get => Settings.Reds; set => Set("Reds", Settings with { Reds = Clamp(value) }, nameof(Reds), nameof(Preset)); }
    public double Yellows { get => Settings.Yellows; set => Set("Yellows", Settings with { Yellows = Clamp(value) }, nameof(Yellows), nameof(Preset)); }
    public double Greens { get => Settings.Greens; set => Set("Greens", Settings with { Greens = Clamp(value) }, nameof(Greens), nameof(Preset)); }
    public double Cyans { get => Settings.Cyans; set => Set("Cyans", Settings with { Cyans = Clamp(value) }, nameof(Cyans), nameof(Preset)); }
    public double Blues { get => Settings.Blues; set => Set("Blues", Settings with { Blues = Clamp(value) }, nameof(Blues), nameof(Preset)); }
    public double Magentas { get => Settings.Magentas; set => Set("Magentas", Settings with { Magentas = Clamp(value) }, nameof(Magentas), nameof(Preset)); }

    public bool Tint
    {
        get => Settings.Tint;
        set => Set("Tint", Settings with { Tint = value }, nameof(Tint), nameof(Preset));
    }

    public Color TintColor
    {
        get => AdjustmentPanels.ToColor(Settings.TintColor);
        set => Set("Tint Color", Settings with { TintColor = AdjustmentPanels.ToRgb(value) }, nameof(TintColor), nameof(TintBrush), nameof(Preset));
    }

    public IBrush TintBrush => new SolidColorBrush(TintColor);
}

public sealed class PhotoFilterPanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<PhotoFilterAdjustment>(document, layer)
{
    private bool? _useColor;

    public IReadOnlyList<string> FilterNames { get; } = PhotoFilters.All.Select(f => f.Name).ToArray();

    /// <summary>Filter (one of Photoshop's) or Color (any color); a color that is not a filter's shows as Color.</summary>
    public bool UseFilter
    {
        get => !UseColor;
        set => UseColor = !value;
    }

    public bool UseColor
    {
        get => _useColor ?? PhotoFilters.Matching(Settings.Color) is null;
        set
        {
            _useColor = value;
            if (!value && PhotoFilters.Matching(Settings.Color) is null)
                Set("Filter", Settings with { Color = PhotoFilters.Default.Color }, nameof(FilterName));
            OnPropertyChanged(string.Empty);
        }
    }

    public string? FilterName
    {
        get => PhotoFilters.Matching(Settings.Color)?.Name;
        set
        {
            if (value is null || PhotoFilters.All.FirstOrDefault(f => f.Name == value) is not { } filter) return;
            _useColor = false;
            Set("Filter", Settings with { Color = filter.Color }, nameof(FilterName), nameof(FilterColor), nameof(FilterBrush), nameof(UseColor), nameof(UseFilter));
        }
    }

    public Color FilterColor
    {
        get => AdjustmentPanels.ToColor(Settings.Color);
        set
        {
            _useColor = true;
            Set("Filter Color", Settings with { Color = AdjustmentPanels.ToRgb(value) }, nameof(FilterColor), nameof(FilterBrush), nameof(FilterName));
        }
    }

    public IBrush FilterBrush => new SolidColorBrush(FilterColor);

    public double Density
    {
        get => Settings.Density;
        set => Set("Density", Settings with { Density = (int)Math.Round(Math.Clamp(value, 1, 100)) }, nameof(Density));
    }

    public bool PreserveLuminosity
    {
        get => Settings.PreserveLuminosity;
        set => Set("Preserve Luminosity", Settings with { PreserveLuminosity = value }, nameof(PreserveLuminosity));
    }

    protected override void PresetChosen() => _useColor = null;
}

public sealed class ChannelMixerPanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<ChannelMixerAdjustment>(document, layer)
{
    private int _output;
    private static readonly string[] Rgb = ["Red", "Green", "Blue"], Gray = ["Gray"];

    private static ChannelMixerAdjustment Mono(int r, int g, int b) => ChannelMixerAdjustment.Default with { Monochrome = true, Gray = new(r, g, b, 0) };

    /// <summary>Photoshop's Channel Mixer presets.</summary>
    protected override IReadOnlyList<(string, ChannelMixerAdjustment)> PresetList { get; } =
    [
        ("Black & White Infrared (RGB)", Mono(-70, 200, -30)),
        ("Black & White with Blue Filter (RGB)", Mono(0, 0, 100)),
        ("Black & White with Green Filter (RGB)", Mono(0, 100, 0)),
        ("Black & White with Orange Filter (RGB)", Mono(50, 50, 0)),
        ("Black & White with Red Filter (RGB)", Mono(100, 0, 0)),
        ("Black & White with Yellow Filter (RGB)", Mono(34, 66, 0)),
    ];

    public IReadOnlyList<string> OutputChannels => Settings.Monochrome ? Gray : Rgb;

    public int OutputChannel
    {
        get => Settings.Monochrome ? 0 : _output;
        set
        {
            if (value < 0 || Settings.Monochrome || value == _output) return;
            _output = Math.Min(value, 2);
            OnPropertyChanged(string.Empty);
        }
    }

    public bool Monochrome
    {
        get => Settings.Monochrome;
        set
        {
            Set("Monochrome", Settings with { Monochrome = value }, nameof(Monochrome));
            OnPropertyChanged(string.Empty);
        }
    }

    private MixerChannel Current => Settings.Monochrome ? Settings.Gray : _output switch { 1 => Settings.Green, 2 => Settings.Blue, _ => Settings.Red };

    private void SetCurrent(string control, MixerChannel c, string property)
    {
        var s = Settings;
        var next = s.Monochrome ? s with { Gray = c } : _output switch { 1 => s with { Green = c }, 2 => s with { Blue = c }, _ => s with { Red = c } };
        Set(control, next, property, nameof(Total), nameof(TotalOver), nameof(Preset));
    }

    private static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, -200, 200));

    public double Red { get => Current.Red; set => SetCurrent("Red", Current with { Red = Clamp(value) }, nameof(Red)); }
    public double Green { get => Current.Green; set => SetCurrent("Green", Current with { Green = Clamp(value) }, nameof(Green)); }
    public double Blue { get => Current.Blue; set => SetCurrent("Blue", Current with { Blue = Clamp(value) }, nameof(Blue)); }
    public double Constant { get => Current.Constant; set => SetCurrent("Constant", Current with { Constant = Clamp(value) }, nameof(Constant)); }

    /// <summary>The sum of the source percentages, as Photoshop shows it ("+100%").</summary>
    public string Total => $"{Current.Red + Current.Green + Current.Blue:+0;-0;0}%";

    /// <summary>Totals above 100% may clip highlights (Photoshop shows a warning sign).</summary>
    public bool TotalOver => Current.Red + Current.Green + Current.Blue > 100;

    protected override void PresetChosen() => _output = 0;
}

public sealed class SelectiveColorPanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<SelectiveColorAdjustment>(document, layer)
{
    private int _range;

    public IReadOnlyList<string> Colors { get; } = ["Reds", "Yellows", "Greens", "Cyans", "Blues", "Magentas", "Whites", "Neutrals", "Blacks"];

    public int Range
    {
        get => _range;
        set
        {
            if (value is < 0 or > 8 || value == _range) return;
            _range = value;
            OnPropertyChanged(string.Empty);
        }
    }

    private SelectiveColorValues Current => Settings[(SelectiveColorRange)_range];

    private void SetCurrent(string control, SelectiveColorValues v, string property) =>
        Set(control, Settings.With((SelectiveColorRange)_range, v), property, nameof(Preset));

    private static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, -100, 100));

    public double Cyan { get => Current.Cyan; set => SetCurrent("Cyan", Current with { Cyan = Clamp(value) }, nameof(Cyan)); }
    public double Magenta { get => Current.Magenta; set => SetCurrent("Magenta", Current with { Magenta = Clamp(value) }, nameof(Magenta)); }
    public double Yellow { get => Current.Yellow; set => SetCurrent("Yellow", Current with { Yellow = Clamp(value) }, nameof(Yellow)); }
    public double Black { get => Current.Black; set => SetCurrent("Black", Current with { Black = Clamp(value) }, nameof(Black)); }

    public bool Relative
    {
        get => !Settings.Absolute;
        set => Set("Method", Settings with { Absolute = !value }, nameof(Relative), nameof(Absolute));
    }

    public bool Absolute
    {
        get => Settings.Absolute;
        set => Set("Method", Settings with { Absolute = value }, nameof(Relative), nameof(Absolute));
    }
}

public sealed class GradientMapPanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<GradientMapAdjustment>(document, layer)
{
    public Gradient? Gradient
    {
        get => Settings.Gradient;
        set
        {
            if (value is null) return;
            Set("Gradient", Settings with { Gradient = value.Resolve(Document.Editor.ForegroundRgb, Document.Editor.BackgroundRgb) }, nameof(Gradient));
        }
    }

    public RgbColor Foreground => Document.Editor.ForegroundRgb;
    public RgbColor Background => Document.Editor.BackgroundRgb;

    public bool Dither
    {
        get => Settings.Dither;
        set => Set("Dither", Settings with { Dither = value }, nameof(Dither));
    }

    public bool Reverse
    {
        get => Settings.Reverse;
        set => Set("Reverse", Settings with { Reverse = value }, nameof(Reverse));
    }

    public IReadOnlyList<string> Methods { get; } = ["Perceptual", "Linear", "Classic"];

    public int Method
    {
        get => (int)Settings.Method;
        set
        {
            if (value is < 0 or > 2) return;
            Set("Method", Settings with { Method = (GradientMethod)value }, nameof(Method));
        }
    }
}

public sealed class ColorLookupPanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<ColorLookupAdjustment>(document, layer)
{
    public const string LoadItem = "Load 3D LUT…";

    /// <summary>Looks generated by Strayta (so the menu is not empty); a loaded file joins them while it is selected.</summary>
    private static readonly Lazy<IReadOnlyList<(string Name, byte[] Data)>> BuiltIn = new(() =>
    [
        ("Strayta Warm.cube", LookupTable3D.WriteCube("Strayta Warm", 17, (r, g, b) => (MathF.Min(1, r * 1.06f + 0.02f), g * 1.01f, b * 0.9f))),
        ("Strayta Cool.cube", LookupTable3D.WriteCube("Strayta Cool", 17, (r, g, b) => (r * 0.92f, g * 1.0f, MathF.Min(1, b * 1.07f + 0.02f)))),
        ("Faded Film.cube", LookupTable3D.WriteCube("Faded Film", 17, (r, g, b) => (0.08f + r * 0.86f, 0.07f + g * 0.84f, 0.1f + b * 0.8f))),
        ("Teal and Orange.cube", LookupTable3D.WriteCube("Teal and Orange", 17, TealOrange)),
        ("Bleach Bypass.cube", LookupTable3D.WriteCube("Bleach Bypass", 17, BleachBypass)),
    ]);

    private static (float, float, float) TealOrange(float r, float g, float b)
    {
        float l = 0.3f * r + 0.59f * g + 0.11f * b;
        float t = l * l * (3 - 2 * l); // shadows toward teal, highlights toward orange
        (float R, float G, float B) tint = (0.1f + 0.9f * t, 0.45f + 0.15f * t, 0.5f - 0.35f * t);
        float k = 0.25f;
        return (Math.Clamp(r + (tint.R - 0.5f) * k, 0, 1), Math.Clamp(g + (tint.G - 0.5f) * k, 0, 1), Math.Clamp(b + (tint.B - 0.5f) * k, 0, 1));
    }

    private static (float, float, float) BleachBypass(float r, float g, float b)
    {
        float l = 0.3f * r + 0.59f * g + 0.11f * b;
        static float Contrast(float v) => Math.Clamp((v - 0.5f) * 1.25f + 0.5f, 0, 1);
        return (Contrast(r + (l - r) * 0.6f), Contrast(g + (l - g) * 0.6f), Contrast(b + (l - b) * 0.6f));
    }

    public bool IsLut => Settings.Kind == ColorLookupKind.Lut3D;

    public string ProfileNote => IsLut ? "" :
        $"This layer uses an ICC {(Settings.Kind == ColorLookupKind.AbstractProfile ? "abstract" : "device link")} profile (\"{Settings.Name}\"). " +
        "Strayta keeps it but cannot apply it yet; choose a 3D LUT to replace it.";

    public IReadOnlyList<string> LutNames
    {
        get
        {
            var names = new List<string> { LoadItem };
            if (IsLut && Settings.Data.Length > 0 && BuiltIn.Value.All(b => b.Name != Settings.Name)) names.Add(Settings.Name);
            names.AddRange(BuiltIn.Value.Select(b => b.Name));
            return names;
        }
    }

    /// <summary>The chosen LUT's name; choosing a built-in look loads it (the Load item is handled by the view).</summary>
    public string? LutName
    {
        get => IsLut && Settings.Data.Length > 0 ? Settings.Name : null;
        set
        {
            if (value is null || value == LoadItem || value == LutName) return;
            if (BuiltIn.Value.FirstOrDefault(b => b.Name == value) is { Name: not null } look)
                Use(look.Name, "CUBE", look.Data);
        }
    }

    public bool Dither
    {
        get => Settings.Dither;
        set => Set("Dither", Settings with { Dither = value }, nameof(Dither));
    }

    public string? Error { get; private set; }

    /// <summary>Loads a .cube or .3dl file into the layer (the file is embedded, as Photoshop does).</summary>
    public bool LoadFile(string path)
    {
        Error = null;
        string? format = LookupTable3D.FormatOfExtension(path);
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (IOException e)
        {
            Error = e.Message;
            OnPropertyChanged(nameof(Error));
            return false;
        }
        if (format is null || LookupTable3D.Parse(data, format) is null)
        {
            Error = $"\"{Path.GetFileName(path)}\" is not a 3D LUT Strayta can read (.cube or .3dl).";
            OnPropertyChanged(nameof(Error));
            return false;
        }
        Use(Path.GetFileName(path), format, data);
        return true;
    }

    private void Use(string name, string format, byte[] data)
    {
        Set("3D LUT", new ColorLookupAdjustment(ColorLookupKind.Lut3D, name, format, data, Settings.Dither));
        OnPropertyChanged(string.Empty);
    }
}
