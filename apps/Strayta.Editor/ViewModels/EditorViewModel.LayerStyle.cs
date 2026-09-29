using CommunityToolkit.Mvvm.Input;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>The Layer Style dialogs (the main window implements them).</summary>
public interface ILayerStyleDialogs
{
    /// <summary>Runs a Layer Style dialog on <paramref name="session"/>; true when the person clicked OK.</summary>
    Task<bool> RunLayerStyleAsync(LayerStyleViewModel session);

    /// <summary>Runs Layer › Layer Style › Global Light…; true for OK.</summary>
    Task<bool> RunGlobalLightAsync(GlobalLightViewModel session);

    /// <summary>Runs Layer › Layer Style › Scale Effects…; true for OK.</summary>
    Task<bool> RunScaleEffectsAsync(ScaleEffectsViewModel session);
}

// Layer › Layer Style: the dialog's pages, Copy / Paste / Clear Layer Style, Global Light and Scale Effects.
public sealed partial class EditorViewModel
{
    /// <summary>Where the Layer Style dialogs come from; the window by default, a script in the self-test.</summary>
    public ILayerStyleDialogs? LayerStyleDialogs { get; set; }

    private ILayerStyleDialogs? StyleDialogs => LayerStyleDialogs ?? _dialogs as ILayerStyleDialogs;

    /// <summary>The style Copy Layer Style took, shared by every open document like Photoshop's.</summary>
    public LayerStyleState? CopiedLayerStyle { get; private set; }

    /// <summary>Layer › Layer Style › Blending Options… / Drop Shadow… / …, the Layers panel's fx menu and double-click.</summary>
    [RelayCommand]
    private async Task LayerStyle(string page)
    {
        if (ActiveDocument is not { } doc || StyleDialogs is not { } dialogs) return;
        if (!Enum.TryParse<LayerStylePage>(page, out var p)) p = LayerStylePage.BlendingOptions;
        if (doc.BeginLayerStyle(p) is not { } session) return;
        await RunStyleDialog(() => dialogs.RunLayerStyleAsync(session), session.Commit, session.Cancel);
    }

    /// <summary>Layer › Layer Style › Global Light…</summary>
    [RelayCommand]
    private async Task GlobalLight()
    {
        if (ActiveDocument is not { } doc || StyleDialogs is not { } dialogs || doc.IsTransforming || doc.CropBox is not null) return;
        var session = doc.BeginGlobalLight();
        await RunStyleDialog(() => dialogs.RunGlobalLightAsync(session), session.Commit, session.Cancel);
    }

    /// <summary>Layer › Layer Style › Scale Effects…</summary>
    [RelayCommand]
    private async Task ScaleEffects()
    {
        if (ActiveDocument is not { } doc || StyleDialogs is not { } dialogs || doc.IsTransforming || doc.CropBox is not null) return;
        if (doc.BeginScaleEffects() is not { } session) return;
        await RunStyleDialog(() => dialogs.RunScaleEffectsAsync(session), session.Commit, session.Cancel);
    }

    /// <summary>Runs a previewing dialog: OK commits, Cancel (or a failure) puts everything back.</summary>
    private static async Task RunStyleDialog(Func<Task<bool>> dialog, Action commit, Action cancel)
    {
        bool ok;
        try
        {
            ok = await dialog();
        }
        catch
        {
            cancel();
            throw;
        }
        if (ok) commit();
        else cancel();
    }

    [RelayCommand]
    private void CopyLayerStyle()
    {
        if (ActiveDocument?.CopyLayerStyle() is { } style) CopiedLayerStyle = style;
    }

    [RelayCommand]
    private void PasteLayerStyle()
    {
        if (CopiedLayerStyle is { } style) ActiveDocument?.PasteLayerStyle(style);
        else if (ActiveDocument is { } doc) doc.Notice = "Copy a layer style first.";
    }

    [RelayCommand] private void ClearLayerStyle() => ActiveDocument?.ClearLayerStyle();
}
