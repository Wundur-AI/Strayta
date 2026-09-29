using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>One stop on the Gradient Editor's bar: a color stop (below the bar) or an opacity stop (above it).</summary>
public sealed partial class GradientStopViewModel : ObservableObject
{
    public GradientStopViewModel(bool isColor) => IsColor = isColor;

    public bool IsColor { get; }

    /// <summary>0..1 along the bar.</summary>
    [ObservableProperty] public partial float Location { get; set; }

    /// <summary>Where the blend toward the next stop is half way, 0..1 of the way there (the diamond).</summary>
    [ObservableProperty] public partial float Midpoint { get; set; } = 0.5f;

    [ObservableProperty] public partial Color Color { get; set; } = Colors.Black;
    [ObservableProperty] public partial GradientStopKind Kind { get; set; }

    /// <summary>0..1, for opacity stops.</summary>
    [ObservableProperty] public partial float Opacity { get; set; } = 1f;
}

/// <summary>A preset in a gradient or pattern grid: its picture and what it stands for.</summary>
public sealed record PresetTile(string Name, Bitmap Image, object Value, bool IsUser);

/// <summary>
/// Photoshop's Gradient Editor: presets, name, Gradient Type (Solid or Noise), Smoothness, and the bar with its color
/// and opacity stops (click below or above the bar to add one, drag to move, drag away to delete, diamonds set the
/// midpoints). <see cref="Gradient"/> is the result as the Core type the Gradient tool and layer effects use.
/// </summary>
public sealed partial class GradientEditorViewModel : ObservableObject
{
    private bool _loading;

    public GradientEditorViewModel(Gradient initial, RgbColor foreground, RgbColor background)
    {
        Foreground = foreground;
        Background = background;
        UserPresets.Shared.Changed += RefreshPresets;
        RefreshPresets();
        Load(initial);
    }

    public RgbColor Foreground { get; }
    public RgbColor Background { get; }

    public ObservableCollection<GradientStopViewModel> ColorStops { get; } = [];
    public ObservableCollection<GradientStopViewModel> OpacityStops { get; } = [];
    public ObservableCollection<PresetTile> Presets { get; } = [];

    [ObservableProperty] public partial string Name { get; set; } = "Custom";

    /// <summary>0: Solid, 1: Noise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSolid), nameof(IsNoise))]
    public partial int TypeIndex { get; set; }

    public bool IsSolid => TypeIndex == 0;
    public bool IsNoise => TypeIndex == 1;
    public IReadOnlyList<string> TypeNames { get; } = ["Solid", "Noise"];

    /// <summary>Percent, 0..100.</summary>
    [ObservableProperty] public partial double Smoothness { get; set; } = 100;
    [ObservableProperty] public partial double Roughness { get; set; } = 50;
    [ObservableProperty] public partial int NoiseModelIndex { get; set; }
    public IReadOnlyList<string> NoiseModelNames { get; } = ["RGB", "HSB"];
    [ObservableProperty] public partial bool RestrictColors { get; set; }
    [ObservableProperty] public partial bool AddTransparency { get; set; }
    [ObservableProperty] public partial int Seed { get; set; } = 1;

    /// <summary>The stop being edited; the stop fields below the bar show it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedIsColor), nameof(SelectedIsOpacity), nameof(StopLocation), nameof(StopColor),
        nameof(StopOpacity), nameof(StopKindIndex), nameof(CanDeleteStop))]
    public partial GradientStopViewModel? SelectedStop { get; set; }

    /// <summary>The midpoint diamond after <see cref="SelectedStop"/> is selected rather than the stop: Location edits the midpoint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StopLocation))]
    public partial bool MidpointSelected { get; set; }

    public bool HasSelection => SelectedStop is not null;
    public bool SelectedIsColor => SelectedStop?.IsColor == true;
    public bool SelectedIsOpacity => SelectedStop is { IsColor: false };
    public bool CanDeleteStop => SelectedStop is { } s && (s.IsColor ? ColorStops : OpacityStops).Count > 2;

    /// <summary>The Location field, in percent: the stop's place, or the selected midpoint's.</summary>
    public double StopLocation
    {
        get => SelectedStop is not { } s ? 0 : Math.Round((MidpointSelected ? s.Midpoint : s.Location) * 100, 1);
        set
        {
            if (SelectedStop is not { } s) return;
            if (MidpointSelected) SetMidpoint(s, (float)(value / 100));
            else MoveStop(s, (float)(value / 100));
            OnPropertyChanged();
        }
    }

    public Color StopColor
    {
        get => SelectedStop?.Color ?? Colors.Black;
        set
        {
            if (SelectedStop is not { IsColor: true } s || s.Color == value) return;
            s.Color = value;
            s.Kind = GradientStopKind.User;
            OnPropertyChanged(nameof(StopKindIndex));
            Changed();
        }
    }

    /// <summary>Percent.</summary>
    public double StopOpacity
    {
        get => Math.Round((SelectedStop?.Opacity ?? 1) * 100);
        set
        {
            if (SelectedStop is not { IsColor: false } s) return;
            s.Opacity = (float)Math.Clamp(value / 100, 0, 1);
            Changed();
        }
    }

    /// <summary>0: the stop's own color; 1: Foreground; 2: Background.</summary>
    public int StopKindIndex
    {
        get => (int)(SelectedStop?.Kind ?? GradientStopKind.User);
        set
        {
            if (SelectedStop is not { IsColor: true } s || value < 0) return;
            s.Kind = (GradientStopKind)value;
            if (s.Kind == GradientStopKind.Foreground) s.Color = ToColor(Foreground);
            if (s.Kind == GradientStopKind.Background) s.Color = ToColor(Background);
            OnPropertyChanged(nameof(StopColor));
            Changed();
        }
    }

    public IReadOnlyList<string> StopKindNames { get; } = ["User Color", "Foreground", "Background"];

    /// <summary>The bar's picture (and the result), redrawn after every change.</summary>
    [ObservableProperty] public partial Bitmap? BarImage { get; private set; }

    /// <summary>Raised after any change to the gradient (the bar control redraws its markers).</summary>
    public event Action? GradientChanged;

    partial void OnSmoothnessChanged(double value) => Changed();
    partial void OnTypeIndexChanged(int value) => Changed();
    partial void OnRoughnessChanged(double value) => Changed();
    partial void OnNoiseModelIndexChanged(int value) => Changed();
    partial void OnRestrictColorsChanged(bool value) => Changed();
    partial void OnAddTransparencyChanged(bool value) => Changed();
    partial void OnSeedChanged(int value) => Changed();

    /// <summary>The gradient as edited (stops sorted, foreground/background stops resolved to today's colors).</summary>
    public Gradient Gradient
    {
        get
        {
            var colors = ColorStops.OrderBy(s => s.Location).Select(s => new GradientColorStop(s.Location, s.Midpoint, FromColor(s.Color)) { Kind = s.Kind }).ToList();
            var opacities = OpacityStops.OrderBy(s => s.Location).Select(s => new GradientOpacityStop(s.Location, s.Midpoint, s.Opacity)).ToList();
            return new Gradient(colors, opacities)
            {
                Name = string.IsNullOrWhiteSpace(Name) ? "Custom" : Name.Trim(),
                Smoothness = (float)Math.Clamp(Smoothness / 100, 0, 1),
                Noise = IsNoise ? new GradientNoise
                {
                    Roughness = (float)Math.Clamp(Roughness / 100, 0, 1),
                    Seed = Seed,
                    Model = (NoiseColorModel)NoiseModelIndex,
                    RestrictColors = RestrictColors,
                    AddTransparency = AddTransparency,
                } : null,
            };
        }
    }

    /// <summary>Shows <paramref name="g"/> in the editor (a preset click, or the gradient the dialog opened with).</summary>
    public void Load(Gradient g)
    {
        _loading = true;
        try
        {
            var resolved = g.Resolve(Foreground, Background);
            Name = g.Name;
            ColorStops.Clear();
            OpacityStops.Clear();
            foreach (var c in resolved.Colors)
                ColorStops.Add(new GradientStopViewModel(true) { Location = c.Location, Midpoint = c.Midpoint, Color = ToColor(c.Color), Kind = c.Kind });
            foreach (var o in resolved.Opacities)
                OpacityStops.Add(new GradientStopViewModel(false) { Location = o.Location, Midpoint = o.Midpoint, Opacity = o.Opacity });
            if (ColorStops.Count == 0)
            {
                ColorStops.Add(new GradientStopViewModel(true) { Location = 0, Color = Colors.Black });
                ColorStops.Add(new GradientStopViewModel(true) { Location = 1, Color = Colors.White });
            }
            if (OpacityStops.Count == 0)
            {
                OpacityStops.Add(new GradientStopViewModel(false) { Location = 0 });
                OpacityStops.Add(new GradientStopViewModel(false) { Location = 1 });
            }
            Smoothness = Math.Round(g.Smoothness * 100);
            TypeIndex = g.Noise is null ? 0 : 1;
            if (g.Noise is { } n)
            {
                Roughness = Math.Round(n.Roughness * 100);
                Seed = n.Seed;
                NoiseModelIndex = (int)n.Model;
                RestrictColors = n.RestrictColors;
                AddTransparency = n.AddTransparency;
            }
            SelectedStop = ColorStops[0];
            MidpointSelected = false;
        }
        finally
        {
            _loading = false;
        }
        Changed();
    }

    // ---- Stops (the bar control calls these) ----------------------------------------------------------

    /// <summary>A click below (color) or above (opacity) the bar: a new stop there, with the gradient's color or opacity at that point.</summary>
    public GradientStopViewModel AddStop(bool color, float location)
    {
        location = Math.Clamp(location, 0f, 1f);
        var (r, g, b, a) = GradientLut.Build(Gradient.Resolve(Foreground, Background) with { Noise = null }, GradientMethod.Perceptual).At(location);
        var stop = new GradientStopViewModel(color) { Location = location, Color = Color.FromRgb(B(r), B(g), B(b)), Opacity = a };
        (color ? ColorStops : OpacityStops).Add(stop);
        SelectedStop = stop;
        MidpointSelected = false;
        Changed();
        return stop;
    }

    public void MoveStop(GradientStopViewModel stop, float location)
    {
        stop.Location = (float)Math.Round(Math.Clamp(location, 0f, 1f), 3);
        OnPropertyChanged(nameof(StopLocation));
        Changed();
    }

    /// <summary>Sets the midpoint of the segment after <paramref name="stop"/>; Photoshop keeps it within 5–95%.</summary>
    public void SetMidpoint(GradientStopViewModel stop, float midpoint)
    {
        stop.Midpoint = (float)Math.Round(Math.Clamp(midpoint, 0.05f, 0.95f), 3);
        OnPropertyChanged(nameof(StopLocation));
        Changed();
    }

    /// <summary>Deletes a stop (dragged off the bar, or the Delete button); a row keeps at least two.</summary>
    public bool RemoveStop(GradientStopViewModel stop)
    {
        var row = stop.IsColor ? ColorStops : OpacityStops;
        if (row.Count <= 2 || !row.Remove(stop)) return false;
        if (ReferenceEquals(SelectedStop, stop)) SelectedStop = row.OrderBy(s => s.Location).FirstOrDefault();
        MidpointSelected = false;
        Changed();
        return true;
    }

    /// <summary>The stop after <paramref name="stop"/> in its row, by location (its midpoint lies between them).</summary>
    public GradientStopViewModel? Next(GradientStopViewModel stop)
    {
        var ordered = Ordered(stop.IsColor);
        int i = ordered.IndexOf(stop);
        return i >= 0 && i + 1 < ordered.Count ? ordered[i + 1] : null;
    }

    /// <summary>A row's stops by location (ties keep the order they were added in, as the evaluator's stable sort does).</summary>
    public List<GradientStopViewModel> Ordered(bool color) => (color ? ColorStops : OpacityStops).OrderBy(s => s.Location).ToList();

    [RelayCommand]
    private void DeleteStop()
    {
        if (SelectedStop is { } s) RemoveStop(s);
    }

    [RelayCommand]
    private void Randomize() => Seed = Random.Shared.Next(1, int.MaxValue);

    // ---- Presets ---------------------------------------------------------------------------------------

    /// <summary>New (Photoshop's button): saves the gradient as it is as a user preset under its name.</summary>
    [RelayCommand]
    private void NewPreset()
    {
        var g = Gradient;
        // Keep foreground/background stops as such in the preset, so it follows the colors later.
        UserPresets.Shared.AddGradient(g);
    }

    [RelayCommand]
    private void DeletePreset(PresetTile? tile)
    {
        if (tile is { IsUser: true, Value: Gradient g }) UserPresets.Shared.RemoveGradient(g);
    }

    [RelayCommand]
    private void UsePreset(PresetTile? tile)
    {
        if (tile?.Value is Gradient g) Load(g);
    }

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var g in GradientPresets.All)
            Presets.Add(new PresetTile(g.Name, GradientImages.Strip(g.Resolve(Foreground, Background), 44, 44), g, GradientPresets.IsUserPreset(g)));
    }

    /// <summary>Stops listening to the shared presets (the dialog closed).</summary>
    public void Detach() => UserPresets.Shared.Changed -= RefreshPresets;

    private void Changed()
    {
        if (_loading) return;
        OnPropertyChanged(nameof(CanDeleteStop));
        BarImage = GradientImages.Strip(Gradient.Resolve(Foreground, Background), 400, 1);
        GradientChanged?.Invoke();
    }

    public static Color ToColor(RgbColor c) => Color.FromRgb(B(c.R), B(c.G), B(c.B));
    public static RgbColor FromColor(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);
    private static byte B(float v) => (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
}
