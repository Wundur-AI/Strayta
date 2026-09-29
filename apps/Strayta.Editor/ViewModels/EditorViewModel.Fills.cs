using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>Edit › Fill…, Stroke… and Define Pattern… dialogs (the main window implements them; MainWindow.Fills.cs).</summary>
public interface IFillDialogs
{
    /// <summary>Runs the Fill dialog over <paramref name="fill"/>; true for OK.</summary>
    Task<bool> AskFillAsync(FillDialogViewModel fill);

    /// <summary>Runs the Stroke dialog over <paramref name="stroke"/>; true for OK.</summary>
    Task<bool> AskStrokeAsync(StrokeDialogViewModel stroke);

    /// <summary>Photoshop's Pattern Name prompt; null when cancelled.</summary>
    Task<string?> AskPatternNameAsync(string suggested, Pattern preview);
}

// Paint Bucket fill options (Fill: Foreground / Pattern, Mode, Opacity), Edit › Fill…, Edit › Stroke… and
// Edit › Define Pattern…. Settings are app-wide and remembered between uses, as in Photoshop.
public sealed partial class EditorViewModel
{
    // ---- Paint Bucket ---------------------------------------------------------------------------------

    public IReadOnlyList<string> BucketSourceNames { get; } = ["Foreground", "Pattern"];

    /// <summary>0: the foreground color; 1: <see cref="BucketPattern"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BucketUsesPattern))]
    public partial int BucketSourceIndex { get; set; }

    public bool BucketUsesPattern => BucketSourceIndex == 1;

    [ObservableProperty] public partial Pattern? BucketPattern { get; set; } = PatternLibrary.BuiltIn[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BucketModeIndex))]
    public partial PaintMode BucketMode { get; set; } = PaintMode.Normal;

    public int BucketModeIndex { get => Controls.PaintModeNames.IndexOf(BucketMode); set { if (Controls.PaintModeNames.At(value) is { } m) BucketMode = m; } }

    /// <summary>Percent.</summary>
    [ObservableProperty] public partial double BucketOpacity { get; set; } = 100;

    /// <summary>What a Paint Bucket click fills with now.</summary>
    public FillOptions CurrentBucketFill => new(BucketUsesPattern && BucketPattern is { } p ? new FillSource.Tiled(p) : new FillSource.Color(CurrentColor))
    {
        Mode = BucketMode,
        Opacity = (float)Math.Clamp(BucketOpacity / 100, 0, 1),
    };

    // ---- Edit › Fill…, Stroke…, Define Pattern… ---------------------------------------------------------

    /// <summary>Where the fill dialogs come from; the window by default, a script in the self-test.</summary>
    public IFillDialogs? FillDialogs { get; set; }

    private IFillDialogs? FillDialogProvider => FillDialogs ?? _dialogs as IFillDialogs;

    private FillDialogViewModel? _lastFill;
    private StrokeDialogViewModel? _lastStroke;

    /// <summary>Edit › Fill… (⇧F5): the dialog, then the fill as one undo step.</summary>
    [RelayCommand]
    private async Task FillDialog()
    {
        if (ActiveDocument is not { } doc || FillDialogProvider is not { } dialogs) return;
        var vm = _lastFill?.Copy(doc) ?? new FillDialogViewModel(doc.Model.Patterns);
        vm.ContentAwareAvailable = doc.Selection is not null;
        if (!await dialogs.AskFillAsync(vm)) return;
        _lastFill = vm;
        if (vm.Contents == FillContents.ContentAware)
        {
            // As Photoshop's Fill does: fill the selection in place with the last Content-Aware Fill settings
            // (Edit › Content-Aware Fill… offers the full dialog; DocumentViewModel.ContentAwareFill.cs).
            await doc.ContentAwareFillAsync(ContentAwareFillSettings);
            return;
        }
        await doc.FillWithAsync(vm.ToOptions(CurrentColor, CurrentBackgroundColor));
    }

    /// <summary>Edit › Stroke…: the dialog, then a band along the selection's outline as one undo step.</summary>
    [RelayCommand]
    private async Task StrokeDialog()
    {
        if (ActiveDocument is not { } doc || FillDialogProvider is not { } dialogs) return;
        if (!doc.HasSelection)
        {
            doc.Notice = "Make a selection first: Stroke draws along its outline.";
            return;
        }
        var vm = _lastStroke?.Copy() ?? new StrokeDialogViewModel { Color = ForegroundColor };
        if (!await dialogs.AskStrokeAsync(vm)) return;
        _lastStroke = vm;
        await doc.StrokeSelectionAsync(vm.Width, vm.ToOptions(), vm.Location);
    }

    /// <summary>Edit › Define Pattern…: the selected area of the image (all of it with no selection) becomes a user pattern.</summary>
    [RelayCommand]
    private async Task DefinePattern()
    {
        if (ActiveDocument is not { } doc || FillDialogProvider is not { } dialogs) return;
        if (await doc.CapturePatternAsync() is not { } captured) return;
        if (await dialogs.AskPatternNameAsync(captured.Name, captured) is not { } name) return;
        var pattern = new Pattern(captured.Id, string.IsNullOrWhiteSpace(name) ? captured.Name : name.Trim(), captured.Pixels);
        UserPresets.Shared.AddPattern(pattern);
        BucketPattern = pattern;
        doc.Notice = $"Pattern \"{pattern.Name}\" ({pattern.Width}×{pattern.Height}) defined.";
    }
}
