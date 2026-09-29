using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

// View › Rulers, Show › Guides / Grid, Snap and Snap To, Lock / Clear / New Guide, New Guide Layout, the grid settings and
// the Info panel's readout. The switches are the person's (saved in view.json, see ViewSettings); the guides themselves
// belong to each document (DocumentViewModel.Guides.cs).
public sealed partial class EditorViewModel
{
    [ObservableProperty] public partial bool ShowRulers { get; set; } = ViewSettings.Loaded.ShowRulers;
    [ObservableProperty] public partial bool ShowGuides { get; set; } = ViewSettings.Loaded.ShowGuides;
    [ObservableProperty] public partial bool ShowGrid { get; set; } = ViewSettings.Loaded.ShowGrid;
    [ObservableProperty] public partial bool LockGuides { get; set; } = ViewSettings.Loaded.LockGuides;

    /// <summary>View › Snap (⇧⌘;): drags snap to what <see cref="SnapTo"/> lists.</summary>
    [ObservableProperty] public partial bool SnapEnabled { get; set; } = ViewSettings.Loaded.Snap;

    /// <summary>View › Snap To.</summary>
    [ObservableProperty] public partial SnapTargets SnapTo { get; set; } = ViewSettings.Loaded.SnapTo;

    /// <summary>The rulers' and Info panel's unit (right-click a ruler).</summary>
    [ObservableProperty] public partial RulerUnit RulerUnit { get; set; } = ViewSettings.Loaded.RulerUnit;

    /// <summary>Gridline every <see cref="GridSpacing"/> <see cref="GridUnit"/>, with <see cref="GridSubdivisions"/> subdivisions.</summary>
    [ObservableProperty] public partial double GridSpacing { get; set; } = ViewSettings.Loaded.GridSpacing;
    [ObservableProperty] public partial RulerUnit GridUnit { get; set; } = ViewSettings.Loaded.GridUnit;
    [ObservableProperty] public partial int GridSubdivisions { get; set; } = ViewSettings.Loaded.GridSubdivisions;

    /// <summary>What the Info panel shows (pointer position, color, sizes), kept by the active document.</summary>
    public InfoReadout Info { get; } = new();

    /// <summary>View › New Guide… (set by the window): the orientation and position in pixels, or null when cancelled.</summary>
    public Func<DocumentViewModel, Task<Guide?>>? AskNewGuide { get; set; }

    /// <summary>View › New Guide Layout… (set by the window): the layout and whether to clear the existing guides first.</summary>
    public Func<DocumentViewModel, Task<(GuideLayout Layout, bool ClearExisting)?>>? AskGuideLayout { get; set; }

    /// <summary>Guides &amp; Grid preferences (set by the window); the dialog changes the grid settings itself.</summary>
    public Func<Task>? AskGridSettings { get; set; }

    /// <summary>Snap To › Guides / Grid / Layers / Document Bounds is on (and its menu item is checked).</summary>
    public bool SnapsTo(SnapTargets target) => SnapTo.HasFlag(target);

    /// <summary>Raised when any rulers, guides, grid or snap setting changes (the menu's check marks follow it).</summary>
    public event Action? ViewSettingsChanged;

    partial void OnShowRulersChanged(bool value) => SaveViewSettings();
    partial void OnShowGuidesChanged(bool value) => SaveViewSettings();
    partial void OnShowGridChanged(bool value) => SaveViewSettings();
    partial void OnLockGuidesChanged(bool value) => SaveViewSettings();
    partial void OnSnapEnabledChanged(bool value) => SaveViewSettings();
    partial void OnSnapToChanged(SnapTargets value) => SaveViewSettings();
    partial void OnRulerUnitChanged(RulerUnit value) => SaveViewSettings();
    partial void OnGridSpacingChanged(double value) => SaveViewSettings();
    partial void OnGridUnitChanged(RulerUnit value) => SaveViewSettings();
    partial void OnGridSubdivisionsChanged(int value) => SaveViewSettings();

    private void SaveViewSettings()
    {
        new ViewSettings
        {
            ShowRulers = ShowRulers, ShowGuides = ShowGuides, ShowGrid = ShowGrid, LockGuides = LockGuides, Snap = SnapEnabled,
            SnapTo = SnapTo, RulerUnit = RulerUnit, GridSpacing = GridSpacing, GridUnit = GridUnit, GridSubdivisions = GridSubdivisions,
        }.Save();
        ViewSettingsChanged?.Invoke();
    }

    /// <summary>
    /// The grid's major spacing in document pixels for <paramref name="doc"/> (Percent of the width, physical units at its
    /// resolution), never below one pixel.
    /// </summary>
    public double GridSpacingPixels(Document doc) =>
        Math.Max(1, RulerUnits.ToPixels(GridSpacing, GridUnit, doc.Resolution, doc.Width));

    [RelayCommand] private void ToggleRulers() => ShowRulers = !ShowRulers;
    [RelayCommand] private void ToggleGuides() => ShowGuides = !ShowGuides;
    [RelayCommand] private void ToggleGrid() => ShowGrid = !ShowGrid;
    [RelayCommand] private void ToggleLockGuides() => LockGuides = !LockGuides;
    [RelayCommand] private void ToggleSnap() => SnapEnabled = !SnapEnabled;

    /// <summary>View › Snap To: "Guides", "Grid", "Layers", "DocumentBounds" toggle one; "All" and "None" set them all.</summary>
    [RelayCommand]
    private void ToggleSnapTo(string target)
    {
        SnapTo = target switch
        {
            "All" => SnapTargets.All,
            "None" => SnapTargets.None,
            _ when Enum.TryParse<SnapTargets>(target, out var t) => SnapTo ^ t,
            _ => SnapTo,
        };
        // Choosing a target turns snapping on, as the menu is otherwise a dead end.
        if (SnapTo != SnapTargets.None && target != "None") SnapEnabled = true;
    }

    [RelayCommand]
    private void SetRulerUnit(string unit)
    {
        if (Enum.TryParse<RulerUnit>(unit, out var u)) RulerUnit = u;
    }

    [RelayCommand] private void ClearGuides() => ActiveDocument?.ClearGuides();

    [RelayCommand]
    private async Task NewGuide()
    {
        if (ActiveDocument is not { } doc || AskNewGuide is null) return;
        if (await AskNewGuide(doc) is { } guide) doc.AddGuide(guide);
    }

    [RelayCommand]
    private async Task NewGuideLayout()
    {
        if (ActiveDocument is not { } doc || AskGuideLayout is null) return;
        if (await AskGuideLayout(doc) is { } r) doc.AddGuideLayout(r.Layout, r.ClearExisting);
    }

    [RelayCommand]
    private async Task GridSettings()
    {
        if (AskGridSettings is not null) await AskGridSettings();
    }
}

/// <summary>Window › Info (F8): where the pointer is, the color under it, the size of what is being drawn or transformed, the document size.</summary>
public sealed partial class InfoReadout : ObservableObject
{
    [ObservableProperty] public partial string X { get; set; } = "";
    [ObservableProperty] public partial string Y { get; set; } = "";
    [ObservableProperty] public partial string R { get; set; } = "";
    [ObservableProperty] public partial string G { get; set; } = "";
    [ObservableProperty] public partial string B { get; set; } = "";

    /// <summary>"8-bit" or "16-bit": the scale R, G and B are shown in (16-bit values run 0–32768, as in Photoshop).</summary>
    [ObservableProperty] public partial string Depth { get; set; } = "8-bit";
    [ObservableProperty] public partial string W { get; set; } = "";
    [ObservableProperty] public partial string H { get; set; } = "";
    [ObservableProperty] public partial string DocumentSize { get; set; } = "";
    [ObservableProperty] public partial string Unit { get; set; } = "px";
}
