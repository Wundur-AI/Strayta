using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Rendering.Filters;

namespace Strayta.Editor.ViewModels;

/// <summary>Channel Options (and New Channel / New Spot Channel): name, what the color marks, color and opacity.</summary>
public sealed record ChannelOptions(string Name, ChannelKind Kind, RgbColor Color, float Opacity);

/// <summary>Save Selection: the channel to save into (0 for a new one), the new channel's name, and the operation.</summary>
public sealed record SaveSelectionChoice(int IntoId, string Name, SelectionMode Mode);

/// <summary>Load Selection: the channel, Invert, and the operation with the current selection.</summary>
public sealed record LoadSelectionChoice(int ChannelId, bool Invert, SelectionMode Mode);

/// <summary>One number of an Image › Adjustments dialog.</summary>
public sealed record AdjustmentSetting(string Label, double Minimum, double Maximum, double Value, string Format = "0");

/// <summary>The Channels panel's and Select menu's dialogs (the main window implements them; MainWindow.Channels.cs).</summary>
public interface IChannelDialogs
{
    /// <summary>New Channel…, New Spot Channel… and Channel Options…; null when cancelled.</summary>
    Task<ChannelOptions?> AskChannelOptionsAsync(string title, ChannelOptions initial, bool spot);

    Task<SaveSelectionChoice?> AskSaveSelectionAsync(IReadOnlyList<DocumentChannel> channels, string suggestedName);

    Task<LoadSelectionChoice?> AskLoadSelectionAsync(IReadOnlyList<DocumentChannel> channels, int targetedId, bool hasSelection);

    Task<QuickMaskOptions?> AskQuickMaskOptionsAsync(QuickMaskOptions current);

    /// <summary>Duplicate Channel's name; null when cancelled.</summary>
    Task<string?> AskChannelNameAsync(string title, string initial);

    /// <summary>
    /// An Image › Adjustments dialog: its numbers (and a Colorize-style check box when <paramref name="option"/> is
    /// given); <paramref name="preview"/> is called with every change. Null when cancelled.
    /// </summary>
    Task<(double[] Values, bool Option)?> AskAdjustmentAsync(string title, IReadOnlyList<AdjustmentSetting> settings, string? option,
        Action<double[], bool> preview);
}

// The Channels panel's commands, Select › Save Selection / Load Selection, Quick Mask (Q) and Image › Adjustments.
public sealed partial class EditorViewModel
{
    /// <summary>Where the channel dialogs come from; the window by default, a script in the self-test.</summary>
    public IChannelDialogs? ChannelDialogs { get; set; }

    private IChannelDialogs? ChannelDialogProvider => ChannelDialogs ?? _dialogs as IChannelDialogs;

    /// <summary>Quick Mask Options, shared by all documents.</summary>
    [ObservableProperty] public partial QuickMaskOptions QuickMaskSettings { get; set; } = ViewModels.QuickMaskOptions.Default;

    [RelayCommand]
    private void ToggleQuickMask() => ActiveDocument?.ToggleQuickMask();

    [RelayCommand]
    private async Task EditQuickMaskOptions()
    {
        if (ChannelDialogProvider is not { } dialogs || await dialogs.AskQuickMaskOptionsAsync(QuickMaskSettings) is not { } options) return;
        var before = QuickMaskSettings;
        QuickMaskSettings = options;
        ActiveDocument?.ApplyQuickMaskOptions(before, options);
    }

    /// <summary>The panel's New Channel button: "Alpha 1" and so on, no dialog.</summary>
    [RelayCommand]
    private void NewChannel() => ActiveDocument?.NewChannel();

    /// <summary>New Channel… (the panel menu), with Channel Options.</summary>
    [RelayCommand]
    private async Task NewChannelWithOptions()
    {
        if (ActiveDocument is not { } doc || ChannelDialogProvider is not { } dialogs) return;
        var initial = new ChannelOptions(doc.SuggestedChannelName, ChannelKind.MaskedAreas, new RgbColor(1f, 0f, 0f), 0.5f);
        if (await dialogs.AskChannelOptionsAsync("New Channel", initial, spot: false) is not { } o) return;
        if (o.Kind == ChannelKind.Spot) doc.NewSpotChannel(o.Name, o.Color, o.Opacity);
        else doc.NewChannel(o.Name, o.Kind, o.Color, o.Opacity);
    }

    [RelayCommand]
    private async Task NewSpotChannel()
    {
        if (ActiveDocument is not { } doc || ChannelDialogProvider is not { } dialogs) return;
        var initial = new ChannelOptions(doc.SuggestedSpotName, ChannelKind.Spot, new RgbColor(1f, 0f, 0f), 0f);
        if (await dialogs.AskChannelOptionsAsync("New Spot Channel", initial, spot: true) is { } o) doc.NewSpotChannel(o.Name, o.Color, o.Opacity);
    }

    [RelayCommand]
    private async Task DuplicateChannel()
    {
        if (ActiveDocument is not { TargetedChannel: { } channel } doc) return;
        string? name = ChannelDialogProvider is { } dialogs ? await dialogs.AskChannelNameAsync("Duplicate Channel", $"{channel.Name} copy") : null;
        if (name is not null) doc.DuplicateChannel(channel.Id, name);
    }

    [RelayCommand]
    private void DeleteChannel()
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.TargetedChannel is { } channel) doc.DeleteChannel(channel.Id);
        else if (doc.ChannelTarget == ChannelTargetKind.LayerMask) doc.DeleteMask();
        else doc.Notice = "Select a saved selection or spot channel to delete.";
    }

    [RelayCommand]
    private async Task EditChannelOptions()
    {
        if (ActiveDocument is not { } doc || ChannelDialogProvider is not { } dialogs) return;
        if (doc.ChannelTarget == ChannelTargetKind.QuickMask)
        {
            await EditQuickMaskOptions();
            return;
        }
        if (doc.TargetedChannel is not { } c) return;
        if (await dialogs.AskChannelOptionsAsync("Channel Options", new ChannelOptions(c.Name, c.Kind, c.Color, c.Opacity), c.IsSpot) is { } o)
            doc.SetChannelOptions(c.Id, o.Name, o.Kind, o.Color, o.Opacity);
    }

    /// <summary>The panel's Save Selection as Channel button: a new channel, no dialog.</summary>
    [RelayCommand]
    private void SaveSelectionAsChannel() => ActiveDocument?.SaveSelection();

    /// <summary>The panel's Load Channel as Selection button: the targeted channel (or color channel, or layer mask).</summary>
    [RelayCommand]
    private void LoadChannelAsSelection()
    {
        if (ActiveDocument is not { } doc) return;
        switch (doc.ChannelTarget)
        {
            case ChannelTargetKind.Channel:
                doc.LoadSelection(doc.TargetedChannel!.Id);
                break;
            case ChannelTargetKind.LayerMask:
                doc.LoadLayerMaskAsSelection();
                break;
            case ChannelTargetKind.Color:
                int k = Enumerable.Range(0, doc.ColorChannelCount).Where(doc.IsColorTargeted).DefaultIfEmpty(-1).First();
                doc.LoadColorChannelAsSelection(doc.IsCompositeTargeted ? -1 : k);
                break;
        }
    }

    /// <summary>Select › Save Selection….</summary>
    [RelayCommand]
    private async Task SaveSelection()
    {
        if (ActiveDocument is not { } doc || ChannelDialogProvider is not { } dialogs) return;
        if (!doc.HasSelection)
        {
            doc.Notice = "Make a selection first: Save Selection stores the selected area in a channel.";
            return;
        }
        if (await dialogs.AskSaveSelectionAsync(doc.Channels, doc.SuggestedChannelName) is { } choice)
            doc.SaveSelection(choice.IntoId, choice.Mode, choice.Name);
    }

    /// <summary>Select › Load Selection….</summary>
    [RelayCommand]
    private async Task LoadSelection()
    {
        if (ActiveDocument is not { } doc || ChannelDialogProvider is not { } dialogs) return;
        if (doc.Channels.Count == 0)
        {
            doc.Notice = "The document has no saved selections. Use Select › Save Selection first.";
            return;
        }
        if (await dialogs.AskLoadSelectionAsync(doc.Channels, doc.TargetedChannel?.Id ?? 0, doc.HasSelection) is { } choice)
            doc.LoadSelection(choice.ChannelId, choice.Invert, choice.Mode);
    }

    /// <summary>⌘2 … ⌘9: the composite, color channels and saved selections.</summary>
    [RelayCommand]
    private void TargetChannelShortcut(string number)
    {
        if (int.TryParse(number, out int n)) ActiveDocument?.TargetByShortcut(n);
    }

    /// <summary>\: the layer mask's red overlay.</summary>
    [RelayCommand]
    private void ToggleLayerMaskOverlay() => ActiveDocument?.ToggleLayerMaskOverlay();

    // ---- Image › Adjustments ---------------------------------------------------------------------------------

    /// <summary>
    /// Image › Adjustments applied to the selected layer's pixels (or its targeted mask, or a targeted channel), within
    /// the selection, as one undo step. With some color channels targeted only those change. Settings dialogs preview
    /// on the canvas.
    /// </summary>
    [RelayCommand]
    private async Task ImageAdjustment(string kind)
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.SelectedSmartObject is not null && doc.ChannelTarget == ChannelTargetKind.Color)
        {
            doc.Notice = "Rasterize the smart object to adjust its pixels, or add an adjustment layer above it.";
            return;
        }
        if (doc.FilterRasterizePrompt() is { } prompt)
        {
            if (FilterDialogProvider is not { } fd || !await fd.AskRasterizeAsync(prompt)) return;
            doc.RasterizeSelected();
        }
        Adjustment? fixedAdjustment = kind switch
        {
            "Invert" => new InvertAdjustment(),
            "Desaturate" => new HueSaturationAdjustment(0, -100, 0, false, 0, 25, 0, false),
            _ => null,
        };
        string title = kind;
        if (fixedAdjustment is not null)
        {
            if (!doc.BeginFilter(title)) return;
            await doc.ApplyFilterAsync(new AdjustmentFilter(fixedAdjustment, title));
            return;
        }
        if (ChannelDialogProvider is not { } dialogs || AdjustmentDialogs.Settings(kind) is not { } settings) return;
        if (!doc.BeginFilter(title)) return;
        (double[] Values, bool Option)? result;
        try
        {
            result = await dialogs.AskAdjustmentAsync(title, settings.Values, settings.Option,
                (values, option) => doc.PreviewFilter(new AdjustmentFilter(AdjustmentDialogs.Build(kind, values, option), title)));
        }
        catch
        {
            doc.CancelFilter();
            throw;
        }
        if (result is not { } r)
        {
            doc.CancelFilter();
            return;
        }
        await doc.ApplyFilterAsync(new AdjustmentFilter(AdjustmentDialogs.Build(kind, r.Values, r.Option), title));
    }
}

/// <summary>The settings of Image › Adjustments' dialogs, in Photoshop's ranges.</summary>
public static class AdjustmentDialogs
{
    public static (IReadOnlyList<AdjustmentSetting> Values, string? Option)? Settings(string kind) => kind switch
    {
        "Brightness/Contrast" => ([new("Brightness", -150, 150, 0), new("Contrast", -50, 100, 0)], null),
        "Levels" => ([new("Input Black", 0, 253, 0), new("Gamma", 0.1, 9.99, 1, "0.00"), new("Input White", 2, 255, 255),
            new("Output Black", 0, 255, 0), new("Output White", 0, 255, 255)], null),
        "Hue/Saturation" => ([new("Hue", -180, 180, 0), new("Saturation", -100, 100, 0), new("Lightness", -100, 100, 0)], "Colorize"),
        "Exposure" => ([new("Exposure", -20, 20, 0, "0.00"), new("Offset", -0.5, 0.5, 0, "0.0000"), new("Gamma Correction", 0.01, 9.99, 1, "0.00")], null),
        "Threshold" => ([new("Threshold Level", 1, 255, 128)], null),
        "Posterize" => ([new("Levels", 2, 255, 4)], null),
        _ => null,
    };

    public static Adjustment Build(string kind, double[] v, bool option) => kind switch
    {
        "Brightness/Contrast" => new BrightnessContrastAdjustment((int)v[0], (int)v[1]),
        "Levels" => new LevelsAdjustment(new LevelsChannel((int)v[0], Math.Max((int)v[0] + 2, (int)v[2]), (int)v[3], (int)v[4], (float)v[1]), []),
        "Hue/Saturation" => option
            ? new HueSaturationAdjustment(0, 0, 0, true, (int)(v[0] < 0 ? v[0] + 360 : v[0]), Math.Max(0, (int)v[1]), (int)v[2], false)
            : new HueSaturationAdjustment((int)v[0], (int)v[1], (int)v[2], false, 0, 25, 0, false),
        "Exposure" => new ExposureAdjustment((float)v[0], (float)v[1], (float)v[2]),
        "Threshold" => new ThresholdAdjustment((int)v[0]),
        "Posterize" => new PosterizeAdjustment((int)v[0]),
        _ => new InvertAdjustment(),
    };
}
