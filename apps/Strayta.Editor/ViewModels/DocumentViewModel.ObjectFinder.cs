using System.Diagnostics;
using System.Numerics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Segmentation;

namespace Strayta.Editor.ViewModels;

// Object Selection's Object Finder and Lasso mode. Once the image has been analyzed for Object Selection, the objects
// in it are found in the background (SAM prompted with a grid of points from one low-priority thread, through a
// two-thread decoder, cancelled as soon as the document changes). Hovering then highlights the innermost object
// under the pointer and a click selects it without running the model again. In Lasso mode the drawn path is the
// prompt's box and also bounds the result.
public sealed partial class DocumentViewModel
{
    /// <summary>Grid points per side the Object Finder prompts SAM with (Photoshop-like coverage for a laptop CPU).</summary>
    public const int ObjectFinderGrid = 10;

    private sealed class FinderRun
    {
        public required SamEmbedding Embedding { get; init; }
        public required int Version { get; init; }
        public required CancellationTokenSource Cancel { get; init; }
        public Task Task = Task.CompletedTask;
        public IReadOnlyList<FoundObject> Objects = [];
        public bool Done;
        public double ElapsedMs;
    }

    private FinderRun? _finder;
    private Task? _finderPreparing;

    /// <summary>The outline of the object under the pointer while hovering with Object Selection (image coordinates).</summary>
    [ObservableProperty] public partial IReadOnlyList<Vector2[]>? ObjectHoverOutline { get; private set; }

    /// <summary>The objects found so far for the current image (empty while none, or when the image changed since).</summary>
    public IReadOnlyList<FoundObject> FoundObjects => _finder is { } f && f.Version == _modelVersion ? f.Objects : [];

    /// <summary>True once the Object Finder has gone through the whole image, and how long it took (self-test, benchmark).</summary>
    public bool ObjectFinderDone => _finder is { Done: true } f && f.Version == _modelVersion;
    public double ObjectFinderMs => _finder?.ElapsedMs ?? 0;

    /// <summary>Analyzes the image for Object Selection, then (with Object Finder on) finds its objects in the background.</summary>
    private async Task PrepareObjectFinderAsync()
    {
        var embedding = await ObjectEmbeddingAsync(quiet: true);
        // Only for the active document while Object Selection is the tool (it may have changed during the analysis).
        if (embedding is null || !Editor.ObjectFinder || !Editor.IsObjectSelectTool || !ReferenceEquals(Editor.ActiveDocument, this)) return;
        StartObjectFinder(embedding);
    }

    private void StartObjectFinder(SamEmbedding embedding)
    {
        int version = _modelVersion;
        if (_finder is { } running && running.Version == version && !running.Cancel.IsCancellationRequested
            && ReferenceEquals(running.Embedding.Image.Pixels, embedding.Image.Pixels)) return;
        _finder?.Cancel.Cancel();
        var run = new FinderRun { Embedding = embedding, Version = version, Cancel = new CancellationTokenSource() };
        _finder = run;
        var token = run.Cancel.Token;
        var clock = Stopwatch.StartNew();
        // A dedicated background thread at low priority: the finder takes seconds and must never slow editing.
        run.Task = Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            try
            {
                var objects = Engine.FindObjects(embedding, ObjectFinderGrid, token, found =>
                {
                    foreach (var o in found) _ = o.Outline; // traced here, not on the UI thread when hovered
                    Dispatcher.UIThread.Post(() => run.Objects = found);
                });
                foreach (var o in objects) _ = o.Outline;
                Dispatcher.UIThread.Post(() =>
                {
                    run.Objects = objects;
                    run.Done = true;
                    run.ElapsedMs = clock.Elapsed.TotalMilliseconds;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
                // The app is closing.
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>Waits for the Object Finder to finish the current image (self-test and benchmark).</summary>
    internal async Task<bool> WaitForObjectFinderAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (ObjectFinderDone) return true;
            if (_finder is null && _finderPreparing is null) _finderPreparing = PrepareObjectFinderAsync();
            await Task.Delay(50);
        }
        return false;
    }

    /// <summary>The pointer over the image with Object Selection: highlights the object under it (null clears).</summary>
    public void HoverObject(Vector2? at)
    {
        if (at is not { } p || !Editor.ObjectFinder || !Editor.IsObjectSelectTool)
        {
            ObjectHoverOutline = null;
            return;
        }
        if (_finder is not { } f || f.Version != _modelVersion)
        {
            // The image changed since it was analyzed: analyze it again (once) and find its objects.
            ObjectHoverOutline = null;
            if (Engine.CanSelectObjects && (_finderPreparing is null || _finderPreparing.IsCompleted)) _finderPreparing = PrepareObjectFinderAsync();
            return;
        }
        var found = ObjectFinder.ObjectAt(f.Objects, p.X, p.Y);
        ObjectHoverOutline = found?.Outline;
    }

    public void ClearObjectHover() => ObjectHoverOutline = null;

    /// <summary>
    /// Stops an unfinished Object Finder run (another tool or document took over), so it uses no CPU while it is not
    /// needed. The objects found so far stay; picking Object Selection again starts it over.
    /// </summary>
    public void PauseObjectFinder()
    {
        if (_finder is { Done: false } f) f.Cancel.Cancel();
        ObjectHoverOutline = null;
    }

    /// <summary>
    /// A click with Object Finder on over an object it found: selects that object (its mask snapped to the image's
    /// edges), combined by the click's mode. False when there is none, so the click prompts the model as usual.
    /// </summary>
    private async Task<bool> TrySelectFoundObjectAsync(SelectionGesture gesture)
    {
        if (!Editor.ObjectFinder || gesture is not { Box.IsEmpty: true, Points: [var p] }) return false;
        if (_finder is not { } f || f.Version != _modelVersion || ObjectFinder.ObjectAt(f.Objects, p.X, p.Y) is not { } found) return false;
        int request = ++_objectRequest;
        var current = Selection;
        var canvas = Model.Bounds;
        var guide = f.Embedding.Image;
        var clock = Stopwatch.StartNew();
        var next = await Task.Run(() => SelectionMask.Combine(current, MaskUpscaler.ToSelection(found.Logits, canvas, guide: guide), gesture.Mode));
        LastObjectSelectionMs = clock.Elapsed.TotalMilliseconds;
        if (request != _objectRequest || !ReferenceEquals(current, Selection)) return true;
        Notice = "";
        ObjectHoverOutline = null;
        SetSelection(next, "Object Selection");
        return true;
    }

    /// <summary>Object Selection in Lasso mode keeps the result inside the drawn path.</summary>
    private static SelectionMask? ClipToLasso(SelectionMask? mask, SelectionGesture gesture, PixelRect canvas) =>
        gesture.Points.Count >= 3 && mask is not null
            ? SelectionMask.Combine(mask, SelectionMask.Polygon(gesture.Points, canvas), SelectionMode.Intersect)
            : mask;
}
