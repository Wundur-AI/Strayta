using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

/// <summary>What Layer › New › Artboard… asks for.</summary>
public sealed record NewArtboardRequest(string Name, int Width, int Height, string Preset, ArtboardBackground Background, (byte R, byte G, byte B) Color);

/// <summary>The artboard dialogs, implemented by the main window (MainWindow.Artboards.cs).</summary>
public interface IArtboardDialogs
{
    Task<NewArtboardRequest?> AskNewArtboardAsync(NewArtboardRequest defaults);
}

// Artboards: the Artboard tool (in the Move tool's slot, as in Photoshop), its options bar, and the Layer › New commands.
public sealed partial class EditorViewModel
{
    public bool IsArtboardTool { get => Tool == CanvasTool.Artboard; set { if (value) Tool = CanvasTool.Artboard; } }

    /// <summary>The options bar's size list: "Custom", then every preset.</summary>
    public IReadOnlyList<string> ArtboardPresetNames { get; } = ["Custom", .. ArtboardPresets.All.Select(p => $"{p.Category}: {p}")];

    public static IReadOnlyList<string> ArtboardBackgroundNames { get; } = ["White", "Black", "Transparent", "Other"];

    private LayerGroup? SelectedArtboardGroup => ActiveDocument?.SelectedArtboard;

    public bool HasSelectedArtboard => SelectedArtboardGroup is not null;

    /// <summary>The selected artboard's size preset (0: Custom); choosing one resizes it.</summary>
    public int ArtboardPresetIndex
    {
        get => SelectedArtboardGroup?.Artboard is { } a && ArtboardPresets.Matching(a.Rect.Width, a.Rect.Height) is { } p
            ? ArtboardPresets.All.ToList().IndexOf(p) + 1
            : 0;
        set
        {
            if (value <= 0 || value > ArtboardPresets.All.Count) return;
            var preset = ArtboardPresets.All[value - 1];
            ActiveDocument?.SetSelectedArtboardSize(preset.Width, preset.Height, preset.Name);
            NotifyArtboardOptions();
        }
    }

    public double ArtboardWidth
    {
        get => SelectedArtboardGroup?.Artboard?.Rect.Width ?? 0;
        set
        {
            if (SelectedArtboardGroup?.Artboard is { } a && value >= 1) ActiveDocument?.SetSelectedArtboardSize((int)Math.Round(value), a.Rect.Height);
            NotifyArtboardOptions();
        }
    }

    public double ArtboardHeight
    {
        get => SelectedArtboardGroup?.Artboard?.Rect.Height ?? 0;
        set
        {
            if (SelectedArtboardGroup?.Artboard is { } a && value >= 1) ActiveDocument?.SetSelectedArtboardSize(a.Rect.Width, (int)Math.Round(value));
            NotifyArtboardOptions();
        }
    }

    public int ArtboardBackgroundIndex
    {
        get => SelectedArtboardGroup?.Artboard is { } a ? (int)a.Background - 1 : 0;
        set
        {
            if (value is < 0 or > 3) return;
            // "Other" takes the background color swatch, as Photoshop's Other… picks a color.
            (byte, byte, byte)? color = value == 3 ? (BackgroundColor.R, BackgroundColor.G, BackgroundColor.B) : null;
            ActiveDocument?.SetSelectedArtboardBackground((ArtboardBackground)(value + 1), color);
            NotifyArtboardOptions();
        }
    }

    /// <summary>Refreshes the Artboard tool's options bar (after the selection, the document or an undo changed).</summary>
    public void NotifyArtboardOptions()
    {
        OnPropertyChanged(nameof(IsArtboardTool));
        OnPropertyChanged(nameof(HasSelectedArtboard));
        OnPropertyChanged(nameof(ArtboardPresetIndex));
        OnPropertyChanged(nameof(ArtboardWidth));
        OnPropertyChanged(nameof(ArtboardHeight));
        OnPropertyChanged(nameof(ArtboardBackgroundIndex));
    }

    /// <summary>The options bar's "Add Artboard": one more beside the selected artboard (or a first one covering the canvas).</summary>
    [RelayCommand]
    private void AddArtboard()
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.SelectedArtboard is { } selected) doc.AddAdjacentArtboard(selected, ArtboardSide.Right);
        else doc.NewArtboard(doc.Model.Bounds);
        NotifyArtboardOptions();
    }

    /// <summary>Layer › New › Artboard…</summary>
    [RelayCommand]
    private async Task NewArtboard()
    {
        if (ActiveDocument is not { } doc || _dialogs is not IArtboardDialogs dialogs) return;
        var preset = ArtboardPresets.All[0];
        var defaults = new NewArtboardRequest(Editing.LayerFactory.NextName(doc.Model, "Artboard"), preset.Width, preset.Height, preset.Name,
            ArtboardBackground.White, (255, 255, 255));
        if (await dialogs.AskNewArtboardAsync(defaults) is not { } request) return;
        // Beside the existing artboards (or at the top left of an empty document), as Photoshop places new ones.
        var right = Artboards.Of(doc.Model).Select(g => g.Artboard!.Rect.Right).DefaultIfEmpty(int.MinValue).Max();
        int left = right == int.MinValue ? 0 : right + DocumentViewModel.ArtboardGap;
        doc.NewArtboard(new PixelRect(left, 0, left + request.Width, request.Height), request.Name, request.Background, request.Color, request.Preset);
        NotifyArtboardOptions();
    }

    [RelayCommand]
    private void ArtboardFromLayers()
    {
        ActiveDocument?.ArtboardFromLayers();
        NotifyArtboardOptions();
    }

    [RelayCommand]
    private void ArtboardFromGroup()
    {
        ActiveDocument?.ArtboardFromGroup();
        NotifyArtboardOptions();
    }
}
