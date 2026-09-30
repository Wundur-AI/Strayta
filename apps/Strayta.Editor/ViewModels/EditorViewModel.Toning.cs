using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core.Painting;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

// Toning tools (Dodge, Burn, Sponge: the O slot) and focus tools (Blur, Sharpen, Smudge: the slot above it, which has no
// shortcut in Photoshop). Options are app-wide and kept per tool, as in Photoshop; the brush (size, hardness, tip,
// spacing, pressure) is the Brush's. The strokes are made in DocumentViewModel.Toning.cs.
public sealed partial class EditorViewModel
{
    private (ToolGroup Focus, ToolGroup Toning) CreateToningToolGroups()
    {
        // Photoshop's slots: Blur / Sharpen / Smudge (no key), then Dodge / Burn / Sponge (O), after Gradient.
        var focus = new ToolGroup(this, "",
            new(CanvasTool.Blur, "Blur", "IconBlurTool"),
            new(CanvasTool.Sharpen, "Sharpen", "IconSharpenTool"),
            new(CanvasTool.Smudge, "Smudge", "IconSmudgeTool"));
        var toning = new ToolGroup(this, "O",
            new(CanvasTool.Dodge, "Dodge", "IconDodgeTool"),
            new(CanvasTool.Burn, "Burn", "IconBurnTool"),
            new(CanvasTool.Sponge, "Sponge", "IconSpongeTool"));
        return (focus, toning);
    }

    public bool IsDodgeTool => Tool == CanvasTool.Dodge;
    public bool IsBurnTool => Tool == CanvasTool.Burn;
    public bool IsDodgeOrBurnTool => Tool is CanvasTool.Dodge or CanvasTool.Burn;
    public bool IsSpongeTool => Tool == CanvasTool.Sponge;
    public bool IsSharpenTool => Tool == CanvasTool.Sharpen;
    public bool IsSmudgeTool => Tool == CanvasTool.Smudge;

    /// <summary>Dodge, Burn, Sponge.</summary>
    public bool IsToningTool => Tool is CanvasTool.Dodge or CanvasTool.Burn or CanvasTool.Sponge;

    /// <summary>Blur, Sharpen, Smudge.</summary>
    public bool IsFocusTool => Tool is CanvasTool.Blur or CanvasTool.Sharpen or CanvasTool.Smudge;

    /// <summary>Any of the six (they share the options bar in ToningToolOptions).</summary>
    public bool IsToneOrFocusTool => IsToningTool || IsFocusTool;

    private void NotifyToningTools()
    {
        foreach (var name in new[]
                 {
                     nameof(IsDodgeTool), nameof(IsBurnTool), nameof(IsDodgeOrBurnTool), nameof(IsSpongeTool), nameof(IsSharpenTool),
                     nameof(IsSmudgeTool), nameof(IsToningTool), nameof(IsFocusTool), nameof(IsToneOrFocusTool),
                     nameof(ToneRangeIndex), nameof(ToneExposure), nameof(ToneProtectTones), nameof(FocusModeIndex), nameof(FocusStrength),
                     nameof(FocusSampleAllLayers), nameof(ToneAirbrush), nameof(ShowsBrushPresetPicker),
                 })
            OnPropertyChanged(name);
    }

    /// <summary>The Airbrush option of the current tool (the toning tools have their own; the focus and healing tools have none).</summary>
    public bool StrokeAirbrush => Tool switch
    {
        CanvasTool.Dodge or CanvasTool.Burn or CanvasTool.Sponge => ToneAirbrush,
        CanvasTool.Healing or CanvasTool.SpotHealing or CanvasTool.Blur or CanvasTool.Sharpen or CanvasTool.Smudge => false,
        _ => BrushAirbrush,
    };

    // ---- Dodge and Burn ------------------------------------------------------------------------------------

    public IReadOnlyList<string> ToneRangeNames { get; } = ["Shadows", "Midtones", "Highlights"];

    [ObservableProperty] public partial int DodgeRangeIndex { get; set; } = 1;
    [ObservableProperty] public partial int BurnRangeIndex { get; set; } = 1;

    /// <summary>Exposure in percent (Photoshop's default is 50%).</summary>
    [ObservableProperty] public partial double DodgeExposure { get; set; } = 50;
    [ObservableProperty] public partial double BurnExposure { get; set; } = 50;

    [ObservableProperty] public partial bool DodgeProtectTones { get; set; } = true;
    [ObservableProperty] public partial bool BurnProtectTones { get; set; } = true;

    /// <summary>The current tool's Range (Dodge or Burn).</summary>
    public int ToneRangeIndex
    {
        get => IsBurnTool ? BurnRangeIndex : DodgeRangeIndex;
        set
        {
            if (IsBurnTool) BurnRangeIndex = value;
            else DodgeRangeIndex = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The current tool's Exposure (Dodge or Burn).</summary>
    public double ToneExposure
    {
        get => IsBurnTool ? BurnExposure : DodgeExposure;
        set
        {
            if (IsBurnTool) BurnExposure = value;
            else DodgeExposure = value;
            OnPropertyChanged();
        }
    }

    public bool ToneProtectTones
    {
        get => IsBurnTool ? BurnProtectTones : DodgeProtectTones;
        set
        {
            if (IsBurnTool) BurnProtectTones = value;
            else DodgeProtectTones = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Airbrush for the toning tools: the effect keeps building (up to the exposure) while the pointer rests.</summary>
    [ObservableProperty] public partial bool ToneAirbrush { get; set; }

    // ---- Sponge --------------------------------------------------------------------------------------------

    public IReadOnlyList<string> SpongeModeNames { get; } = ["Desaturate", "Saturate"];

    [ObservableProperty] public partial int SpongeModeIndex { get; set; }

    /// <summary>Sponge Flow in percent: how much each dab changes the saturation.</summary>
    [ObservableProperty] public partial double SpongeFlow { get; set; } = 50;

    [ObservableProperty] public partial bool SpongeVibrance { get; set; } = true;

    // ---- Blur, Sharpen, Smudge -----------------------------------------------------------------------------

    /// <summary>The focus tools' Mode menu, in Photoshop's order.</summary>
    public IReadOnlyList<string> FocusModeNames { get; } = ["Normal", "Darken", "Lighten", "Hue", "Saturation", "Color", "Luminosity"];

    private static readonly PaintMode[] FocusModes =
        [PaintMode.Normal, PaintMode.Darken, PaintMode.Lighten, PaintMode.Hue, PaintMode.Saturation, PaintMode.Color, PaintMode.Luminosity];

    [ObservableProperty] public partial int BlurModeIndex { get; set; }
    [ObservableProperty] public partial int SharpenModeIndex { get; set; }
    [ObservableProperty] public partial int SmudgeModeIndex { get; set; }
    [ObservableProperty] public partial double BlurStrength { get; set; } = 50;
    [ObservableProperty] public partial double SharpenStrength { get; set; } = 50;
    [ObservableProperty] public partial double SmudgeStrength { get; set; } = 50;
    [ObservableProperty] public partial bool BlurSampleAllLayers { get; set; }
    [ObservableProperty] public partial bool SharpenSampleAllLayers { get; set; }
    [ObservableProperty] public partial bool SmudgeSampleAllLayers { get; set; }

    [ObservableProperty] public partial bool SharpenProtectDetail { get; set; } = true;
    [ObservableProperty] public partial bool SmudgeFingerPainting { get; set; }

    public int FocusModeIndex
    {
        get => Tool switch { CanvasTool.Sharpen => SharpenModeIndex, CanvasTool.Smudge => SmudgeModeIndex, _ => BlurModeIndex };
        set
        {
            switch (Tool)
            {
                case CanvasTool.Sharpen: SharpenModeIndex = value; break;
                case CanvasTool.Smudge: SmudgeModeIndex = value; break;
                default: BlurModeIndex = value; break;
            }
            OnPropertyChanged();
        }
    }

    public double FocusStrength
    {
        get => Tool switch { CanvasTool.Sharpen => SharpenStrength, CanvasTool.Smudge => SmudgeStrength, _ => BlurStrength };
        set
        {
            switch (Tool)
            {
                case CanvasTool.Sharpen: SharpenStrength = value; break;
                case CanvasTool.Smudge: SmudgeStrength = value; break;
                default: BlurStrength = value; break;
            }
            OnPropertyChanged();
        }
    }

    public bool FocusSampleAllLayers
    {
        get => Tool switch { CanvasTool.Sharpen => SharpenSampleAllLayers, CanvasTool.Smudge => SmudgeSampleAllLayers, _ => BlurSampleAllLayers };
        set
        {
            switch (Tool)
            {
                case CanvasTool.Sharpen: SharpenSampleAllLayers = value; break;
                case CanvasTool.Smudge: SmudgeSampleAllLayers = value; break;
                default: BlurSampleAllLayers = value; break;
            }
            OnPropertyChanged();
        }
    }

    // ---- What strokes use ------------------------------------------------------------------------------------

    /// <summary>The current toning tool's settings.</summary>
    public ToneSettings CurrentToneSettings => Tool switch
    {
        CanvasTool.Sponge => new ToneSettings(ToneTool.Sponge) { Saturate = SpongeModeIndex == 1, Vibrance = SpongeVibrance },
        CanvasTool.Burn => new ToneSettings(ToneTool.Burn) { Range = (ToneRange)Math.Clamp(BurnRangeIndex, 0, 2), ProtectTones = BurnProtectTones },
        _ => new ToneSettings(ToneTool.Dodge) { Range = (ToneRange)Math.Clamp(DodgeRangeIndex, 0, 2), ProtectTones = DodgeProtectTones },
    };

    /// <summary>
    /// The brush a toning stroke paints with: Dodge and Burn build up to their Exposure (each dab moves toward it at
    /// full flow, a slower flow with the airbrush so holding still keeps adding); the Sponge builds up by its Flow.
    /// </summary>
    public BrushSettings CurrentToneBrush
    {
        get
        {
            var brush = CurrentBrush with { Mode = PaintMode.Normal };
            return Tool == CanvasTool.Sponge
                ? brush with { Opacity = 1f, Flow = (float)Math.Clamp(SpongeFlow / 100, 0.01, 1) }
                : brush with { Opacity = (float)Math.Clamp(ToneExposure / 100, 0.01, 1), Flow = ToneAirbrush ? 0.15f : 1f };
        }
    }

    /// <summary>The current focus tool's settings.</summary>
    public LocalToolSettings CurrentLocalSettings
    {
        get
        {
            var tool = Tool switch { CanvasTool.Sharpen => LocalTool.Sharpen, CanvasTool.Smudge => LocalTool.Smudge, _ => LocalTool.Blur };
            return new LocalToolSettings(tool)
            {
                Strength = (float)Math.Clamp(FocusStrength / 100, 0, 1),
                Mode = FocusModes[Math.Clamp(FocusModeIndex, 0, FocusModes.Length - 1)],
                ProtectDetail = SharpenProtectDetail,
                FingerPainting = SmudgeFingerPainting,
                FingerColor = CurrentColor,
            };
        }
    }
}
