using CommunityToolkit.Mvvm.Input;

namespace Strayta.Editor.ViewModels;

/// <summary>Edit › Content-Aware Fill's dialog (the main window implements it; MainWindow.Retouch.cs).</summary>
public interface IContentAwareFillDialogs
{
    /// <summary>Asks for the fill's settings, starting from <paramref name="current"/>; null when cancelled.</summary>
    Task<ContentAwareFillSettings?> AskContentAwareFillAsync(ContentAwareFillSettings current);
}

// Edit › Content-Aware Fill… (DocumentViewModel.ContentAwareFill.cs). Edit › Fill…'s "Content-Aware" contents can call
// ContentAwareFillAsync on the document directly, or run this command, which asks for the output first.
public sealed partial class EditorViewModel
{
    /// <summary>Where the dialog comes from; the window by default, a script in the self-test.</summary>
    public IContentAwareFillDialogs? ContentAwareFillDialogs { get; set; }

    /// <summary>The last settings used, offered again next time (as Photoshop remembers them).</summary>
    public ContentAwareFillSettings ContentAwareFillSettings { get; set; } = new();

    [RelayCommand]
    private async Task ContentAwareFill()
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.Selection is null)
        {
            doc.Notice = "Content-Aware Fill needs a selection: select the area to fill.";
            return;
        }
        if ((ContentAwareFillDialogs ?? _dialogs as IContentAwareFillDialogs) is not { } dialogs) return;
        if (await dialogs.AskContentAwareFillAsync(ContentAwareFillSettings) is not { } settings) return;
        ContentAwareFillSettings = settings;
        await doc.ContentAwareFillAsync(settings);
    }
}
