using CommunityToolkit.Mvvm.Input;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>The Layer Style dialog (the main window implements it).</summary>
public interface ILayerStyleDialogs
{
    /// <summary>Runs a Layer Style dialog on <paramref name="session"/>; true when the person clicked OK.</summary>
    Task<bool> RunLayerStyleAsync(LayerStyleViewModel session);
}

// Layer › Layer Style: the dialog's pages, Copy / Paste / Clear Layer Style.
public sealed partial class EditorViewModel
{
    /// <summary>Where the Layer Style dialog comes from; the window by default, a script in the self-test.</summary>
    public ILayerStyleDialogs? LayerStyleDialogs { get; set; }

    /// <summary>The style Copy Layer Style took, shared by every open document like Photoshop's.</summary>
    public LayerStyleState? CopiedLayerStyle { get; private set; }

    /// <summary>Layer › Layer Style › Blending Options… / Drop Shadow… / …, the Layers panel's fx menu and double-click.</summary>
    [RelayCommand]
    private async Task LayerStyle(string page)
    {
        if (ActiveDocument is not { } doc || (LayerStyleDialogs ?? _dialogs as ILayerStyleDialogs) is not { } dialogs) return;
        if (!Enum.TryParse<LayerStylePage>(page, out var p)) p = LayerStylePage.BlendingOptions;
        if (doc.BeginLayerStyle(p) is not { } session) return;
        bool ok;
        try
        {
            ok = await dialogs.RunLayerStyleAsync(session);
        }
        catch
        {
            session.Cancel();
            throw;
        }
        if (ok) session.Commit();
        else session.Cancel();
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
