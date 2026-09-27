using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Rendering;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

// The Crop tool (C), Image › Image Size, Canvas Size, Crop and Trim. All of them change the canvas through one
// CanvasEdit. While the Crop tool is active the document shows a CropBox: an overlay the canvas draws and drags
// without re-rendering anything; the document changes only when the crop is committed.
public sealed partial class DocumentViewModel
{
    private bool _canvasPending;
    private int _canvasVersion;
    private bool _canvasFrameHooked;
    private int _cropLiveLayers; // type, smart object, fill and shape layers a turned crop would rasterize

    /// <summary>The open crop (the Crop tool is active on this document), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCropping))]
    public partial CropBox? CropBox { get; private set; }

    public bool IsCropping => CropBox is not null;

    /// <summary>Why committing the crop will change more than the canvas (e.g. rasterize type when the image is turned).</summary>
    [ObservableProperty] public partial string CropNotice { get; private set; } = "";

    /// <summary>Opens or closes the crop to match the editor's tool (called when the tool or the active document changes).</summary>
    public void SyncCropSession()
    {
        bool wanted = Editor.Tool == CanvasTool.Crop;
        if (wanted && CropBox is null && !_canvasPending) BeginCrop();
        else if (!wanted && CropBox is { Locked: false } box)
        {
            // Photoshop asks and defaults to cropping; switching tools applies a changed crop (it can be undone).
            if (box.IsModified) _ = CommitCropAsync();
            else EndCrop();
        }
    }

    /// <summary>Shows a crop box around the whole canvas.</summary>
    public void BeginCrop()
    {
        if (CropBox is not null) return;
        if (IsTransforming) _ = CommitTransformAsync();
        var box = new CropBox(Model.Width, Model.Height);
        if (Editor.CropAspectRatio(Model) is { } ratio) box.SetAspectRatio(ratio);
        _cropLiveLayers = Model.Root.Descendants().Count(n => n is PixelLayer { Pixels: not null } && CanvasOperations.IsLive(n));
        box.Changed += OnCropChanged;
        CropBox = box;
        OnCropChanged();
    }

    private void EndCrop()
    {
        if (CropBox is { } box) box.Changed -= OnCropChanged;
        CropBox = null;
        CropNotice = "";
    }

    private void OnCropChanged()
    {
        if (CropBox is not { } box) return;
        int live = box.IsRotated ? _cropLiveLayers : 0;
        CropNotice = live == 0 ? ""
            : $"Turning the image rasterizes {live} type, smart object or shape layer{(live == 1 ? "" : "s")}.";
    }

    /// <summary>Esc: puts the box back around the whole image.</summary>
    public void CancelCrop() => CropBox?.Reset();

    /// <summary>
    /// Enter, double-click or ✓: applies the crop as one undoable step. The box stays on screen (locked) until the
    /// cropped image is shown, then a fresh box frames the new canvas.
    /// </summary>
    public async Task CommitCropAsync()
    {
        if (CropBox is not { Locked: false } box) return;
        if (!box.IsModified)
        {
            if (Editor.Tool != CanvasTool.Crop) EndCrop();
            return;
        }
        box.Locked = true;
        var (map, width, height) = (box.ResultMap, box.ResultWidth, box.ResultHeight);
        bool delete = Editor.CropDeletePixels;
        var fill = BackgroundFill();
        if (!await ChangeCanvasAsync("Crop", () => CanvasOperations.Crop(Model, map, width, height, delete, fill)))
            box.Locked = false;
    }

    /// <summary>Image › Crop: crops to the selection's bounds (and drops the selection).</summary>
    public async Task CropToSelectionAsync()
    {
        if (Selection is not { } selection) return;
        var rect = selection.Bounds.Intersect(Model.Bounds);
        if (rect.IsEmpty || rect == Model.Bounds) return;
        CloseCropForCommand();
        var fill = BackgroundFill();
        await ChangeCanvasAsync("Crop", () => CanvasOperations.Crop(Model, rect, deleteCroppedPixels: true, fill));
    }

    /// <summary>Image › Trim: crops away transparent pixels, or pixels of a corner's color, from the chosen sides.</summary>
    public async Task TrimAsync(TrimBasis basis, bool top, bool left, bool bottom, bool right)
    {
        CloseCropForCommand();
        var doc = Model;
        var rect = await Task.Run(() =>
        {
            using var renderer = new CpuRenderer();
            var rgba = renderer.Render(doc).ToRgba8();
            return CanvasOperations.TrimBounds(rgba, doc.Width, doc.Height, basis, top, left, bottom, right);
        });
        if (rect.IsEmpty)
        {
            Notice = "Trim would remove the whole image.";
            return;
        }
        if (rect == Model.Bounds)
        {
            Notice = "There is nothing to trim.";
            return;
        }
        await ChangeCanvasAsync("Trim", () => CanvasOperations.Crop(doc, rect, deleteCroppedPixels: true));
    }

    /// <summary>Image › Image Size. Unchanged pixel dimensions with a new resolution change only the resolution.</summary>
    public async Task ResizeImageAsync(int width, int height, ResampleMethod method, double resolution)
    {
        CloseCropForCommand();
        if (width == Model.Width && height == Model.Height)
        {
            if (resolution != Model.Resolution) Apply(new CanvasEdit(Model, null, resolution, Selection, s => Selection = s, "Image Size"));
            return;
        }
        await ChangeCanvasAsync("Image Size", () => CanvasOperations.ResizeImage(Model, width, height, method), resolution);
    }

    /// <summary>Image › Canvas Size: the old canvas placed at (<paramref name="offsetX"/>, <paramref name="offsetY"/>) on the new one.</summary>
    public async Task ResizeCanvasAsync(int width, int height, int offsetX, int offsetY, Avalonia.Media.Color extension)
    {
        CloseCropForCommand();
        if (width == Model.Width && height == Model.Height && offsetX == 0 && offsetY == 0) return;
        var fill = ColorComponents(extension);
        await ChangeCanvasAsync("Canvas Size", () => CanvasOperations.ResizeCanvas(Model, width, height, offsetX, offsetY, fill));
    }

    /// <summary>Menu commands work on the whole image: an open, unchanged crop box is closed first (it comes back afterwards).</summary>
    private void CloseCropForCommand()
    {
        if (IsTransforming) _ = CommitTransformAsync();
        if (CropBox is { Locked: false }) EndCrop();
    }

    /// <summary>
    /// Computes a canvas change in the background and applies it as one step; false (with a notice) if it failed.
    /// Every layer's resampling runs off the UI thread, so the window stays responsive.
    /// </summary>
    private async Task<bool> ChangeCanvasAsync(string description, Func<CanvasChange> compute, double? resolution = null)
    {
        if (_canvasPending) return false;
        _canvasPending = true;
        var sw = Stopwatch.StartNew();
        var busy = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (t, _) =>
        {
            ((DispatcherTimer)t!).Stop();
            IsBusy = true;
        });
        busy.Start();
        try
        {
            var doc = Model;
            var selection = Selection;
            double ppi = resolution ?? doc.Resolution;
            var edit = await Task.Run(() => new CanvasEdit(doc, compute(), ppi, selection, s => Selection = s, description));
            LastCanvasChangeMs = sw.Elapsed.TotalMilliseconds;
            Apply(edit);
            return true;
        }
        catch (Exception ex)
        {
            _canvasPending = false;
            Notice = $"Could not apply {description}: {ex.Message}";
            return false;
        }
        finally
        {
            busy.Stop();
            IsBusy = false;
        }
    }

    /// <summary>Milliseconds the last Crop / Image Size / Canvas Size took to compute (diagnostics and self-test).</summary>
    public double LastCanvasChangeMs { get; private set; }

    /// <summary>
    /// Called from <see cref="AfterChange"/> when a <see cref="CanvasEdit"/> is applied, undone or redone: the render
    /// snapshots, the Photoshop composite and the status follow the new canvas. The view keeps the old image (and
    /// crop box) until the first frame of the new canvas arrives, then switches size in one go, so nothing flashes
    /// stretched.
    /// </summary>
    private void OnCanvasChanged()
    {
        _snapshot = new PreviewDocument(Model, 1);
        _preview = null;
        _lastRender = null;
        _lastRenderVersion = -1;
        _diffBitmap = null;
        _reference = Model.Composite is { } comp ? RgbaConverter.ToRgba8(comp, Model.Palette) : null;
        _referenceBitmap = _reference is null ? null : BitmapFactory.FromRgba(_reference, Model.Width, Model.Height);
        HasReference = _reference is not null;
        if (!HasReference) Mode = ViewMode.Strayta;
        Status = $"{Model.Width}×{Model.Height} · {Model.ColorMode} {Model.BitDepth}-bit";
        if (CropBox is { } box) box.Locked = true;

        _canvasPending = true;
        _canvasVersion = _modelVersion + 1; // the render AfterChange requests next
        if (!_canvasFrameHooked)
        {
            FrameDisplayed += OnCanvasFrame;
            _canvasFrameHooked = true;
        }
        // Should rendering fail, do not leave the view on the old size.
        int version = _canvasVersion;
        DispatcherTimer.RunOnce(() => { if (_canvasVersion == version) FinishCanvasChange(); }, TimeSpan.FromSeconds(3));
    }

    private void OnCanvasFrame(bool full)
    {
        if (_canvasPending && _displayedVersion >= _canvasVersion) FinishCanvasChange();
    }

    private void FinishCanvasChange()
    {
        if (!_canvasPending) return;
        _canvasPending = false;
        OnPropertyChanged(nameof(DocumentSize));
        EndCrop();
        SyncCropSession();
        CanvasChanged?.Invoke();
    }

    /// <summary>Raised once the view shows the new canvas after a crop or resize (and its undo or redo).</summary>
    public event Action? CanvasChanged;

    /// <summary>True between applying a canvas change and showing its first frame.</summary>
    public bool IsCanvasChangePending => _canvasPending;

    /// <summary>The Background layer's new area gets the background color, as in Photoshop.</summary>
    private float[] BackgroundFill() => ColorComponents(Editor.BackgroundColor);

    private float[] ColorComponents(Avalonia.Media.Color c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        return Model.ColorMode switch
        {
            ColorMode.Grayscale => [0.299f * r + 0.587f * g + 0.114f * b],
            _ => [r, g, b],
        };
    }

    /// <summary>
    /// Times Image Size (halving, then back) and a straightened crop on the current document, reporting the
    /// compute time and when the new canvas is on screen (STRAYTA_CROPBENCH=1, or =new on a generated 4000×3000
    /// layered document).
    /// </summary>
    public async Task RunCanvasBenchmarkAsync()
    {
        async Task<(double Compute, double Shown)> Timed(Func<Task> action)
        {
            var sw = Stopwatch.StartNew();
            var shown = new TaskCompletionSource();
            void Done() => shown.TrySetResult();
            CanvasChanged += Done;
            await action();
            await Task.WhenAny(shown.Task, Task.Delay(10_000));
            CanvasChanged -= Done;
            return (LastCanvasChangeMs, sw.Elapsed.TotalMilliseconds);
        }

        int w = Model.Width, h = Model.Height;
        for (int run = 1; run <= 2; run++) // the first run includes JIT compilation
        {
            var half = await Timed(() => ResizeImageAsync(w / 2, h / 2, ResampleMethod.Bicubic, Model.Resolution));
            Console.WriteLine($"CROPBENCH image size {w}x{h} -> {Model.Width}x{Model.Height} (run {run}): compute={half.Compute:F0}ms on screen={half.Shown:F0}ms");
            var undo = await Timed(() => { Undo(); return Task.CompletedTask; });
            Console.WriteLine($"CROPBENCH undo image size: on screen={undo.Shown:F0}ms");
        }

        // Crop box drags are overlay-only: count how many document renders a drag causes.
        Editor.Tool = CanvasTool.Crop;
        SyncCropSession();
        var box = CropBox!;
        int renders = 0;
        void OnFrame(bool full) => renders++;
        FrameDisplayed += OnFrame;
        for (int last = -1; last != renders;) { last = renders; await Task.Delay(700); } // earlier renders settle first
        renders = 0;
        var drag = Stopwatch.StartNew();
        box.BeginDrag(Editing.TransformHandle.BottomRight, w, h);
        for (int i = 1; i <= 120; i++)
        {
            box.DragTo(w - i * w / 400.0, h - i * h / 400.0, shift: false, alt: false);
            await Task.Delay(8);
        }
        box.EndDrag();
        var (cx, cy) = box.Center;
        box.BeginDrag(Editing.TransformHandle.Rotate, box.Right + 50, cy);
        for (int i = 1; i <= 60; i++)
        {
            var (sin, cos) = Math.SinCos(i * 0.1 * Math.PI / 180);
            box.DragTo(cx + (box.Right + 50 - cx) * cos, cy + (box.Right + 50 - cx) * sin, shift: false, alt: false);
            await Task.Delay(8);
        }
        box.EndDrag();
        FrameDisplayed -= OnFrame;
        Console.WriteLine($"CROPBENCH crop box drag {drag.ElapsedMilliseconds}ms, 180 moves: document renders during the drag={renders} (overlay only)");
        var crop = await Timed(CommitCropAsync);
        Console.WriteLine($"CROPBENCH rotated crop {w}x{h} -> {Model.Width}x{Model.Height} ({box.Angle:F1}°): compute={crop.Compute:F0}ms on screen={crop.Shown:F0}ms");
        var undoCrop = await Timed(() => { Undo(); return Task.CompletedTask; });
        Console.WriteLine($"CROPBENCH undo crop: on screen={undoCrop.Shown:F0}ms");

        CropBox!.BeginDrag(Editing.TransformHandle.TopLeft, 0, 0);
        CropBox.DragTo(w / 10, h / 10, shift: false, alt: false);
        CropBox.EndDrag();
        var plain = await Timed(CommitCropAsync);
        Console.WriteLine($"CROPBENCH straight crop -> {Model.Width}x{Model.Height}: compute={plain.Compute:F0}ms on screen={plain.Shown:F0}ms");
        Undo();
        Editor.Tool = CanvasTool.Move;
    }
}
