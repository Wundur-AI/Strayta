using Strayta.Core;

namespace Strayta.Editor.Editing;

/// <summary>
/// A smart object change as one history step ("Update Smart Object", "Replace Contents", a smart filter, ...): the
/// document's file data (its linked-files block), and for each affected layer its pixels, bounds, file record and tags,
/// plus optional structural edits (layers added or removed). Rasters and records are immutable, so undo puts the old
/// references back.
/// </summary>
public sealed class SmartObjectEdit : IEdit
{
    /// <summary>What a layer looks like before or after.</summary>
    public sealed record LayerState(Raster? Pixels, PixelRect Bounds, object? Source, string[] Tags)
    {
        public static LayerState Of(PixelLayer layer) => new(layer.Pixels, layer.Bounds, layer.SourceData, [.. layer.Tags]);
    }

    private readonly Document _doc;
    private readonly object? _sourceBefore, _sourceAfter;
    private readonly List<(PixelLayer Layer, LayerState Before, LayerState After)> _layers = [];
    private readonly IEdit[] _structure;

    public SmartObjectEdit(Document doc, string description, object? sourceAfter, IEnumerable<(PixelLayer Layer, LayerState After)> layers,
        params IEdit[] structure)
    {
        _doc = doc;
        Description = description;
        _sourceBefore = doc.SourceData;
        _sourceAfter = sourceAfter;
        foreach (var (layer, after) in layers) _layers.Add((layer, LayerState.Of(layer), after));
        _structure = structure;
    }

    public string Description { get; }

    /// <summary>The panel's thumbnails change (and layers may come or go).</summary>
    public bool ChangesStructure => true;

    /// <summary>The layers whose pixels this edit redrew.</summary>
    public IEnumerable<PixelLayer> Layers => _layers.Select(l => l.Layer);

    public void Do()
    {
        _doc.SourceData = _sourceAfter;
        foreach (var e in _structure) e.Do();
        foreach (var (layer, _, after) in _layers) Set(layer, after);
    }

    public void Undo()
    {
        foreach (var (layer, before, _) in _layers) Set(layer, before);
        for (int i = _structure.Length - 1; i >= 0; i--) _structure[i].Undo();
        _doc.SourceData = _sourceBefore;
    }

    private static void Set(PixelLayer layer, LayerState state)
    {
        layer.Pixels = state.Pixels;
        layer.Bounds = state.Bounds;
        layer.SourceData = state.Source;
        if (layer.Tags.SetEquals(state.Tags)) return;
        layer.Tags.Clear();
        foreach (var t in state.Tags) layer.Tags.Add(t);
    }
}
