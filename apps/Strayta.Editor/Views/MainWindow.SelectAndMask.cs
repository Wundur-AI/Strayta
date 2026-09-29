using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Select and Mask as a workspace: while a session runs, the window shows SelectAndMaskWorkspace in place of the tool
// strip, options bar and panels (bound to EditorViewModel.SelectAndMaskSession), until OK or Cancel.
public partial class MainWindow
{
    private TaskCompletionSource<bool>? _selectAndMaskDone;

    /// <summary>The workspace control (for tests).</summary>
    internal SelectAndMaskWorkspace SelectAndMaskView => SelectAndMask;

    /// <summary>Shows the workspace for <paramref name="session"/> (already the editor's open session) and waits for OK (true) or Cancel.</summary>
    private Task<bool> ShowSelectAndMaskWorkspaceAsync(SelectAndMaskViewModel session)
    {
        _selectAndMaskDone?.TrySetResult(false);
        var done = _selectAndMaskDone = new TaskCompletionSource<bool>();
        SelectAndMask.IsVisible = true;
        SelectAndMask.DataContext = session;
        SelectAndMask.Completed -= OnSelectAndMaskCompleted;
        SelectAndMask.Completed += OnSelectAndMaskCompleted;
        return done.Task;
    }

    /// <summary>OK or Cancel in the workspace (also used by tests).</summary>
    internal void CompleteSelectAndMask(bool ok) => OnSelectAndMaskCompleted(ok);

    private void OnSelectAndMaskCompleted(bool ok)
    {
        var done = _selectAndMaskDone;
        _selectAndMaskDone = null;
        SelectAndMask.IsVisible = false;
        SelectAndMask.DataContext = null;
        done?.TrySetResult(ok);
    }
}
