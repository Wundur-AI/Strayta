using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Rendering.Filters;

namespace Strayta.Editor.ViewModels;

/// <summary>The Filter menu's dialogs (the main window implements them; MainWindow.Filters.cs).</summary>
public interface IFilterDialogs
{
    /// <summary>Runs a filter dialog over <paramref name="session"/>; true when the person clicked OK.</summary>
    Task<bool> RunFilterAsync(FilterSessionViewModel session);

    /// <summary>Photoshop's "must be rasterized before proceeding" prompt; true for OK.</summary>
    Task<bool> AskRasterizeAsync(string message);
}

// Filter › Last Filter and the filters. Settings are remembered per filter for the next time its dialog opens, as in
// Photoshop, and Last Filter (⌃⌘F) applies the last one again without a dialog.
public sealed partial class EditorViewModel
{
    private readonly Dictionary<FilterKind, ImageFilter> _filterSettings = [];

    /// <summary>Where the filter dialogs come from; the window by default, a script in the self-test.</summary>
    public IFilterDialogs? FilterDialogs { get; set; }

    private IFilterDialogs? FilterDialogProvider => FilterDialogs ?? _dialogs as IFilterDialogs;

    /// <summary>The filter Last Filter applies, or null before the first one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastFilterName))]
    [NotifyCanExecuteChangedFor(nameof(ApplyLastFilterCommand))]
    public partial ImageFilter? LastFilter { get; private set; }

    /// <summary>The Filter menu's first item: the last filter's name, as in Photoshop.</summary>
    public string LastFilterName => LastFilter?.Name ?? "Last Filter";

    /// <summary>Filter › Blur › Gaussian Blur… and the rest (<paramref name="kind"/> is a <see cref="FilterKind"/> name).</summary>
    [RelayCommand]
    private async Task Filter(string kind)
    {
        if (ActiveDocument is not { } doc || FilterDialogProvider is not { } dialogs || !Enum.TryParse<FilterKind>(kind, out var k)) return;
        var initial = _filterSettings.GetValueOrDefault(k) ?? FilterSessionViewModel.DefaultFilter(k);
        if (initial is AddNoiseFilter noise) initial = noise with { Seed = Random.Shared.Next() }; // a new pattern each time
        if (await FilterSmartObjectAsync(doc, dialogs, k, initial, showDialog: true)) return; // a smart filter (EditorViewModel.SmartObjects.cs)
        if (!await PrepareFilterTargetAsync(doc, dialogs) || !doc.BeginFilter(initial.Name)) return;

        using var session = new FilterSessionViewModel(doc, k, initial);
        bool ok;
        try
        {
            ok = await dialogs.RunFilterAsync(session);
        }
        catch
        {
            session.Cancel();
            throw;
        }
        if (!ok)
        {
            session.Cancel();
            return;
        }
        var filter = session.Filter;
        _filterSettings[k] = filter;
        LastFilter = filter;
        await doc.ApplyFilterAsync(filter);
    }

    /// <summary>Filter › Last Filter (⌃⌘F): the last filter again, with the same settings (and a fresh noise pattern).</summary>
    [RelayCommand(CanExecute = nameof(HasLastFilter))]
    private async Task ApplyLastFilter()
    {
        if (ActiveDocument is not { } doc || LastFilter is not { } filter || FilterDialogProvider is not { } dialogs) return;
        if (filter is AddNoiseFilter noise) filter = noise with { Seed = Random.Shared.Next() };
        if (await FilterSmartObjectAsync(doc, dialogs, FilterSessionViewModel.KindOf(filter), filter, showDialog: false)) return;
        if (!await PrepareFilterTargetAsync(doc, dialogs) || !doc.BeginFilter(filter.Name)) return;
        await doc.ApplyFilterAsync(filter);
    }

    private bool HasLastFilter() => LastFilter is not null;

    /// <summary>Type, shape, fill and smart-object layers are rasterized first, after Photoshop's prompt.</summary>
    private static async Task<bool> PrepareFilterTargetAsync(DocumentViewModel doc, IFilterDialogs dialogs)
    {
        if (doc.FilterRasterizePrompt() is not { } prompt) return true;
        if (!await dialogs.AskRasterizeAsync(prompt)) return false;
        doc.RasterizeSelected();
        return true;
    }

    /// <summary>For the benchmark: opens a filter session on the active document without a dialog.</summary>
    internal FilterSessionViewModel OpenFilterSession(ImageFilter filter)
    {
        var doc = ActiveDocument ?? throw new InvalidOperationException("No document.");
        if (!doc.BeginFilter(filter.Name)) throw new InvalidOperationException(doc.Notice);
        return new FilterSessionViewModel(doc, FilterSessionViewModel.KindOf(filter), filter);
    }
}
