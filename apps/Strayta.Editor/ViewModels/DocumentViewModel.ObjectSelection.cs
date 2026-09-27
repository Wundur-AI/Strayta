using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Rendering;
using Strayta.Segmentation;

namespace Strayta.Editor.ViewModels;

// Object Selection and Select › Subject: local AI segmentation (Strayta.Segmentation). The slow step, encoding
// the image, runs once per image in the background and is cached; each box or click afterwards only runs the
// small prompt decoder.
public sealed partial class DocumentViewModel
{
    private (int Version, byte[] Rgba)? _sampleRender;
    private int _objectRequest, _analyzing;

    /// <summary>True while a segmentation model is analyzing the image (shown in the status bar).</summary>
    [ObservableProperty] public partial bool IsAnalyzing { get; private set; }

    /// <summary>Milliseconds from prompt to selection for the last Object Selection (decode, upscale, combine).</summary>
    public double LastObjectSelectionMs { get; private set; }

    private static SegmentationEngine Engine => SegmentationEngine.Shared;

    /// <summary>
    /// A finished Object Selection drag (the box) or click (the object under the cursor), combined with the current
    /// selection by the gesture's mode (Shift adds, Option subtracts).
    /// </summary>
    public async Task ApplyObjectSelectionAsync(SelectionGesture gesture)
    {
        if (!Engine.CanSelectObjects)
        {
            Notice = SegmentationModels.FetchHint;
            return;
        }
        SamPrompt? prompt = !gesture.Box.IsEmpty ? SamPrompt.Rectangle(gesture.Box)
            : gesture.Points is [var p] ? SamPrompt.Click(p.X, p.Y)
            : null;
        if (prompt is null) return;

        int request = ++_objectRequest;
        var current = Selection;
        var canvas = Model.Bounds;
        if (await ObjectEmbeddingAsync() is not { } embedding) return;

        var sw = Stopwatch.StartNew();
        SelectionMask? next;
        try
        {
            next = await Task.Run(() => SelectionMask.Combine(current, Engine.SelectObject(embedding, prompt, canvas), gesture.Mode));
        }
        catch (Exception ex)
        {
            Notice = $"Object Selection failed: {ex.Message}";
            return;
        }
        LastObjectSelectionMs = sw.Elapsed.TotalMilliseconds;
        // Dropped if another prompt finished first or the selection changed meanwhile (undo, Select All, ...).
        if (request != _objectRequest || !ReferenceEquals(current, Selection)) return;
        if (next is null && gesture.Mode != SelectionMode.Subtract && gesture.Mode != SelectionMode.Intersect)
        {
            Notice = "No object was found there. Drag a box around the object, or click on it.";
            return;
        }
        Notice = "";
        SetSelection(next, "Object Selection");
    }

    /// <summary>Select › Subject (and the options bar button): selects the main subject of the image.</summary>
    public async Task SelectSubjectAsync()
    {
        if (!Engine.CanSelectSubject)
        {
            Notice = SegmentationModels.FetchHint;
            return;
        }
        int request = ++_objectRequest;
        var current = Selection;
        var canvas = Model.Bounds;
        if (await SampleAsync(Editor.ObjectSampleAllLayers) is not { } sample) return;
        IsAnalyzing = ++_analyzing > 0;
        Notice = "Selecting the subject…";
        try
        {
            var subject = await Task.Run(() => Engine.SelectSubject(sample.Image(), canvas));
            if (request != _objectRequest || !ReferenceEquals(current, Selection)) return;
            if (subject is null)
            {
                Notice = "No subject was found.";
                return;
            }
            Notice = "";
            SetSelection(subject, "Select Subject");
        }
        catch (Exception ex)
        {
            Notice = $"Select Subject failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = --_analyzing > 0;
        }
    }

    /// <summary>
    /// Starts analyzing the image in the background (when the tool is picked or this document becomes active), so
    /// the first box usually gets an instant answer.
    /// </summary>
    public void PrepareObjectSelection()
    {
        if (Engine.CanSelectObjects) _ = ObjectEmbeddingAsync(quiet: true);
    }

    /// <summary>
    /// The SAM embedding for what Object Selection samples now: the whole document render (Sample All Layers) or
    /// the selected layer's pixels. Cached by content identity, so it is recomputed only after the pixels change.
    /// </summary>
    private async Task<SamEmbedding?> ObjectEmbeddingAsync(bool quiet = false, bool? sampleAll = null)
    {
        if (await SampleAsync(sampleAll ?? Editor.ObjectSampleAllLayers, quiet) is not { } sample) return null;
        if (Engine.TryGetEmbedding(sample.Source, sample.Content) is { } cached) return cached.MovedTo(sample.Placement);

        IsAnalyzing = ++_analyzing > 0;
        if (!quiet) Notice = "Analyzing the image for Object Selection…";
        try
        {
            var embedding = await Engine.GetEmbeddingAsync(sample.Source, sample.Content, sample.Image);
            if (Notice.StartsWith("Analyzing", StringComparison.Ordinal)) Notice = "";
            return embedding.MovedTo(sample.Placement);
        }
        catch (Exception ex)
        {
            Notice = $"Object Selection could not analyze the image: {ex.Message}";
            return null;
        }
        finally
        {
            IsAnalyzing = --_analyzing > 0;
        }
    }

    /// <summary>What the models look at, identified so embeddings can be cached: the source (this document or
    /// a layer), the content (a pixel array that is replaced, never modified, on every edit) and the image.</summary>
    private sealed record Sample(object Source, object Content, PixelRect Placement, Func<RgbaImage> Image);

    private async Task<Sample?> SampleAsync(bool sampleAll, bool quiet = false)
    {
        if (sampleAll)
        {
            var rgba = await DocumentPixelsAsync();
            if (rgba is null) return null;
            var image = new RgbaImage(rgba, Model.Width, Model.Height, Model.Bounds);
            return new Sample(this, rgba, Model.Bounds, () => image);
        }

        if (SelectedLayer?.Node is PixelLayer { Pixels: { } pixels } layer && !layer.Bounds.IsEmpty)
        {
            var bounds = layer.Bounds;
            var palette = Model.Palette;
            return new Sample(layer, pixels, bounds, () => new RgbaImage(RgbaConverter.ToRgba8(pixels, palette), pixels.Width, pixels.Height, bounds));
        }
        if (!quiet) Notice = "Object Selection looks at the selected layer: select a layer with pixels, or turn on Sample All Layers.";
        return null;
    }

    /// <summary>
    /// The full-resolution render of the document as it is now: the one on screen if it is current, otherwise a
    /// fresh render in the background. Null if the document changed while rendering.
    /// </summary>
    private async Task<byte[]?> DocumentPixelsAsync()
    {
        int version = _modelVersion;
        if (_sampleRender is { } s && s.Version == version) return s.Rgba;
        if (_lastRender is { } shown && _lastRenderVersion == version)
        {
            _sampleRender = (version, shown);
            return shown;
        }
        var doc = Model;
        var rgba = await Task.Run(() =>
        {
            using var renderer = new CpuRenderer();
            return renderer.Render(doc).ToRgba8();
        });
        if (version != _modelVersion) return null;
        _sampleRender = (version, rgba);
        return rgba;
    }

    // ---- Benchmark (STRAYTA_SEGBENCH=1) -------------------------------------------------------------------

    /// <summary>
    /// Times the segmentation pipeline on this document: image encoding, prompts (box and click), and Select Subject,
    /// with the process's memory use. Prints SEGBENCH lines; the selection is undone afterwards.
    /// </summary>
    public async Task RunSegmentationBenchmarkAsync()
    {
        if (!Engine.CanSelectObjects || !Engine.CanSelectSubject)
        {
            Console.WriteLine($"SEGBENCH skipped: {SegmentationModels.FetchHint}");
            return;
        }
        long before = Environment.WorkingSet;
        var total = Stopwatch.StartNew();
        await ObjectEmbeddingAsync();
        Console.WriteLine($"SEGBENCH {Model.Width}x{Model.Height} first embedding (model load + render reuse + encode) {total.ElapsedMilliseconds} ms; " +
                          $"encoder {Engine.LastTimings.PreprocessMs:F0} ms preprocess + {Engine.LastTimings.InferenceMs:F0} ms inference");

        // Force a second, warm encode of the same image for the steady-state number.
        var image = new RgbaImage((await DocumentPixelsAsync())!, Model.Width, Model.Height, Model.Bounds);
        var warm = await Task.Run(() => Engine.Encode(image));
        Console.WriteLine($"SEGBENCH warm encode {warm.EncodeTime.TotalMilliseconds:F0} ms");

        int w = Model.Width, h = Model.Height;
        var box = new PixelRect(w / 4, h / 4, w * 3 / 4, h * 3 / 4);
        var decode = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            var g = i % 2 == 0
                ? new SelectionGesture(CanvasTool.ObjectSelect, SelectionMode.Replace, box, [])
                : new SelectionGesture(CanvasTool.ObjectSelect, SelectionMode.Replace, PixelRect.Empty, [new System.Numerics.Vector2(w / 2f, h / 2f)]);
            await ApplyObjectSelectionAsync(g);
            decode.Add(LastObjectSelectionMs);
        }
        decode.Sort();
        Console.WriteLine($"SEGBENCH prompt → selection (decode + upscale + edge refine + combine): median {decode[decode.Count / 2]:F0} ms, " +
                          $"max {decode[^1]:F0} ms; decoder alone {Engine.LastTimings.InferenceMs:F0} ms");

        var sw = Stopwatch.StartNew();
        await SelectSubjectAsync();
        long first = sw.ElapsedMilliseconds;
        sw.Restart();
        await SelectSubjectAsync();
        Console.WriteLine($"SEGBENCH Select Subject {first} ms first (includes model load), {sw.ElapsedMilliseconds} ms warm " +
                          $"({Engine.LastTimings.InferenceMs:F0} ms inference); selection {Selection?.Bounds}");
        GC.Collect();
        Console.WriteLine($"SEGBENCH working set {before >> 20} MB before, {Environment.WorkingSet >> 20} MB after (models and cached embeddings loaded)");
        while (CanUndo) Undo();
    }
}
