using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Psd;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>
/// Crop, Canvas Size, Image Size or Trim as one history step: the canvas size and resolution, every layer's
/// pixels, bounds, mask and name, the stored composite, and the file data that depends on the canvas (saved
/// selections, guides, paths, vector masks and type positions). Layers whose live content (type, smart objects,
/// fills, shapes) had to be resampled are rasterized, and the selection is dropped. Rasters are immutable, so undo
/// just puts the old references back.
/// </summary>
public sealed class CanvasEdit : IEdit
{
    private sealed record DocState(int Width, int Height, double Resolution, Raster? Composite, object? Source, SelectionMask? Selection);
    private sealed record NodeState(LayerGeometry Geometry, object? Source, string[] Tags);

    // What Rasterize Layer removes (its record drops the matching blocks, vector masks included).
    private static readonly string[] LiveTags = ["text", "smart-object", "fill", "shape", "vector-mask"];

    private readonly Document _doc;
    private readonly Action<SelectionMask?> _setSelection;
    private readonly DocState _before, _after;
    private readonly List<(LayerNode Node, NodeState Before, NodeState After)> _nodes = [];

    /// <param name="change">The computed change; null for a resolution-only Image Size.</param>
    public CanvasEdit(Document doc, CanvasChange? change, double resolution, SelectionMask? selection, Action<SelectionMask?> setSelection, string description)
    {
        _doc = doc;
        _setSelection = setSelection;
        Description = description;
        _before = new DocState(doc.Width, doc.Height, doc.Resolution, doc.Composite, doc.SourceData, selection);
        if (change is null)
        {
            _after = _before with { Resolution = resolution };
            return;
        }

        var map = ToCanvasMap(change.Map);
        int oldW = doc.Width, oldH = doc.Height, newW = change.Width, newH = change.Height;
        object? MapSource(object? source, bool rasterize) => source switch
        {
            PsdLayerRecord r => PsdCanvas.WithCanvas(rasterize ? r.Rasterized() : r, oldW, oldH, newW, newH, map),
            PsdGroupRecords g => g with { Folder = PsdCanvas.WithCanvas(g.Folder, oldW, oldH, newW, newH, map) },
            _ => source,
        };

        var rasterized = change.ResampledLiveLayers.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var (node, geometry) in change.Layers)
        {
            bool raster = rasterized.Contains(node);
            var before = new NodeState(LayerGeometry.Of(node), node.SourceData, node.Tags.ToArray());
            var after = new NodeState(geometry, MapSource(node.SourceData, raster), raster ? before.Tags.Except(LiveTags).ToArray() : before.Tags);
            _nodes.Add((node, before, after));
        }

        object? docSource = doc.SourceData is PsdFile file
            ? PsdCanvas.WithCanvas(file, newW, newH, map, plane => plane.Width == oldW && plane.Height == oldH ? change.MapPlane(plane, 0f) : plane, change.Composite)
            : doc.SourceData;
        _after = new DocState(newW, newH, resolution, change.Composite, docSource, null);
    }

    public string Description { get; }

    /// <summary>The layer panel's thumbnails and kind badges (rasterized layers) change.</summary>
    public bool ChangesStructure => true;

    public bool ChangesSize => _before.Width != _after.Width || _before.Height != _after.Height;

    public void Do() => Write(_after, n => n.After);

    public void Undo() => Write(_before, n => n.Before);

    private void Write(DocState doc, Func<(LayerNode Node, NodeState Before, NodeState After), NodeState> pick)
    {
        _doc.SetCanvasSize(doc.Width, doc.Height);
        _doc.Resolution = doc.Resolution;
        _doc.Composite = doc.Composite;
        _doc.SourceData = doc.Source;
        foreach (var entry in _nodes)
        {
            var state = pick(entry);
            state.Geometry.ApplyTo(entry.Node);
            entry.Node.SourceData = state.Source;
            if (!entry.Node.Tags.SetEquals(state.Tags))
            {
                entry.Node.Tags.Clear();
                foreach (var t in state.Tags) entry.Node.Tags.Add(t);
            }
        }
        _setSelection(doc.Selection);
    }

    private static CanvasMap ToCanvasMap(Affine m) => new(m.M11, m.M12, m.M21, m.M22, m.Dx, m.Dy);
}
