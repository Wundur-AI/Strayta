using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// A History snapshot: the whole document as it was when the snapshot was taken (the layer tree copied, sharing the
/// immutable pixel data, and the selection), with a thumbnail. The first snapshot of every document is the document as
/// opened. Clicking one restores it as a new history step; it can also be the History Brush's source.
/// </summary>
public sealed partial class HistorySnapshot : ObservableObject
{
    internal HistorySnapshot(string name, IReadOnlyList<LayerNode> tree, SelectionMask? selection, int width, int height, HistoryLayerPixels pixels)
    {
        Name = name;
        Tree = tree;
        Selection = selection;
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    [ObservableProperty] public partial string Name { get; set; }

    /// <summary>A small picture of the snapshot, once rendered.</summary>
    [ObservableProperty] public partial Bitmap? Thumbnail { get; internal set; }

    /// <summary>The top-level layers, copied (never part of the document; restoring copies them again).</summary>
    internal IReadOnlyList<LayerNode> Tree { get; }
    internal SelectionMask? Selection { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal HistoryLayerPixels Pixels { get; }
}

/// <summary>The pixels of every pixel layer in one history state, keyed by layer identity (see <c>HistoryKey</c>).</summary>
internal sealed record HistoryLayerPixels(int Width, int Height, IReadOnlyDictionary<LayerNode, (Raster? Pixels, PixelRect Bounds)> Layers);

// History snapshots, the history-state limit, and what the History Brush paints from. Every history state's layer
// pixels are recorded as references when the state is made (rasters are never modified in place, so this costs a few
// pointers per layer), so the History Brush can paint from any state without stepping the document there.
public sealed partial class DocumentViewModel
{
    // Layers copied into or out of a snapshot keep the identity of the layer they were copied from, so the History
    // Brush finds "the same layer" in a snapshot after it has been restored.
    private readonly ConditionalWeakTable<LayerNode, LayerNode> _historyOrigin = new();
    private readonly Dictionary<IEdit, HistoryLayerPixels> _statePixels = new(ReferenceEqualityComparer.Instance);
    private HistoryLayerPixels _basePixels = null!;
    private int _recordedPush;
    private IEdit? _recordedBase;
    private int _snapshotCount;

    /// <summary>The History panel's snapshots, the opened document first.</summary>
    public ObservableCollection<HistorySnapshot> Snapshots { get; } = [];

    /// <summary>
    /// Where the History Brush paints from: a <see cref="HistorySnapshot"/>, a history state (the <see cref="IEdit"/>
    /// that made it), or <see cref="HistoryBase"/> for the oldest state. Photoshop's default: the opened document.
    /// </summary>
    [ObservableProperty] public partial object? HistoryBrushSource { get; private set; }

    /// <summary>Stands for history state 0 (the opened document, or the oldest state kept) as a History Brush source.</summary>
    public static readonly object HistoryBase = new();

    /// <summary>Called from the constructor: the opened document's snapshot and state, and the history-state limit.</summary>
    private void InitHistoryStates()
    {
        _undo.Limit = Editor.HistoryStates;
        _basePixels = CaptureLayerPixels();
        var open = TakeSnapshot(Title);
        Snapshots.Add(open);
        HistoryBrushSource = open;
        HistoryChanged += RecordHistoryState;
    }

    /// <summary>The History States preference changed: older steps beyond it are dropped now.</summary>
    internal void SetHistoryLimit(int limit)
    {
        _undo.Limit = limit;
        RecordHistoryState();
        HistoryChanged?.Invoke();
    }

    /// <summary>The newest edit dropped by the history-state limit (its state is now the oldest one), or null.</summary>
    public IEdit? HistoryBaseEdit => _undo.BaseEdit;

    private LayerNode HistoryKey(LayerNode node) => _historyOrigin.TryGetValue(node, out var origin) ? origin : node;

    private HistoryLayerPixels CaptureLayerPixels()
    {
        var layers = new Dictionary<LayerNode, (Raster?, PixelRect)>(ReferenceEqualityComparer.Instance);
        foreach (var node in Model.Root.Descendants())
            if (node is PixelLayer p) layers[HistoryKey(p)] = (p.Pixels, p.Bounds);
        return new HistoryLayerPixels(Model.Width, Model.Height, layers);
    }

    /// <summary>After every edit, undo and redo: records the new state's pixels after an edit, and drops what is gone.</summary>
    private void RecordHistoryState()
    {
        if (_undo.PushCount == _recordedPush && ReferenceEquals(_undo.BaseEdit, _recordedBase)) return;
        if (_undo.PushCount != _recordedPush)
        {
            _recordedPush = _undo.PushCount;
            var last = _undo.Done.Count > 0 ? _undo.Done[^1] : null;
            if (last is not null) _statePixels[last] = CaptureLayerPixels();
            if (last is not SelectionEdit) _finder?.Cancel.Cancel(); // the Object Finder's image is out of date
        }
        if (!ReferenceEquals(_undo.BaseEdit, _recordedBase))
        {
            // The oldest steps were dropped: the state after the newest dropped one is the new state 0.
            _recordedBase = _undo.BaseEdit;
            if (_recordedBase is not null && _statePixels.TryGetValue(_recordedBase, out var basePixels)) _basePixels = basePixels;
        }
        var alive = new HashSet<IEdit>(_undo.Done.Concat(_undo.Undone), ReferenceEqualityComparer.Instance);
        foreach (var gone in _statePixels.Keys.Where(k => !alive.Contains(k)).ToList()) _statePixels.Remove(gone);
        if (HistoryBrushSource is IEdit e && !alive.Contains(e)) HistoryBrushSource = Snapshots.FirstOrDefault();
    }

    // ---- Snapshots ------------------------------------------------------------------------------------

    /// <summary>The History panel's New Snapshot button: the document as it is now.</summary>
    public HistorySnapshot NewSnapshot()
    {
        var snapshot = TakeSnapshot($"Snapshot {++_snapshotCount}");
        Snapshots.Add(snapshot);
        return snapshot;
    }

    public void DeleteSnapshot(HistorySnapshot snapshot)
    {
        if (Snapshots.IndexOf(snapshot) <= 0) return; // the opened document's snapshot stays
        Snapshots.Remove(snapshot);
        if (ReferenceEquals(HistoryBrushSource, snapshot)) HistoryBrushSource = Snapshots[0];
    }

    private HistorySnapshot TakeSnapshot(string name)
    {
        var tree = Model.Root.Children.Select(CloneForHistory).ToList();
        var snapshot = new HistorySnapshot(name, tree, Selection, Model.Width, Model.Height, CaptureLayerPixels());
        _ = RenderSnapshotThumbnailAsync(snapshot, opened: Snapshots.Count == 0);
        return snapshot;
    }

    /// <summary>
    /// Clicking a snapshot: the document becomes that snapshot again, as one new history step named after it (so undo
    /// brings back what was there). Snapshots of a different canvas size cannot be restored.
    /// </summary>
    public void RestoreSnapshot(HistorySnapshot snapshot)
    {
        if (IsTransforming) CancelTransform();
        if (snapshot.Width != Model.Width || snapshot.Height != Model.Height)
        {
            Notice = $"\"{snapshot.Name}\" has a different canvas size ({snapshot.Width}×{snapshot.Height}); crop or resize back first.";
            return;
        }
        var selectedKey = SelectedLayer?.Node is { } node ? HistoryKey(node) : null;
        var restored = snapshot.Tree.Select(CloneForHistory).ToList();
        Apply(new RestoreSnapshotEdit(Model.Root, restored, Selection, snapshot.Selection, s => Selection = s, snapshot.Name));
        var match = restored.SelectMany(n => n is LayerGroup g ? g.Descendants().Prepend(n) : new[] { n }).FirstOrDefault(n => ReferenceEquals(HistoryKey(n), selectedKey));
        if (match is not null) Select(match);
        else SelectedLayer = Layers.FirstOrDefault();
        Notice = "";
    }

    /// <summary>A copy of a layer (and its children) sharing its pixels, masks and file data, remembered as the same layer.</summary>
    private LayerNode CloneForHistory(LayerNode node)
    {
        LayerNode copy = node switch
        {
            PixelLayer p => new PixelLayer { Bounds = p.Bounds, Pixels = p.Pixels, Mask = p.Mask, TransparencyLocked = p.TransparencyLocked },
            AdjustmentLayer a => new AdjustmentLayer { Adjustment = a.Adjustment, Kind = a.Kind, Mask = a.Mask },
            LayerGroup g => new LayerGroup { Expanded = g.Expanded, Mask = g.Mask },
            _ => throw new NotSupportedException($"Cannot copy a {node.GetType().Name}."),
        };
        copy.Name = node.Name;
        copy.Visible = node.Visible;
        copy.Opacity = node.Opacity;
        copy.FillOpacity = node.FillOpacity;
        copy.BlendMode = node.BlendMode;
        copy.Clipped = node.Clipped;
        copy.Effects = node.Effects;
        copy.SourceData = node.SourceData;
        foreach (var tag in node.Tags) copy.Tags.Add(tag);
        if (node is LayerGroup group)
            foreach (var child in group.Children) ((LayerGroup)copy).Add(CloneForHistory(child));
        _historyOrigin.AddOrUpdate(copy, HistoryKey(node));
        return copy;
    }

    /// <summary>
    /// A snapshot's thumbnail, from a render the document makes anyway: for the opened document the composite stored
    /// in the file (or the first full-resolution render), otherwise the current render. Downscaled in the background.
    /// </summary>
    private async Task RenderSnapshotThumbnailAsync(HistorySnapshot snapshot, bool opened)
    {
        const int Size = 40;
        try
        {
            byte[]? rgba = opened ? _reference : await CompositeAsync();
            if (rgba is null && opened)
            {
                var shown = new TaskCompletionSource();
                void OnFrame(bool full)
                {
                    if (full) shown.TrySetResult();
                }
                FrameDisplayed += OnFrame;
                if (_lastRender is null) await Task.WhenAny(shown.Task, Task.Delay(TimeSpan.FromSeconds(60)));
                FrameDisplayed -= OnFrame;
                rgba = _lastRender;
            }
            if (rgba is null) return;
            var (w, h) = (Model.Width, Model.Height);
            if (rgba.Length != w * h * 4) return; // the canvas changed meanwhile
            var (pixels, tw, th) = await Task.Run(() => Downscale(rgba, w, h, Size));
            snapshot.Thumbnail = BitmapFactory.FromRgba(pixels, tw, th);
        }
        catch (Exception)
        {
            // A thumbnail is a nicety; the snapshot works without one.
        }
    }

    /// <summary>A box-filtered copy whose longer side is <paramref name="size"/> pixels, over a white-and-gray checkerboard.</summary>
    private static (byte[] Rgba, int Width, int Height) Downscale(byte[] src, int w, int h, int size)
    {
        double scale = Math.Min(1.0, (double)size / Math.Max(w, h));
        int tw = Math.Max(1, (int)Math.Round(w * scale)), th = Math.Max(1, (int)Math.Round(h * scale));
        var dst = new byte[tw * th * 4];
        for (int ty = 0; ty < th; ty++)
            for (int tx = 0; tx < tw; tx++)
            {
                int x0 = tx * w / tw, x1 = Math.Max(x0 + 1, (tx + 1) * w / tw), y0 = ty * h / th, y1 = Math.Max(y0 + 1, (ty + 1) * h / th);
                double r = 0, g = 0, b = 0, a = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int i = (y * w + x) * 4;
                        double al = src[i + 3] / 255.0;
                        r += src[i] * al;
                        g += src[i + 1] * al;
                        b += src[i + 2] * al;
                        a += al;
                    }
                int n = (x1 - x0) * (y1 - y0);
                double coverage = a / n, checker = ((tx / 4 + ty / 4) & 1) == 0 ? 255 : 204;
                int o = (ty * tw + tx) * 4;
                dst[o] = (byte)Math.Round(r / n + checker * (1 - coverage));
                dst[o + 1] = (byte)Math.Round(g / n + checker * (1 - coverage));
                dst[o + 2] = (byte)Math.Round(b / n + checker * (1 - coverage));
                dst[o + 3] = 255;
            }
        return (dst, tw, th);
    }

    // ---- History Brush source ------------------------------------------------------------------------------

    /// <summary>The History panel's left column on a state row: that state becomes the History Brush's source.</summary>
    public void SetHistoryBrushSource(int position)
    {
        var edits = HistoryEdits;
        HistoryBrushSource = position <= 0 ? HistoryBase : position <= edits.Count ? edits[position - 1] : HistoryBrushSource;
    }

    /// <summary>The History panel's left column on a snapshot row.</summary>
    public void SetHistoryBrushSource(HistorySnapshot snapshot) => HistoryBrushSource = snapshot;

    /// <summary>The pixels the History Brush source had for <paramref name="layer"/>, or a reason it has none.</summary>
    private ((Raster? Pixels, PixelRect Bounds)? Source, string? Problem) HistoryBrushPixels(PixelLayer layer)
    {
        HistoryLayerPixels? state = HistoryBrushSource switch
        {
            HistorySnapshot s => s.Pixels,
            IEdit e => _statePixels.GetValueOrDefault(e),
            _ when ReferenceEquals(HistoryBrushSource, HistoryBase) => _basePixels,
            _ => null,
        };
        if (state is null) return (null, "Set a source for the History Brush: click the left column of a state or snapshot in the History panel.");
        if (state.Width != Model.Width || state.Height != Model.Height)
            return (null, "Could not use the History Brush because the history state has a different canvas size.");
        if (!state.Layers.TryGetValue(HistoryKey(layer), out var pixels))
            return (null, "Could not use the History Brush because the history state does not contain a corresponding layer.");
        return (pixels, null);
    }
}

/// <summary>Restoring a history snapshot: the document's layers are replaced by copies of the snapshot's, and its selection.</summary>
public sealed class RestoreSnapshotEdit(LayerGroup root, IReadOnlyList<LayerNode> restored, SelectionMask? selectionBefore,
    SelectionMask? selectionAfter, Action<SelectionMask?> setSelection, string description) : IEdit
{
    private readonly IReadOnlyList<LayerNode> _before = root.Children.ToList();

    public string Description => description;
    public bool ChangesStructure => true;

    public void Do()
    {
        Replace(restored);
        setSelection(selectionAfter);
    }

    public void Undo()
    {
        Replace(_before);
        setSelection(selectionBefore);
    }

    private void Replace(IReadOnlyList<LayerNode> children)
    {
        foreach (var child in root.Children.ToList()) root.Remove(child);
        foreach (var child in children) root.Add(child);
    }
}
