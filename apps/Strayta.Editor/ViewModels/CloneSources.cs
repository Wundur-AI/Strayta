using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using Strayta.Core.Painting;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// One of the Clone Source panel's five sources in a document: where the Clone Stamp and the Healing Brush sample, and
/// how the sampled area is placed (offset, W/H scale, angle).
/// </summary>
/// <remarks>
/// The Clone Stamp and the Healing Brush each keep their own source point and aligned offset in a slot, so
/// Option-clicking with one tool does not move the other's source; both use the panel's active slot and its transform.
/// </remarks>
public sealed partial class CloneSourceSlot : ObservableObject
{
    private readonly CloneAligner _clone = new(), _heal = new();
    private bool _syncing;

    public CloneSourceSlot(int number, Func<CanvasTool> tool)
    {
        Number = number;
        _tool = tool;
    }

    private readonly Func<CanvasTool> _tool;

    /// <summary>1..5, as the panel's buttons show it.</summary>
    public int Number { get; }

    /// <summary>The source point and aligned offset of <paramref name="tool"/> (the Healing Brush's, or the Clone Stamp's for every other tool).</summary>
    internal CloneAligner AlignerFor(CanvasTool tool) => tool == CanvasTool.Healing ? _heal : _clone;

    private CloneAligner Current => AlignerFor(_tool());

    /// <summary>W in percent (negative flips).</summary>
    [ObservableProperty] public partial double WidthPercent { get; set; } = 100;

    /// <summary>H in percent (negative flips).</summary>
    [ObservableProperty] public partial double HeightPercent { get; set; } = 100;

    /// <summary>Keep H equal to W (the panel's link button).</summary>
    [ObservableProperty] public partial bool LinkSize { get; set; } = true;

    /// <summary>Rotation in degrees, counter-clockwise.</summary>
    [ObservableProperty] public partial double Angle { get; set; }

    /// <summary>Offset X shown and typed in the panel: destination − source of the current tool's aligned offset.</summary>
    [ObservableProperty] public partial double OffsetX { get; set; }

    /// <summary>Offset Y.</summary>
    [ObservableProperty] public partial double OffsetY { get; set; }

    /// <summary>The panel's description of the current tool's source point.</summary>
    public string SourceText => Current.SourcePoint is { } s ? $"Source {s.X}, {s.Y}" : "No source set";

    public bool HasSource => Current.SourcePoint is not null;

    /// <summary>The panel's button icon is dimmed until the slot has a source.</summary>
    public double IconOpacity => HasSource ? 1 : 0.4;

    /// <summary>The placement of the sampled area.</summary>
    public SourceTransform Transform => new((float)(WidthPercent / 100), (float)(HeightPercent / 100), (float)Angle);

    partial void OnWidthPercentChanged(double value)
    {
        if (LinkSize && !_syncing && Math.Abs(HeightPercent) != Math.Abs(value))
            HeightPercent = Math.CopySign(Math.Abs(value), HeightPercent);
    }

    partial void OnHeightPercentChanged(double value)
    {
        if (LinkSize && !_syncing && Math.Abs(WidthPercent) != Math.Abs(value))
            WidthPercent = Math.CopySign(Math.Abs(value), WidthPercent);
    }

    partial void OnOffsetXChanged(double value) => PushOffset();
    partial void OnOffsetYChanged(double value) => PushOffset();

    private void PushOffset()
    {
        if (_syncing) return;
        Current.SetOffset((int)Math.Round(OffsetX), (int)Math.Round(OffsetY));
    }

    /// <summary>Re-reads the current tool's source point and offset (after an Option-click, a stroke, or a tool change).</summary>
    internal void Refresh()
    {
        _syncing = true;
        try
        {
            var offset = Current.Offset ?? (0, 0);
            OffsetX = offset.Dx;
            OffsetY = offset.Dy;
        }
        finally
        {
            _syncing = false;
        }
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(IconOpacity));
    }

    /// <summary>The panel's reset button: 100%, 0°.</summary>
    [RelayCommand]
    private void ResetTransform()
    {
        _syncing = true;
        (WidthPercent, HeightPercent, Angle) = (100, 100, 0);
        _syncing = false;
    }

    /// <summary>Flip horizontally / vertically (negates W or H), as the panel's flip buttons do.</summary>
    [RelayCommand]
    private void Flip(string axis)
    {
        _syncing = true;
        if (axis == "H") WidthPercent = -WidthPercent;
        else HeightPercent = -HeightPercent;
        _syncing = false;
    }
}

// The document's Clone Source slots (the panel shows the active document's).
public sealed partial class DocumentViewModel
{
    private IReadOnlyList<CloneSourceSlot>? _cloneSources;

    /// <summary>The five clone sources of this document.</summary>
    public IReadOnlyList<CloneSourceSlot> CloneSources =>
        _cloneSources ??= [.. Enumerable.Range(1, 5).Select(n => new CloneSourceSlot(n, () => Editor.Tool))];

    /// <summary>Index of the active clone source (0..4), shared by the Clone Stamp and the Healing Brush.</summary>
    public int ActiveCloneSourceIndex
    {
        get;
        set
        {
            value = Math.Clamp(value, 0, 4);
            if (field == value) return;
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveCloneSource));
            ActiveCloneSource.Refresh();
        }
    }

    public CloneSourceSlot ActiveCloneSource => CloneSources[ActiveCloneSourceIndex];
}

/// <summary>Window › Clone Source: five sources for the Clone Stamp and Healing Brush, their placement, and the overlay.</summary>
public sealed class CloneSourceToolViewModel(EditorViewModel editor) : Tool
{
    public EditorViewModel Editor { get; } = editor;
}
