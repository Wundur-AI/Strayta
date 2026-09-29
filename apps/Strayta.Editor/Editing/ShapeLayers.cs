using Strayta.Core;
using Strayta.Core.Paths;
using Strayta.Psd;

namespace Strayta.Editor.Editing;

/// <summary>
/// Shape layers in the editor: reading a layer's outline, fill, stroke and live shapes, drawing them
/// (<see cref="ShapeRenderer"/>) and recording changes as one undoable step that replaces the layer's PSD data and
/// pixels together. Photoshop's shape layers (fill layers with a vector mask) and the ones Strayta draws are the same
/// to it; unedited layers keep Photoshop's own pixels until something changes.
/// </summary>
public static class ShapeLayers
{
    /// <summary>True for a layer whose file data describes a shape (a fill with a vector mask).</summary>
    public static bool IsShape(LayerNode? node) =>
        node is PixelLayer { SourceData: PsdLayerRecord record } && PsdShapeLayer.IsShape(record);

    /// <summary>The layer's shape, or null when it is not a (readable) shape layer.</summary>
    public static ShapeLayerData? Read(Document doc, LayerNode? node) =>
        node is PixelLayer { SourceData: PsdLayerRecord record } && PsdShapeLayer.IsShape(record)
            ? PsdShapeLayer.Read(record, doc.Width, doc.Height, doc.Patterns, doc.Resolution)
            : null;

    /// <summary>
    /// Where a shape's pixels may go: the canvas and a margin around it, so a shape moved in from off the canvas still
    /// shows its whole outline (Photoshop keeps shape pixels past the canvas too).
    /// </summary>
    public static PixelRect Clip(Document doc)
    {
        int mx = Math.Max(64, doc.Width / 4), my = Math.Max(64, doc.Height / 4);
        return new PixelRect(-mx, -my, doc.Width + mx, doc.Height + my);
    }

    public static ShapeRender Render(Document doc, ShapeLayerData data, CancellationToken cancel = default) =>
        ShapeRenderer.Render(data, doc.Bounds, Clip(doc), doc.ColorMode, doc.BitDepth, cancel);

    /// <summary>A new shape layer showing <paramref name="data"/> (insert it with an <see cref="InsertEdit"/>).</summary>
    public static PixelLayer Create(Document doc, ShapeLayerData data, string name)
    {
        var render = Render(doc, data);
        var layer = new PixelLayer
        {
            Name = name,
            SourceData = PsdShapeLayer.Create(data, doc.Width, doc.Height, doc.Resolution),
            Pixels = render.Pixels,
            Bounds = render.Pixels is null ? PixelRect.Empty : render.Bounds,
        };
        layer.Tags.Add("shape");
        layer.Tags.Add("vector-mask");
        return layer;
    }

    /// <summary>The layer's state after giving it <paramref name="data"/>: new PSD data, pixels and mask.</summary>
    public static ShapeEdit.State StateFor(Document doc, PixelLayer layer, ShapeLayerData data, ShapeRender? render = null)
    {
        var record = layer.SourceData as PsdLayerRecord;
        var newRecord = record is null ? PsdShapeLayer.Create(data, doc.Width, doc.Height, doc.Resolution)
            : PsdShapeLayer.Apply(record, data, doc.Width, doc.Height, doc.Resolution);
        render ??= Render(doc, data);
        // Photoshop's cached rasterization of the old outline (applied to the old pixels) no longer applies; a real
        // pixel mask stays.
        var mask = layer.Mask is { AppliedToPixels: true } ? null : layer.Mask;
        return new ShapeEdit.State(newRecord, render.Pixels, render.Pixels is null ? PixelRect.Empty : render.Bounds, mask);
    }

    /// <summary>The edit that gives <paramref name="layer"/> the shape <paramref name="data"/>.</summary>
    public static ShapeEdit Edit(Document doc, PixelLayer layer, ShapeLayerData data, string description) =>
        new(layer, ShapeEdit.State.Of(layer), StateFor(doc, layer, data), description);

    /// <summary>Photoshop's names for new shape layers, after the tool that drew them.</summary>
    public static string BaseName(LiveShapeKind kind) => kind switch
    {
        LiveShapeKind.Rectangle => "Rectangle",
        LiveShapeKind.RoundedRectangle => "Rectangle",
        LiveShapeKind.Ellipse => "Ellipse",
        LiveShapeKind.Polygon => "Polygon",
        LiveShapeKind.Triangle => "Triangle",
        LiveShapeKind.Line => "Line",
        _ => "Shape",
    };
}

/// <summary>Replaces a shape layer's file data, pixels, bounds and mask together.</summary>
public sealed class ShapeEdit(PixelLayer layer, ShapeEdit.State before, ShapeEdit.State after, string description) : ILayerPropertyEdit
{
    /// <summary>Everything a shape change touches.</summary>
    public sealed record State(object? Source, Raster? Pixels, PixelRect Bounds, LayerMask? Mask)
    {
        public static State Of(PixelLayer layer) => new(layer.SourceData, layer.Pixels, layer.Bounds, layer.Mask);
    }

    private State _after = after;

    public LayerNode Node => layer;
    public string Description => description;
    public bool ChangesStructure => false;

    public void Do() => Set(_after);
    public void Undo() => Set(before);

    /// <summary>A run of the same change to the same layer (a stroke width typed step by step, a color dragged) is one step.</summary>
    public bool TryMerge(IEdit next)
    {
        // Only settings changes merge; every path gesture stays its own step, as in Photoshop.
        if (next is not ShapeEdit s || !ReferenceEquals(s.Node, Node) || s.Description != Description
            || !(Description.StartsWith("Change ", StringComparison.Ordinal) || Description == "Move Shape")) return false;
        _after = s._after;
        return true;
    }

    /// <summary>Puts a state on the layer (also used for live previews outside history).</summary>
    public static void Set(PixelLayer layer, State s)
    {
        layer.SourceData = s.Source;
        layer.Pixels = s.Pixels;
        layer.Bounds = s.Bounds;
        layer.Mask = s.Mask;
        layer.Tags.Add("vector-mask");
        if (!layer.Tags.Contains("fill")) layer.Tags.Add("shape");
    }

    private void Set(State s) => Set(layer, s);
}

/// <summary>
/// Replaces the document's paths (the Work Path and saved paths), which live in the PSD file data it came from, so
/// crops and resizes carry them along like everything else the file keeps.
/// </summary>
public sealed class DocumentPathsEdit(Document doc, object? before, object after, string description) : IEdit
{
    public string Description => description;
    public bool ChangesStructure => false;

    public void Do() => doc.SourceData = after;
    public void Undo() => doc.SourceData = before;

    /// <summary>The document's paths, in the Paths panel's order (the Work Path first).</summary>
    public static List<DocumentPath> Read(Document doc) =>
        doc.SourceData is PsdFile file ? PsdPathResources.Read(file) : [];

    /// <summary>The edit that sets the document's paths to <paramref name="paths"/>.</summary>
    public static DocumentPathsEdit To(Document doc, IReadOnlyList<DocumentPath> paths, string description)
    {
        var file = doc.SourceData as PsdFile ?? PsdPathResources.Empty(doc);
        return new DocumentPathsEdit(doc, doc.SourceData, PsdPathResources.WithPaths(file, paths), description);
    }
}
