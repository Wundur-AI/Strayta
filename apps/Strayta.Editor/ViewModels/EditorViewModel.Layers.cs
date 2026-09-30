using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core;

namespace Strayta.Editor.ViewModels;

/// <summary>The Layers panel's thumbnail size (panel options).</summary>
public enum LayerThumbnailSize
{
    None,
    Small,
    Medium,
    Large,
}

// The layer workflow: Move tool options (Auto-Select, Show Transform Controls, Align and Distribute), the commands of
// Layer › and Select › that act on the selected layers, and the Layers panel's options and filter.
public sealed partial class EditorViewModel
{
    // ---- Move tool options ----------------------------------------------------------------------------

    /// <summary>Auto-Select: a click with the Move tool selects the layer under the pointer (⌘ flips it for one click).</summary>
    [ObservableProperty] public partial bool MoveAutoSelect { get; set; }

    /// <summary>Auto-Select picks the outermost group rather than the layer (Photoshop's "Group" choice).</summary>
    [ObservableProperty] public partial bool MoveAutoSelectGroups { get; set; }

    /// <summary>Show Transform Controls: the Move tool draws the selected layers' box with handles; dragging one starts Free Transform.</summary>
    [ObservableProperty] public partial bool MoveShowTransformControls { get; set; }

    public int MoveAutoSelectTargetIndex
    {
        get => MoveAutoSelectGroups ? 1 : 0;
        set => MoveAutoSelectGroups = value == 1;
    }

    public static IReadOnlyList<string> MoveAutoSelectTargets { get; } = ["Layer", "Group"];

    partial void OnMoveAutoSelectGroupsChanged(bool value) => OnPropertyChanged(nameof(MoveAutoSelectTargetIndex));

    // ---- Layers panel options ------------------------------------------------------------------------

    [ObservableProperty] public partial LayerThumbnailSize LayerThumbnailSize { get; set; } = LayerThumbnailSize.Medium;

    partial void OnLayerThumbnailSizeChanged(LayerThumbnailSize value)
    {
        foreach (var doc in Factory.OpenDocuments())
            foreach (var item in doc.Layers.SelectMany(l => l.SelfAndDescendants())) item.RefreshPanelLayout();
    }

    /// <summary>The filter bar at the top of the Layers panel.</summary>
    public LayerFilter LayerFilter => _layerFilter ??= CreateLayerFilter();

    private LayerFilter? _layerFilter;

    private LayerFilter CreateLayerFilter()
    {
        var filter = new LayerFilter();
        filter.Changed += OnLayerFilterChanged;
        return filter;
    }

    private void OnLayerFilterChanged()
    {
        foreach (var doc in Factory.OpenDocuments()) doc.RefreshRows();
    }

    // ---- Commands --------------------------------------------------------------------------------------

    [RelayCommand] private void GroupLayers() => ActiveDocument?.GroupSelectedLayers();
    [RelayCommand] private void UngroupLayers() => ActiveDocument?.UngroupSelectedLayers();
    [RelayCommand] private Task MergeDown() => ActiveDocument?.MergeDownAsync() ?? Task.CompletedTask;
    [RelayCommand] private Task MergeVisible() => ActiveDocument?.MergeVisibleAsync() ?? Task.CompletedTask;
    [RelayCommand] private Task FlattenImage() => ActiveDocument?.FlattenImageAsync() ?? Task.CompletedTask;
    [RelayCommand] private Task StampVisible() => ActiveDocument?.StampVisibleAsync() ?? Task.CompletedTask;
    [RelayCommand] private void SelectAllLayers() => ActiveDocument?.SelectAllLayers();
    [RelayCommand] private void DeselectLayers() => ActiveDocument?.DeselectLayers();
    [RelayCommand] private void SelectSimilarLayers() => ActiveDocument?.SelectSimilarLayers();
    [RelayCommand] private void SelectLinkedLayers() => ActiveDocument?.SelectLinkedLayers();
    [RelayCommand] private void LinkLayers() => ActiveDocument?.LinkSelectedLayers();
    [RelayCommand] private void UnlinkLayers() => ActiveDocument?.UnlinkSelectedLayers();
    [RelayCommand] private void HideLayers() => ActiveDocument?.ToggleSelectedVisibility();
    [RelayCommand] private void DeleteHiddenLayers() => ActiveDocument?.DeleteHiddenLayers();
    [RelayCommand] private void CollapseAllGroups() => ActiveDocument?.CollapseAllGroups();
    [RelayCommand] private void SetLayerThumbnailSize(string size) => LayerThumbnailSize = Enum.Parse<LayerThumbnailSize>(size);

    /// <summary>Layer › Align (and the Move tool's buttons): "Left", "HorizontalCenter", ... (<see cref="AlignEdge"/>).</summary>
    [RelayCommand] private void AlignLayers(string edge) => ActiveDocument?.AlignLayers(Enum.Parse<AlignEdge>(edge));

    /// <summary>Layer › Distribute: <see cref="DistributeMode"/> names.</summary>
    [RelayCommand] private void DistributeLayers(string mode) => ActiveDocument?.DistributeLayers(Enum.Parse<DistributeMode>(mode));

    /// <summary>A color label for the selected layers (<see cref="LayerColor"/> names).</summary>
    [RelayCommand] private void SetLayerColor(string color) => ActiveDocument?.SetColorLabel(Enum.Parse<LayerColor>(color));

    /// <summary>Layer › Lock Layers: toggles a lock ("Transparency", "Pixels", "Position", "All") on the selected layers.</summary>
    [RelayCommand]
    private void ToggleLayerLock(string kind)
    {
        if (ActiveDocument is not { SelectedLayer.Node: { } primary } doc) return;
        var k = Enum.Parse<LayerLocks>(kind);
        doc.SetLock(k, (primary.Locks & k) == 0);
    }
}

/// <summary>
/// The Layers panel's filter bar, as in Photoshop: by kind (pixel, adjustment, type, shape, smart object), by name, by
/// effect, by blend mode or by color label, with a switch to turn it off without losing the settings.
/// </summary>
public sealed partial class LayerFilter : ObservableObject
{
    public static IReadOnlyList<string> FilterTypes { get; } = ["Kind", "Name", "Effect", "Mode", "Color", "Selected"];

    public static IReadOnlyList<string> EffectNames { get; } =
        ["Bevel & Emboss", "Stroke", "Inner Shadow", "Inner Glow", "Satin", "Color Overlay", "Gradient Overlay", "Pattern Overlay", "Outer Glow", "Drop Shadow"];

    public static IReadOnlyList<BlendMode> Modes { get; } = Enum.GetValues<BlendMode>();

    public static IReadOnlyList<LayerColor> Colors { get; } = Enum.GetValues<LayerColor>();

    public event Action? Changed;

    /// <summary>The switch at the right of the filter bar.</summary>
    [ObservableProperty] public partial bool Enabled { get; set; } = true;

    [ObservableProperty] public partial int FilterTypeIndex { get; set; }

    [ObservableProperty] public partial bool Pixel { get; set; }
    [ObservableProperty] public partial bool Adjustment { get; set; }
    [ObservableProperty] public partial bool Type { get; set; }
    [ObservableProperty] public partial bool Shape { get; set; }
    [ObservableProperty] public partial bool SmartObject { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial int EffectIndex { get; set; }
    [ObservableProperty] public partial BlendMode Mode { get; set; } = BlendMode.Normal;
    [ObservableProperty] public partial LayerColor Color { get; set; } = LayerColor.Red;

    public bool ByKind => FilterTypeIndex == 0;
    public bool ByName => FilterTypeIndex == 1;
    public bool ByEffect => FilterTypeIndex == 2;
    public bool ByMode => FilterTypeIndex == 3;
    public bool ByColor => FilterTypeIndex == 4;

    /// <summary>True when the filter hides anything: switched on, with something to filter by.</summary>
    public bool IsActive => Enabled && FilterTypeIndex switch
    {
        0 => Pixel || Adjustment || Type || Shape || SmartObject,
        1 => Name.Length > 0,
        _ => true,
    };

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(FilterTypeIndex))
            foreach (var n in new[] { nameof(ByKind), nameof(ByName), nameof(ByEffect), nameof(ByMode), nameof(ByColor) }) base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(n));
        if (e.PropertyName is not (nameof(ByKind) or nameof(ByName) or nameof(ByEffect) or nameof(ByMode) or nameof(ByColor) or nameof(IsActive)))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsActive)));
            Changed?.Invoke();
        }
    }

    /// <summary>Whether <paramref name="node"/> passes the filter (<paramref name="selected"/>: the selected layers, for "Selected").</summary>
    public bool Matches(LayerNode node, IReadOnlyCollection<LayerNode> selected) => FilterTypeIndex switch
    {
        0 => DocumentViewModel.KindOf(node) switch
        {
            "pixel" => Pixel,
            "adjustment" => Adjustment,
            "type" => Type,
            "shape" => Shape,
            "smart" => SmartObject,
            _ => false, // groups are not a kind of their own
        },
        1 => node.Name.Contains(Name, StringComparison.OrdinalIgnoreCase),
        2 => node.Effects?.Items.Any(e => EffectName(e) == EffectNames[Math.Clamp(EffectIndex, 0, EffectNames.Count - 1)]) == true,
        3 => node.BlendMode == Mode,
        4 => node.Color == Color,
        5 => selected.Contains(node),
        _ => true,
    };

    private static string EffectName(LayerEffect effect) => effect switch
    {
        BevelEffect => "Bevel & Emboss",
        StrokeEffect => "Stroke",
        InnerShadowEffect => "Inner Shadow",
        InnerGlowEffect => "Inner Glow",
        SatinEffect => "Satin",
        ColorOverlayEffect => "Color Overlay",
        GradientOverlayEffect => "Gradient Overlay",
        PatternOverlayEffect => "Pattern Overlay",
        OuterGlowEffect => "Outer Glow",
        DropShadowEffect => "Drop Shadow",
        _ => "",
    };
}
