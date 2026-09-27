using CommunityToolkit.Mvvm.Input;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>Layer › Layer Mask and Layer › New Adjustment Layer commands for the active document.</summary>
public sealed partial class EditorViewModel
{
    /// <summary>Adjustment kinds for menus, in Photoshop's order.</summary>
    public IReadOnlyList<string> AdjustmentKinds => AdjustmentFactory.Kinds;

    /// <summary>"reveal" adds a white mask (Reveal All), "hide" a black one (Hide All).</summary>
    [RelayCommand] private void AddMask(string mode) => ActiveDocument?.AddMask(reveal: mode != "hide");
    [RelayCommand] private void DeleteMask() => ActiveDocument?.DeleteMask();
    [RelayCommand] private void ToggleMask() => ActiveDocument?.ToggleMaskEnabled();
    [RelayCommand] private void NewAdjustment(string kind) => ActiveDocument?.NewAdjustmentLayer(kind);

    [RelayCommand]
    private async Task ApplyMask()
    {
        if (ActiveDocument is { } doc) await doc.ApplyMaskAsync();
    }
}
