using CommunityToolkit.Mvvm.ComponentModel;

namespace Strayta.Editor.ViewModels;

// The Select and Mask workspace while it is open: the main window swaps its tools, options bar and panels for the
// workspace's, and Edit › Undo / Redo act inside the workspace. Remember Settings keeps its settings for the next time.
public sealed partial class EditorViewModel
{
    /// <summary>The open Select and Mask session, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectAndMaskOpen), nameof(IsWorkspaceHidden))]
    public partial SelectAndMaskViewModel? SelectAndMaskSession { get; private set; }

    public bool IsSelectAndMaskOpen => SelectAndMaskSession is not null;

    /// <summary>The regular tools and options bar hide while a workspace (Select and Mask) replaces them.</summary>
    public bool IsWorkspaceHidden => !IsSelectAndMaskOpen;

    /// <summary>Settings of the last session that had Remember Settings on (Photoshop keeps them for the next one).</summary>
    public SelectAndMaskSettings? SelectAndMaskMemory { get; private set; }

    /// <summary>Runs a session in the workspace; true for OK. Remember Settings is honored on OK.</summary>
    private async Task<bool> RunSelectAndMaskSessionAsync(ISelectionDialogs dialogs, SelectAndMaskViewModel session)
    {
        SelectAndMaskSession = session;
        bool ok;
        try
        {
            ok = await dialogs.RunSelectAndMaskAsync(session);
        }
        finally
        {
            SelectAndMaskSession = null;
        }
        if (ok) SelectAndMaskMemory = session.RememberSettings ? session.CurrentSettings : null;
        return ok;
    }

    /// <summary>Edit › Undo while Select and Mask is open undoes inside the workspace; true when it did.</summary>
    private bool UndoInWorkspace()
    {
        if (SelectAndMaskSession is not { } s) return false;
        s.Undo();
        return true;
    }

    private bool RedoInWorkspace()
    {
        if (SelectAndMaskSession is not { } s) return false;
        s.Redo();
        return true;
    }

    /// <summary>Option-click on the Layers panel's mask button: a mask that hides the selection (or everything), as in Photoshop.</summary>
    public void AddHidingLayerMask()
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.Selection is not null) doc.AddMaskFromSelection(reveal: false);
        else doc.AddMask(reveal: false);
    }
}
