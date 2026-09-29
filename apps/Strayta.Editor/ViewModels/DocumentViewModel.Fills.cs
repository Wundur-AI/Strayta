using System.Diagnostics;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Editor.ViewModels;

// Edit › Fill…, Edit › Stroke… and Edit › Define Pattern… on the document (EditorViewModel.Fills.cs runs the dialogs).
public sealed partial class DocumentViewModel
{
    /// <summary>Time the last Fill or Stroke took from OK to the edit, for the self-test.</summary>
    public double LastFillMs { get; private set; }

    /// <summary>
    /// Fills the selection (the whole canvas when nothing is selected) of the selected layer with
    /// <paramref name="options"/>, as one undo step.
    /// </summary>
    public async Task FillWithAsync(FillOptions options, string description = "Fill")
    {
        if (EditableLayer("fill") is not { } layer) return;
        var clock = Stopwatch.StartNew();
        var selection = Selection ?? SelectionMask.All(Model.Bounds);
        await EditPixelsAsync(layer, description, doc => FillPainter.Fill(layer, selection, options, doc.ColorMode, doc.BitDepth));
        LastFillMs = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>Edit › Stroke: paints a band <paramref name="width"/> pixels wide along the selection's outline, as one undo step.</summary>
    public async Task StrokeSelectionAsync(int width, FillOptions options, StrokeLocation location)
    {
        if (Selection is not { } selection)
        {
            Notice = "Make a selection first: Stroke draws along its outline.";
            return;
        }
        if (EditableLayer("stroke") is not { } layer) return;
        var clock = Stopwatch.StartNew();
        var canvas = Model.Bounds;
        await EditPixelsAsync(layer, "Stroke", doc =>
            SelectionStroke.Band(selection, width, location, canvas) is { } band
                ? FillPainter.Fill(layer, band, options, doc.ColorMode, doc.BitDepth)
                : (layer.Pixels, layer.Bounds));
        LastFillMs = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Edit › Define Pattern: the image as shown (all visible layers) inside the selection's bounds, or the whole
    /// canvas, as a new pattern named after the document. Null (with a notice) when it cannot be made. Photoshop asks
    /// for a rectangular, unfeathered selection; any selection's bounding box is used here.
    /// </summary>
    public async Task<Pattern?> CapturePatternAsync()
    {
        var area = (Selection?.Bounds ?? Model.Bounds).Intersect(Model.Bounds);
        if (area.IsEmpty)
        {
            Notice = "The selection is outside the canvas.";
            return null;
        }
        const int MaxSide = 2000;
        if (area.Width > MaxSide || area.Height > MaxSide)
        {
            Notice = $"A pattern can be at most {MaxSide}×{MaxSide} pixels. Select a smaller area.";
            return null;
        }
        if (await CompositeAsync() is not { } render)
        {
            Notice = "Nothing to capture yet. Wait for the document to finish rendering.";
            return null;
        }
        int w = Model.Width;
        var rgba = new byte[area.Width * area.Height * 4];
        bool alpha = false;
        for (int y = 0; y < area.Height; y++)
        {
            Array.Copy(render, ((area.Top + y) * w + area.Left) * 4, rgba, y * area.Width * 4, area.Width * 4);
            for (int x = 0; x < area.Width && !alpha; x++) alpha = rgba[(y * area.Width + x) * 4 + 3] != 255;
        }
        string name = Path.GetFileNameWithoutExtension(Title.TrimEnd('*', ' ')) is { Length: > 0 } t ? t : "Pattern";
        return Pattern.FromRgba(Pattern.NewId(), name, area.Width, area.Height, rgba, alpha);
    }
}
