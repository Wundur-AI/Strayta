using CommunityToolkit.Mvvm.Input;

namespace Strayta.Editor.ViewModels;

/// <summary>The dialogs of Select › Modify and Select › Select and Mask (the main window implements them).</summary>
public interface ISelectionDialogs
{
    /// <summary>
    /// A Photoshop-style Modify dialog: one pixel amount, plus "Apply effect at canvas bounds" where it matters.
    /// Returns null if cancelled.
    /// </summary>
    Task<(double Amount, bool ApplyAtCanvasBounds)?> AskModifySelectionAsync(SelectionModification kind, double amount, bool applyAtCanvasBounds);

    /// <summary>Runs a Select and Mask session; true when the person clicked OK.</summary>
    Task<bool> RunSelectAndMaskAsync(SelectAndMaskViewModel session);
}

// Select › Modify, Select › Select and Mask, and layer masks from the selection.
public sealed partial class EditorViewModel
{
    private readonly Dictionary<SelectionModification, double> _modifyAmounts = new()
    {
        [SelectionModification.Expand] = 1,
        [SelectionModification.Contract] = 1,
        [SelectionModification.Feather] = 1,
        [SelectionModification.Smooth] = 1,
        [SelectionModification.Border] = 1,
    };
    private bool _modifyAtCanvasBounds;

    /// <summary>Where the selection dialogs come from; the window by default, a script in the self-test.</summary>
    public ISelectionDialogs? SelectionDialogs { get; set; }

    private ISelectionDialogs? Dialogs => SelectionDialogs ?? _dialogs as ISelectionDialogs;

    /// <summary>Select › Modify › Expand… / Contract… / Feather… (⇧F6) / Smooth… / Border…, with Photoshop's remembered amounts.</summary>
    [RelayCommand]
    private async Task ModifySelection(string kind)
    {
        if (ActiveDocument is not { } doc || !Enum.TryParse<SelectionModification>(kind, out var k)) return;
        if (doc.Selection is null)
        {
            doc.Notice = "Nothing is selected.";
            return;
        }
        if (Dialogs is not { } dialogs) return;
        if (await dialogs.AskModifySelectionAsync(k, _modifyAmounts[k], _modifyAtCanvasBounds) is not { } answer) return;
        _modifyAmounts[k] = answer.Amount;
        _modifyAtCanvasBounds = answer.ApplyAtCanvasBounds;
        await doc.ModifySelectionAsync(k, answer.Amount, answer.ApplyAtCanvasBounds);
    }

    /// <summary>Select › Select and Mask… (⌥⌘R).</summary>
    [RelayCommand]
    private async Task SelectAndMask()
    {
        if (ActiveDocument is not { } doc || Dialogs is not { } dialogs) return;
        using var session = await doc.BeginSelectAndMaskAsync();
        if (session is null) return;
        if (!await dialogs.RunSelectAndMaskAsync(session)) return;
        var refined = await session.ResultAsync();
        doc.ApplySelectAndMask(refined, session.Output);
    }

    /// <summary>Layer › Layer Mask › Reveal Selection ("reveal") / Hide Selection ("hide").</summary>
    [RelayCommand]
    private void AddMaskFromSelection(string mode) => ActiveDocument?.AddMaskFromSelection(reveal: mode != "hide");

    /// <summary>The Layers panel's Add layer mask button: reveals the selection if there is one, else reveals all.</summary>
    [RelayCommand]
    private void AddLayerMask() => ActiveDocument?.AddMaskButton();
}
