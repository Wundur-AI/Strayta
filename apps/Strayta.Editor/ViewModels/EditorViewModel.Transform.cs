using CommunityToolkit.Mvvm.Input;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>Liquify's workspace (the main window opens it; the self-test scripts it).</summary>
public interface ILiquifyDialogs
{
    /// <summary>Runs the Liquify workspace over <paramref name="session"/>; true when the person clicked OK.</summary>
    Task<bool> RunLiquifyAsync(LiquifySession session);
}

// Edit › Transform (Scale, Rotate, Skew, Distort, Perspective, Warp, Rotate 180° / 90°, Flip), Edit › Puppet Warp,
// Edit › Content-Aware Scale and Filter › Liquify.
public sealed partial class EditorViewModel
{
    /// <summary>Where the Liquify workspace comes from; the window by default, a script in the self-test.</summary>
    public ILiquifyDialogs? LiquifyDialogs { get; set; }

    /// <summary>The options bar's warp styles, in <see cref="WarpTransform.Styles"/> order.</summary>
    public IReadOnlyList<string> WarpStyleNames => WarpTransform.StyleNames;

    /// <summary>The options bar's warp grids, in <see cref="WarpTransform.GridSizes"/> order.</summary>
    public IReadOnlyList<string> WarpGridNames { get; } = ["Default", "3 × 3", "4 × 4", "5 × 5"];

    /// <summary>Puppet Warp's densities.</summary>
    public IReadOnlyList<string> PuppetDensityNames { get; } = ["Fewer Points", "Normal", "More Points"];

    /// <summary>
    /// Edit › Transform › Scale / Rotate / Skew / Distort / Perspective / Warp (and the options bar's Warp toggle):
    /// type and shapes are rasterized first after Photoshop's prompt when the mode needs it (Warp on type is Warp Text
    /// and keeps it live).
    /// </summary>
    [RelayCommand]
    private async Task TransformMode(string mode)
    {
        if (ActiveDocument is not { } doc || !Enum.TryParse<TransformMode>(mode, out var m)) return;
        if (doc.TransformRasterizePrompt(m) is { } prompt)
        {
            if (FilterDialogProvider is not { } dialogs || !await dialogs.AskRasterizeAsync(prompt)) return;
            var open = doc.FreeTransform;
            var state = open is null ? default : (open.X, open.Y, open.WidthPercent, open.HeightPercent, open.Angle);
            if (open is not null) doc.CancelTransform();
            doc.RasterizeSelected();
            if (open is not null && doc.BeginFreeTransform() && doc.FreeTransform is { } again)
                (again.X, again.Y, again.WidthPercent, again.HeightPercent, again.Angle) = state;
        }
        doc.SetTransformMode(m);
    }

    /// <summary>The options bar's Warp button: between Free Transform and Warp.</summary>
    [RelayCommand]
    private Task ToggleWarp() =>
        ActiveDocument?.FreeTransform is { IsWarping: true } t ? SetMode(t) : TransformMode(nameof(Editing.TransformMode.Warp));

    private static Task SetMode(FreeTransform t)
    {
        t.Mode = Editing.TransformMode.Free;
        return Task.CompletedTask;
    }

    /// <summary>Edit › Transform › Rotate 180°, Rotate 90° Clockwise / Counter Clockwise, Flip Horizontal / Vertical.</summary>
    [RelayCommand]
    private Task TransformAction(string action) => ActiveDocument?.TransformActionAsync(action) ?? Task.CompletedTask;

    /// <summary>The options bar's warp reset (back to no warp).</summary>
    [RelayCommand]
    private void ResetWarp() => ActiveDocument?.FreeTransform?.Warp?.Reset();

    /// <summary>Edit › Puppet Warp: type and shapes are rasterized after the prompt; smart objects are baked (with a notice).</summary>
    [RelayCommand]
    private async Task PuppetWarp()
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.PuppetRasterizePrompt() is { } prompt)
        {
            if (FilterDialogProvider is not { } dialogs || !await dialogs.AskRasterizeAsync(prompt)) return;
            doc.RasterizeSelected();
        }
        await doc.BeginPuppetWarpAsync();
    }

    /// <summary>Enter or ✓ in Puppet Warp.</summary>
    [RelayCommand]
    private Task CommitPuppetWarp() => ActiveDocument?.CommitPuppetWarpAsync() ?? Task.CompletedTask;

    /// <summary>Esc or ✕ in Puppet Warp.</summary>
    [RelayCommand]
    private void CancelPuppetWarp() => ActiveDocument?.CancelPuppetWarp();

    /// <summary>The options bar's Remove All Pins.</summary>
    [RelayCommand]
    private void RemoveAllPins() => ActiveDocument?.PuppetWarp?.RemoveAllPins();

    /// <summary>Filter › Liquify… (⇧⌘X).</summary>
    [RelayCommand]
    private async Task Liquify()
    {
        if (ActiveDocument is not { } doc || (LiquifyDialogs ?? _dialogs as ILiquifyDialogs) is not { } dialogs) return;
        if (doc.LiquifyRasterizePrompt() is { } prompt)
        {
            if (FilterDialogProvider is not { } ask || !await ask.AskRasterizeAsync(prompt)) return;
            doc.RasterizeSelected();
        }
        if (doc.BeginLiquify() is not { } session) return;
        if (!await dialogs.RunLiquifyAsync(session)) return;
        await doc.ApplyLiquifyAsync(session);
    }

    /// <summary>Edit › Content-Aware Scale (⌥⇧⌘C): type, shapes, fills and smart objects are rasterized after the prompt.</summary>
    [RelayCommand]
    private async Task ContentAwareScale()
    {
        if (ActiveDocument is not { } doc) return;
        if (!doc.IsTransforming && doc.ContentAwareRasterizePrompt() is { } prompt)
        {
            if (FilterDialogProvider is not { } dialogs || !await dialogs.AskRasterizeAsync(prompt)) return;
            doc.RasterizeSelected();
        }
        doc.BeginContentAwareScale();
    }
}
