using Strayta.Core;

namespace Strayta.Editor.Editing;

/// <summary>An edit to a single layer's properties, so the UI can refresh just that layer.</summary>
public interface ILayerPropertyEdit : IEdit
{
    LayerNode Node { get; }
}

/// <summary>Changes one property of one layer; consecutive changes to the same property merge.</summary>
public sealed class PropertyEdit<T>(LayerNode node, string property, T before, T after, Action<LayerNode, T> set) : ILayerPropertyEdit
{
    private T _after = after;

    public LayerNode Node { get; } = node;
    public string Property { get; } = property;
    public string Description => $"Change {Property}";
    public bool ChangesStructure => false;

    public void Do() => set(Node, _after);
    public void Undo() => set(Node, before);

    public bool TryMerge(IEdit next)
    {
        if (next is not PropertyEdit<T> p || !ReferenceEquals(p.Node, Node) || p.Property != Property) return false;
        _after = p._after;
        return true;
    }
}

/// <summary>
/// Moves a layer (and everything attached to it) by an offset; consecutive moves of the same layer merge. Given the
/// canvas size, live data in the file (vector masks and shape outlines, live shape boxes, the type transform, smart
/// object corners) moves along, so shapes and type stay where their pixels are.
/// </summary>
public sealed class MoveEdit(LayerNode node, int dx, int dy, int canvasWidth = 0, int canvasHeight = 0) : IEdit
{
    private int _dx = dx, _dy = dy;

    public LayerNode Node { get; } = node;
    public string Description => "Move";
    public bool ChangesStructure => false;

    public void Do()
    {
        Offset(Node, _dx, _dy);
        MoveLiveData(Node, _dx, _dy, canvasWidth, canvasHeight);
    }

    public void Undo()
    {
        Offset(Node, -_dx, -_dy);
        MoveLiveData(Node, -_dx, -_dy, canvasWidth, canvasHeight);
    }

    private static void MoveLiveData(LayerNode node, int dx, int dy, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        var map = Strayta.Psd.CanvasMap.Translation(dx, dy);
        node.SourceData = node.SourceData switch
        {
            Strayta.Psd.PsdLayerRecord r => Strayta.Psd.PsdCanvas.WithCanvas(r, w, h, w, h, map),
            Strayta.Psd.PsdGroupRecords g => g with { Folder = Strayta.Psd.PsdCanvas.WithCanvas(g.Folder, w, h, w, h, map) },
            var other => other,
        };
        if (node is LayerGroup group)
            foreach (var child in group.Children) MoveLiveData(child, dx, dy, w, h);
    }

    public bool TryMerge(IEdit next)
    {
        if (next is not MoveEdit m || !ReferenceEquals(m.Node, Node)) return false;
        _dx += m._dx;
        _dy += m._dy;
        return true;
    }

    public static void Offset(LayerNode node, int dx, int dy)
    {
        switch (node)
        {
            case PixelLayer p:
                p.Bounds = Shift(p.Bounds, dx, dy);
                p.Mask = ShiftMask(p.Mask, dx, dy);
                break;
            case AdjustmentLayer a:
                a.Mask = ShiftMask(a.Mask, dx, dy);
                break;
            case LayerGroup g:
                g.Mask = ShiftMask(g.Mask, dx, dy);
                foreach (var child in g.Children) Offset(child, dx, dy);
                break;
        }
    }

    private static PixelRect Shift(PixelRect r, int dx, int dy) =>
        r.IsEmpty ? r : new PixelRect(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);

    private static LayerMask? ShiftMask(LayerMask? m, int dx, int dy) => m is null ? null : new LayerMask
    {
        Bounds = Shift(m.Bounds, dx, dy),
        Pixels = m.Pixels,
        DefaultColor = m.DefaultColor,
        Disabled = m.Disabled,
        PositionRelativeToLayer = m.PositionRelativeToLayer,
        AppliedToPixels = m.AppliedToPixels,
    };
}

/// <summary>Moves a layer to a new parent and index (reorder, or in and out of groups).</summary>
public sealed class ReparentEdit(LayerNode node, LayerGroup toParent, int toIndex) : IEdit
{
    private readonly LayerGroup _fromParent = node.Parent ?? throw new ArgumentException("Layer has no parent.");
    private readonly int _fromIndex = node.Parent!.IndexOf(node);

    public string Description => "Reorder Layer";
    public bool ChangesStructure => true;

    public void Do()
    {
        _fromParent.Remove(node);
        toParent.Insert(Math.Clamp(toIndex, 0, toParent.Children.Count), node);
    }

    public void Undo()
    {
        node.Parent!.Remove(node);
        _fromParent.Insert(_fromIndex, node);
    }
}

public sealed class DeleteEdit(LayerNode node) : IEdit
{
    private readonly LayerGroup _parent = node.Parent ?? throw new ArgumentException("Layer has no parent.");
    private readonly int _index = node.Parent!.IndexOf(node);

    public string Description => "Delete Layer";
    public bool ChangesStructure => true;

    public void Do() => _parent.Remove(node);
    public void Undo() => _parent.Insert(_index, node);
}

/// <summary>Replaces a layer's pixels (a committed brush or eraser stroke). Rasters are immutable, so undo just swaps references.</summary>
public sealed class PixelsEdit(PixelLayer layer, Raster? pixels, PixelRect bounds, string description) : IEdit
{
    private readonly Raster? _oldPixels = layer.Pixels;
    private readonly PixelRect _oldBounds = layer.Bounds;

    public string Description => description;
    public bool ChangesStructure => false;

    public void Do() { layer.Pixels = pixels; layer.Bounds = bounds; }
    public void Undo() { layer.Pixels = _oldPixels; layer.Bounds = _oldBounds; }
}

/// <summary>Adds a new layer or group at a position.</summary>
public sealed class InsertEdit(LayerNode node, LayerGroup parent, int index, string description) : IEdit
{
    public string Description => description;
    public bool ChangesStructure => true;

    public void Do() => parent.Insert(Math.Clamp(index, 0, parent.Children.Count), node);
    public void Undo() => parent.Remove(node);
}

/// <summary>
/// Turns a text, fill, shape or smart-object layer into plain pixels: its pixels stay as they are, but
/// the data Photoshop would regenerate them from is dropped, so painting on it is kept.
/// </summary>
public sealed class RasterizeEdit(LayerNode node) : IEdit
{
    private static readonly string[] LiveTags = ["text", "smart-object", "fill", "shape", "vector-mask"];
    private readonly object? _oldSource = node.SourceData;
    private readonly string[] _oldTags = node.Tags.Where(t => LiveTags.Contains(t)).ToArray();

    public string Description => "Rasterize Layer";
    public bool ChangesStructure => true; // the panel's kind badges and icons change

    // A shape or fill layer's pixels already carry its rasterized vector mask; once the vector is gone the mask would
    // show as an ordinary layer mask, so it goes too (Photoshop's Rasterize Layer leaves no mask either).
    private readonly LayerMask? _oldMask = node.GetMask();

    public void Do()
    {
        if (_oldSource is Strayta.Psd.PsdLayerRecord r) node.SourceData = r.Rasterized();
        foreach (var t in LiveTags) node.Tags.Remove(t);
        if (_oldMask is { AppliedToPixels: true }) node.SetMask(null);
    }

    public void Undo()
    {
        node.SourceData = _oldSource;
        foreach (var t in _oldTags) node.Tags.Add(t);
        if (_oldMask is { AppliedToPixels: true }) node.SetMask(_oldMask);
    }

    public static bool CanRasterize(LayerNode node) => node is PixelLayer && LiveTags.Any(node.Tags.Contains);
}
