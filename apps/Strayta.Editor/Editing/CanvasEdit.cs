using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Psd;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>
/// Crop, Canvas Size, Image Size, Trim or Perspective Crop as one history step: the canvas size and resolution,
/// every layer's pixels, bounds, mask, name and styles, the stored composite, the selection, and the file data that
/// depends on the canvas (saved selections, guides, paths, vector masks, type transforms, smart object corners).
/// Type, smart objects, shapes and fills stay live: their data moves with the canvas and their pixels are redrawn
/// or resampled (<see cref="LiveContent"/>); only a perspective crop, which their data cannot express, rasterizes
/// them. Rasters are immutable, so undo just puts the old references back.
/// </summary>
public sealed class CanvasEdit : IEdit
{
    private sealed record DocState(int Width, int Height, double Resolution, Raster? Composite, object? Source, SelectionMask? Selection);
    private sealed record NodeState(LayerGeometry Geometry, object? Source, string[] Tags, LayerEffects? Effects);

    // What Rasterize Layer removes (its record drops the matching blocks, vector masks included).
    private static readonly string[] LiveTags = ["text", "smart-object", "fill", "shape", "vector-mask"];

    private readonly Document _doc;
    private readonly Action<SelectionMask?> _setSelection;
    private readonly DocState _before, _after;
    private readonly List<(LayerNode Node, NodeState Before, NodeState After)> _nodes = [];

    /// <param name="change">The computed change; null for a resolution-only Image Size.</param>
    /// <param name="styleScale">Image Size's Scale Styles: layer effects are scaled by this factor (null leaves them).</param>
    public CanvasEdit(Document doc, CanvasChange? change, double resolution, SelectionMask? selection, Action<SelectionMask?> setSelection,
        string description, double? styleScale = null)
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
        var file = doc.SourceData as PsdFile;
        bool rasterize = change.Perspective is not null;
        object? MapSource(object? source, bool flatten) => source switch
        {
            PsdLayerRecord r => PsdCanvas.WithCanvas(flatten ? r.Rasterized() : r, oldW, oldH, newW, newH, map),
            PsdGroupRecords g => g with { Folder = PsdCanvas.WithCanvas(g.Folder, oldW, oldH, newW, newH, map) },
            _ => source,
        };

        var resampled = change.ResampledLiveLayers.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var (node, mapped) in change.Layers)
        {
            var geometry = mapped;
            var before = new NodeState(LayerGeometry.Of(node), node.SourceData, node.Tags.ToArray(), node.Effects);
            // A perspective crop rasterizes live layers; vector masks elsewhere follow its affine approximation.
            var source = MapSource(node.SourceData, rasterize && resampled.Contains(node));
            var effects = styleScale is { } k ? PsdEffectScaling.Scale(node.Effects, k) : node.Effects;
            if (!ReferenceEquals(effects, node.Effects) && source is PsdLayerRecord styled) source = PsdEffectScaling.WithEffects(styled, effects);
            else if (!ReferenceEquals(effects, node.Effects) && source is PsdGroupRecords group)
                source = group with { Folder = PsdEffectScaling.WithEffects(group.Folder, effects) };

            if (!rasterize && resampled.Contains(node) && node is PixelLayer layer && source is PsdLayerRecord record
                && LiveContent.Redraw(layer, record, file, doc, layer.Bounds, doc.Bounds, PixelRect.FromSize(newW, newH), change.Map) is { } redrawn)
                geometry = geometry with { Pixels = redrawn.Pixels, Bounds = redrawn.Bounds };
            var tags = rasterize && resampled.Contains(node) ? before.Tags.Except(LiveTags).ToArray() : before.Tags;
            _nodes.Add((node, before, new NodeState(geometry, source, tags, effects)));
        }

        object? docSource = doc.SourceData is PsdFile psd
            ? PsdCanvas.WithCanvas(psd, newW, newH, map, plane => plane.Width == oldW && plane.Height == oldH ? change.MapPlane(plane, 0f) : plane, change.Composite)
            : doc.SourceData;
        var canvas = PixelRect.FromSize(newW, newH);
        var movedSelection = change.Perspective is { } p
            ? SelectionTransform.Apply(selection, p, canvas)
            : SelectionTransform.Apply(selection, change.Map, canvas, change.Method);
        _after = new DocState(newW, newH, resolution, change.Composite, docSource, movedSelection);
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
            entry.Node.Effects = state.Effects;
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
