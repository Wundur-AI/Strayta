using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Psd;

namespace Strayta.Editor.ViewModels;

/// <summary>The Properties panel: the selected adjustment layer's settings, or the selected layer's size, position and mask.</summary>
public sealed class PropertiesToolViewModel(EditorViewModel editor) : Tool
{
    public EditorViewModel Editor { get; } = editor;
}

/// <summary>
/// What the Properties panel shows for one layer. Panels read the model directly and write through undoable
/// edits; after any edit or undo they re-read, unless the change was their own.
/// </summary>
public abstract class PropertiesPanel : ObservableObject, IDisposable
{
    private bool _changing;

    protected PropertiesPanel(DocumentViewModel document, LayerNode? node)
    {
        Document = document;
        Node = node;
        document.PropertyChanged += OnDocumentChanged;
    }

    public DocumentViewModel Document { get; }
    public LayerNode? Node { get; }
    public abstract string Title { get; }

    /// <summary>Icon resource key shown next to the title.</summary>
    public virtual string Icon => "IconAdjust";

    /// <summary>The panel to show for <paramref name="node"/>.</summary>
    public static PropertiesPanel For(DocumentViewModel document, LayerNode? node) => node switch
    {
        AdjustmentLayer { Adjustment: { } adjustment } a when AdjustmentPanels.TypeOf(adjustment) is not null => AdjustmentPanels.Create(document, a),
        AdjustmentLayer a => new MessagePanel(document, a, a.Kind,
            $"Strayta cannot edit {a.Kind} adjustments yet. The layer is kept exactly as it is when you save."),
        PixelLayer or LayerGroup => new LayerPanel(document, node),
        { } n when n.GetMask() is not null => new MaskPanel(document, n),
        { } n => new MessagePanel(document, n, "Properties", n.CanHaveMask()
            ? "No properties. Add a layer mask (Layer › Layer Mask) to control where this layer shows."
            : "No properties."),
        null => new MessagePanel(document, null, "Properties", "Select a layer to see its properties."),
    };

    /// <summary>The type of panel <see cref="For"/> creates for <paramref name="node"/>, without creating one.</summary>
    private static Type TypeFor(LayerNode? node) => node switch
    {
        AdjustmentLayer { Adjustment: { } adjustment } when AdjustmentPanels.TypeOf(adjustment) is { } type => type,
        AdjustmentLayer => typeof(MessagePanel),
        PixelLayer or LayerGroup => typeof(LayerPanel),
        { } n when n.GetMask() is not null => typeof(MaskPanel),
        _ => typeof(MessagePanel),
    };

    /// <summary>True while this panel still suits <paramref name="node"/>; otherwise the document makes a new one.</summary>
    public bool Fits(LayerNode? node) => ReferenceEquals(node, Node) && TypeFor(node) == GetType();

    /// <summary>Re-reads everything (e.g. when the layer's targeted thumbnail changes).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Makes an edit, without re-reading the model for the change notifications it causes.</summary>
    protected void Change(Action edit)
    {
        _changing = true;
        try { edit(); }
        finally { _changing = false; }
    }

    /// <summary>Re-reads the model after undo, redo or an edit made elsewhere.</summary>
    protected virtual void Reload() => OnPropertyChanged(string.Empty);

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DocumentViewModel.UndoText) || _changing) return;
        if (!Fits(Document.SelectedLayer?.Node)) Document.UpdateProperties();
        else Reload();
    }

    public virtual void Dispose() => Document.PropertyChanged -= OnDocumentChanged;
}

/// <summary>A panel with only a short explanation.</summary>
public sealed class MessagePanel(DocumentViewModel document, LayerNode? node, string title, string message) : PropertiesPanel(document, node)
{
    public override string Title => title;
    public string Message => message;
    public override string Icon => Node is AdjustmentLayer ? "IconAdjust" : "IconInfo";
}

/// <summary>A layer's mask: its state and the Layer Mask commands.</summary>
public sealed partial class MaskPanel(DocumentViewModel document, LayerNode node) : PropertiesPanel(document, node)
{
    public override string Title => "Layer Mask";
    public override string Icon => "IconMask";

    private LayerMask? Mask => Node?.GetMask();
    public bool IsDisabled => Mask?.Disabled == true;
    public string ToggleText => IsDisabled ? "Enable" : "Disable";
    public bool CanApply => Node is PixelLayer;

    public string Status => IsDisabled
        ? "The mask is disabled, so the whole layer shows. Shift-click the mask thumbnail to enable it again."
        : Document.EditMask
            ? "Painting edits the mask: black hides, white reveals. The Eraser paints the background color."
            : "Click the mask thumbnail in the Layers panel to paint in the mask.";

    [RelayCommand] private void Toggle() => Document.ToggleMaskEnabled();
    [RelayCommand] private void Delete() => Document.DeleteMask();
    [RelayCommand] private async Task ApplyMask() => await Document.ApplyMaskAsync();

}

/// <summary>Base for panels editing one kind of adjustment.</summary>
public abstract class AdjustmentPanel<T>(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanelBase(document, layer) where T : Adjustment
{
    /// <summary>The current settings (the panel is replaced if the adjustment changes type).</summary>
    protected T Settings => (T)Layer.Adjustment!;

    /// <summary>Photoshop's presets for this adjustment (besides Default), in its menu order.</summary>
    protected virtual IReadOnlyList<(string Name, T Settings)> PresetList => [];

    private IReadOnlyList<string>? _presetNames;

    public override bool HasPresets => PresetList.Count > 0;

    /// <summary>Default, the presets, and Custom (shown when the settings match none of them).</summary>
    public override IReadOnlyList<string> PresetNames => _presetNames ??= ["Default", .. PresetList.Select(p => p.Name), "Custom"];

    public override string Preset
    {
        get
        {
            var s = Settings;
            if (AdjustmentFactory.KindOf(s) is { } kind && PsdAdjustmentWriter.SameSettings(s, AdjustmentFactory.Default(kind))) return "Default";
            foreach (var (name, settings) in PresetList)
                if (PsdAdjustmentWriter.SameSettings(s, settings)) return name;
            return "Custom";
        }
        set
        {
            if (value is null || value == Preset || value == "Custom") return;
            T? chosen = value == "Default" ? DefaultSettings : PresetList.FirstOrDefault(p => p.Name == value).Settings;
            if (chosen is null) return;
            Change(() => Document.SetAdjustment(Layer, chosen, "Preset " + value));
            PresetChosen();
            OnPropertyChanged(string.Empty);
        }
    }

    /// <summary>Called after a preset or reset replaced the settings (panels reset their own view state here).</summary>
    protected virtual void PresetChosen()
    {
    }

    private T? DefaultSettings => AdjustmentFactory.KindOf(Settings) is { } kind ? AdjustmentFactory.Default(kind) as T : null;

    public override void ResetSettings()
    {
        if (DefaultSettings is not { } d) return;
        Change(() => Document.SetAdjustment(Layer, d, "Reset"));
        PresetChosen();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Records new settings; edits from the same <paramref name="control"/> merge into one undo step.</summary>
    protected void Set(string control, T value, params string[] changed)
    {
        Change(() => Document.SetAdjustment(Layer, value, control));
        foreach (var p in changed) OnPropertyChanged(p);
    }

    private static readonly string[] RgbChannels = ["RGB", "Red", "Green", "Blue"], GrayChannels = ["Gray"];

    /// <summary>
    /// Channel names for Levels and Curves: the composite plus each color channel. Shared instances: a menu handed
    /// a new list on every refresh resets its selection and writes it back.
    /// </summary>
    protected IReadOnlyList<string> ChannelNamesFor() => Document.Model.ColorMode == ColorMode.Rgb ? RgbChannels : GrayChannels;

    private int[][]? _histograms;

    /// <summary>Histograms of the image below, computed once in the background; see <see cref="DocumentViewModel.HistogramWithoutAsync"/>.</summary>
    protected int[]? HistogramFor(int channel)
    {
        if (_histograms is null)
        {
            _histograms = [];
            _ = LoadHistogramsAsync();
            return null;
        }
        return _histograms.Length == 0 ? null : _histograms[Math.Min(channel, _histograms.Length - 1)];
    }

    private async Task LoadHistogramsAsync()
    {
        try
        {
            _histograms = await Document.HistogramWithoutAsync(Layer);
            OnPropertyChanged("Histogram");
        }
        catch (Exception)
        {
            // A histogram is a nicety; the controls work without one.
        }
    }
}

public sealed class LevelsPanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<LevelsAdjustment>(document, layer)
{
    private int _channel;

    public IReadOnlyList<string> ChannelNames => ChannelNamesFor();

    /// <summary>0 = the composite (master) record, then one per color channel.</summary>
    public int Channel
    {
        get => _channel;
        set
        {
            if (value < 0 || value == _channel) return;
            _channel = value;
            OnPropertyChanged(string.Empty);
        }
    }

    public int[]? Histogram => HistogramFor(_channel);

    private LevelsChannel Current =>
        _channel == 0 ? Settings.Master : _channel - 1 < Settings.Channels.Count ? Settings.Channels[_channel - 1] : LevelsChannel.Identity;

    private void SetCurrent(string control, LevelsChannel value, string property)
    {
        var s = Settings;
        if (_channel == 0) Set(control, s with { Master = value }, property);
        else
        {
            var channels = s.Channels.ToList();
            while (channels.Count < _channel) channels.Add(LevelsChannel.Identity);
            channels[_channel - 1] = value;
            Set(control, s with { Channels = channels }, property);
        }
    }

    public double InputBlack
    {
        get => Current.InputBlack;
        set => SetCurrent("Input Black", Current with { InputBlack = Math.Clamp((int)Math.Round(value), 0, Current.InputWhite - 2) }, nameof(InputBlack));
    }

    public double InputWhite
    {
        get => Current.InputWhite;
        set => SetCurrent("Input White", Current with { InputWhite = Math.Clamp((int)Math.Round(value), Current.InputBlack + 2, 255) }, nameof(InputWhite));
    }

    /// <summary>Midtone gamma, 0.10..9.99 (Photoshop's range); its slider moves on a log scale so 1.00 is centered.</summary>
    public double Gamma
    {
        get => Current.Gamma;
        set => SetCurrent("Gamma", Current with { Gamma = (float)Math.Round(Math.Clamp(value, 0.1, 9.99), 2) }, nameof(Gamma));
    }

    public double OutputBlack
    {
        get => Current.OutputBlack;
        set => SetCurrent("Output Black", Current with { OutputBlack = Math.Clamp((int)Math.Round(value), 0, 255) }, nameof(OutputBlack));
    }

    public double OutputWhite
    {
        get => Current.OutputWhite;
        set => SetCurrent("Output White", Current with { OutputWhite = Math.Clamp((int)Math.Round(value), 0, 255) }, nameof(OutputWhite));
    }

    /// <summary>
    /// Auto: Photoshop's "Enhance Per Channel Contrast" — each color channel's darkest and lightest 0.1% become black
    /// and white (the composite record is reset). One undo step.
    /// </summary>
    public async Task AutoAsync()
    {
        var bins = await Document.HistogramWithoutAsync(Layer);
        var channels = Enumerable.Range(1, 3).Select(c => AutoContrast.Range(bins[c]) is var (lo, hi)
            ? new LevelsChannel(lo, hi, 0, 255, 1f) : LevelsChannel.Identity).ToArray();
        Set("Auto", Settings with { Master = LevelsChannel.Identity, Channels = channels }, string.Empty);
    }
}

public sealed class CurvesPanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<CurvesAdjustment>(document, layer)
{
    private int _channel;

    public IReadOnlyList<string> ChannelNames => ChannelNamesFor();

    public int Channel
    {
        get => _channel;
        set
        {
            if (value < 0 || value == _channel) return;
            _channel = value;
            OnPropertyChanged(string.Empty);
        }
    }

    public int[]? Histogram => HistogramFor(_channel);

    /// <summary>The selected channel's points; channels without their own curve show the identity line.</summary>
    public IReadOnlyList<CurvePoint> Points
    {
        get => (_channel == 0 ? Settings.Master : _channel - 1 < Settings.Channels.Count ? Settings.Channels[_channel - 1] : null)
               ?? AdjustmentFactory.IdentityCurve;
        set => SetPoints(value);
    }

    /// <summary>Called by the curve graph while points are added, dragged or removed.</summary>
    public void SetPoints(IReadOnlyList<CurvePoint> points)
    {
        var s = Settings;
        if (_channel == 0) Set("Curve", s with { Master = points }, nameof(Points));
        else
        {
            var channels = s.Channels.ToList();
            while (channels.Count < _channel) channels.Add(null);
            channels[_channel - 1] = points;
            Set("Curve", s with { Channels = channels }, nameof(Points));
        }
    }

    public void Reset() => SetPoints(AdjustmentFactory.IdentityCurve);

    /// <summary>
    /// Photoshop's preset names. Negative is Photoshop's curve; the contrast, lighter and darker curves are Strayta's
    /// under the same names.
    /// </summary>
    protected override IReadOnlyList<(string, CurvesAdjustment)> PresetList { get; } =
    [
        ("Darker (RGB)", Master([new(0, 0), new(128, 100), new(255, 255)])),
        ("Increase Contrast (RGB)", Master([new(0, 0), new(64, 54), new(192, 202), new(255, 255)])),
        ("Lighter (RGB)", Master([new(0, 0), new(128, 156), new(255, 255)])),
        ("Linear Contrast (RGB)", Master([new(0, 0), new(64, 58), new(192, 198), new(255, 255)])),
        ("Medium Contrast (RGB)", Master([new(0, 0), new(64, 48), new(192, 208), new(255, 255)])),
        ("Negative (RGB)", Master([new(0, 255), new(255, 0)])),
        ("Strong Contrast (RGB)", Master([new(0, 0), new(64, 38), new(192, 218), new(255, 255)])),
    ];

    private static CurvesAdjustment Master(IReadOnlyList<CurvePoint> points) => new(points, [null, null, null]);

    protected override void PresetChosen() => _channel = 0;

    /// <summary>Auto: each color channel's curve maps its darkest and lightest 0.1% to black and white. One undo step.</summary>
    public async Task AutoAsync()
    {
        var bins = await Document.HistogramWithoutAsync(Layer);
        var channels = Enumerable.Range(1, 3).Select(c => AutoContrast.Range(bins[c]) is var (lo, hi)
            ? (IReadOnlyList<CurvePoint>?)[new(lo, 0), new(hi, 255)] : null).ToArray();
        Set("Auto", Settings with { Master = AdjustmentFactory.IdentityCurve, Channels = channels }, string.Empty);
    }
}

public sealed class HueSaturationPanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<HueSaturationAdjustment>(document, layer)
{
    private static readonly string[] All = [nameof(Hue), nameof(Saturation), nameof(Lightness), nameof(HueMinimum), nameof(HueMaximum), nameof(SaturationMinimum)];

    /// <summary>Colorize tints the image with one hue; the sliders then set that hue (0..360) and its saturation (0..100).</summary>
    public bool Colorize
    {
        get => Settings.Colorize;
        set => Set("Colorize", Settings with { Colorize = value }, [nameof(Colorize), .. All]);
    }

    public double HueMinimum => Colorize ? 0 : -180;
    public double HueMaximum => Colorize ? 360 : 180;
    public double SaturationMinimum => Colorize ? 0 : -100;

    public double Hue
    {
        get => Colorize ? Settings.ColorizeHue : Settings.Hue;
        set
        {
            int v = (int)Math.Round(Math.Clamp(value, HueMinimum, HueMaximum));
            Set("Hue", Colorize ? Settings with { ColorizeHue = v } : Settings with { Hue = v }, nameof(Hue));
        }
    }

    public double Saturation
    {
        get => Colorize ? Settings.ColorizeSaturation : Settings.Saturation;
        set
        {
            int v = (int)Math.Round(Math.Clamp(value, SaturationMinimum, 100));
            Set("Saturation", Colorize ? Settings with { ColorizeSaturation = v } : Settings with { Saturation = v }, nameof(Saturation));
        }
    }

    public double Lightness
    {
        get => Colorize ? Settings.ColorizeLightness : Settings.Lightness;
        set
        {
            int v = (int)Math.Round(Math.Clamp(value, -100, 100));
            Set("Lightness", Colorize ? Settings with { ColorizeLightness = v } : Settings with { Lightness = v }, nameof(Lightness));
        }
    }

    public bool HasColorRangeEdits => Settings.HasColorRangeEdits;
}

public sealed class BrightnessContrastPanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<BrightnessContrastAdjustment>(document, layer)
{
    /// <summary>-150..150, Photoshop's range.</summary>
    public double Brightness
    {
        get => Settings.Brightness;
        set => Set("Brightness", Settings with { Brightness = (int)Math.Round(Math.Clamp(value, -150, 150)) }, nameof(Brightness));
    }

    /// <summary>-50..100, Photoshop's range.</summary>
    public double Contrast
    {
        get => Settings.Contrast;
        set => Set("Contrast", Settings with { Contrast = (int)Math.Round(Math.Clamp(value, -50, 100)) }, nameof(Contrast));
    }
}

public sealed class ThresholdPanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<ThresholdAdjustment>(document, layer)
{
    public int[]? Histogram => HistogramFor(0);

    public double Level
    {
        get => Settings.Level;
        set => Set("Threshold Level", new ThresholdAdjustment((int)Math.Round(Math.Clamp(value, 1, 255))), nameof(Level));
    }
}

public sealed class PosterizePanel(DocumentViewModel document, AdjustmentLayer layer) : AdjustmentPanel<PosterizeAdjustment>(document, layer)
{
    public double Levels
    {
        get => Settings.Levels;
        set => Set("Posterize Levels", new PosterizeAdjustment((int)Math.Round(Math.Clamp(value, 2, 255))), nameof(Levels));
    }
}
