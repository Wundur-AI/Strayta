using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

/// <summary>An artboard as the canvas draws it: its bounds, name, and whether it is the selected one.</summary>
public sealed record ArtboardOverlay(LayerGroup Group, string Name, PixelRect Rect, bool IsSelected);

/// <summary>Which side of an artboard the Artboard tool's "+" adds the next one on.</summary>
public enum ArtboardSide { Left, Top, Right, Bottom }

// Artboards: Layer › New › Artboard, Artboards from Layers / Group, and the Artboard tool's create, move, resize and "+".
public sealed partial class DocumentViewModel
{
    /// <summary>The space the Artboard tool's "+" leaves between artboards, in pixels.</summary>
    public const int ArtboardGap = 100;

    /// <summary>The artboards for the canvas overlay (names, and the Artboard tool's handles on the selected one).</summary>
    public IReadOnlyList<ArtboardOverlay> ArtboardOverlays()
    {
        var selected = SelectedArtboard;
        return Artboards.Of(Model).Select(g => new ArtboardOverlay(g, g.Name, g.Artboard!.Rect, ReferenceEquals(g, selected))).ToList();
    }

    /// <summary>The artboard the selected layer is (or is in), or null.</summary>
    public LayerGroup? SelectedArtboard => SelectedLayer?.Node is { } node ? Artboards.Containing(node) : null;

    public void SelectArtboard(LayerGroup group) => Select(group);

    /// <summary>The next "Artboard N" name.</summary>
    private string NextArtboardName() => LayerFactory.NextName(Model, "Artboard");

    /// <summary>
    /// Layer › New › Artboard and the Artboard tool's drag: a new, empty artboard at <paramref name="rect"/>, on top of the
    /// layer stack and selected. The canvas grows to hold it (a separate Canvas Size step), as Photoshop's does.
    /// </summary>
    public LayerGroup NewArtboard(PixelRect rect, string? name = null, ArtboardBackground background = ArtboardBackground.White,
        (byte R, byte G, byte B)? color = null, string preset = "")
    {
        rect = GrowCanvasFor(rect);
        var group = new LayerGroup
        {
            Name = string.IsNullOrWhiteSpace(name) ? NextArtboardName() : name,
            Artboard = new Artboard { Rect = rect, Background = background, Color = color ?? (255, 255, 255), PresetName = preset },
        };
        Apply(new InsertEdit(group, Model.Root, Model.Root.Children.Count, "New Artboard"));
        Select(group);
        return group;
    }

    /// <summary>
    /// Makes the canvas large enough for <paramref name="rect"/> (Canvas Size, anchored so nothing moves when it grows
    /// right or down) and returns the rect in the new canvas's coordinates.
    /// </summary>
    private PixelRect GrowCanvasFor(PixelRect rect)
    {
        int left = Math.Min(0, rect.Left), top = Math.Min(0, rect.Top);
        int right = Math.Max(Model.Width, rect.Right), bottom = Math.Max(Model.Height, rect.Bottom);
        if (left == 0 && top == 0 && right == Model.Width && bottom == Model.Height) return rect;
        var change = CanvasOperations.ResizeCanvas(Model, right - left, bottom - top, -left, -top);
        Apply(new CanvasEdit(Model, change, Model.Resolution, Selection, s => Selection = s, "Canvas Size"));
        return new PixelRect(rect.Left - left, rect.Top - top, rect.Right - left, rect.Bottom - top);
    }

    /// <summary>Layer › New › Artboards from Layers: a new artboard around the selected layers, which move into it.</summary>
    public LayerGroup? ArtboardFromLayers()
    {
        var nodes = SelectedNodesForExport().Where(n => n is not LayerGroup { Artboard: not null } && n.Parent is not null).ToList();
        if (nodes.Count == 0)
        {
            Notice = "Select the layers to put on the new artboard.";
            return null;
        }
        var bounds = ContentBounds(nodes);
        if (bounds.IsEmpty) bounds = Model.Bounds;
        bounds = GrowCanvasFor(bounds);
        var group = new LayerGroup
        {
            Name = NextArtboardName(),
            Artboard = new Artboard { Rect = bounds, PresetName = "Custom" },
        };
        // A layer inside another selected group moves with that group.
        nodes = nodes.Where(n => !nodes.Any(o => !ReferenceEquals(o, n) && o is LayerGroup g && g.Descendants().Contains(n))).ToList();
        Apply(new ArtboardFromLayersEdit(Model, group, nodes));
        Select(group);
        return group;
    }

    /// <summary>Layer › New › Artboard from Group: the selected group becomes an artboard around its layers (moved to the top level if needed).</summary>
    public LayerGroup? ArtboardFromGroup()
    {
        if (SelectedLayer?.Node is not LayerGroup { Artboard: null } group || group.Parent is null)
        {
            Notice = "Select a group to turn into an artboard.";
            return null;
        }
        var bounds = ContentBounds([group]);
        if (bounds.IsEmpty) bounds = Model.Bounds;
        bounds = GrowCanvasFor(bounds);
        var edits = new List<IEdit>();
        if (!ReferenceEquals(group.Parent, Model.Root)) edits.Add(new ReparentEdit(group, Model.Root, TopLevelIndex(group) + 1));
        edits.Add(new ArtboardEdit(group, new Artboard { Rect = bounds, PresetName = "Custom" }, "Artboard from Group"));
        if (group.BlendMode != BlendMode.PassThrough)
            edits.Add(new PropertyEdit<BlendMode>(group, "Blend Mode", group.BlendMode, BlendMode.PassThrough, (n, v) => n.BlendMode = v));
        Apply(new CompositeEdit("Artboard from Group", edits.ToArray()));
        Select(group);
        return group;
    }

    /// <summary>Changes an artboard's bounds or background; a resize drag's steps merge (<paramref name="dragging"/>).</summary>
    public void SetArtboard(LayerGroup group, Artboard artboard, string description = "Change Artboard", bool dragging = false)
    {
        if (group.Artboard == artboard) return;
        if (artboard.Rect.IsEmpty) return;
        Apply(new ArtboardEdit(group, artboard, description, mergeable: dragging));
    }

    /// <summary>The Artboard tool's resize handles: new bounds (at least 1×1), kept inside the canvas.</summary>
    public void ResizeArtboard(LayerGroup group, PixelRect rect)
    {
        if (group.Artboard is not { } artboard) return;
        rect = rect.Intersect(Model.Bounds);
        if (rect.Width < 1 || rect.Height < 1) return;
        var preset = ArtboardPresets.Matching(rect.Width, rect.Height)?.Name ?? "Custom";
        SetArtboard(group, artboard with { Rect = rect, PresetName = preset }, "Resize Artboard", dragging: true);
    }

    /// <summary>Moves an artboard and everything on it (the Artboard tool's drag).</summary>
    public void MoveArtboard(LayerGroup group, int dx, int dy)
    {
        if ((dx, dy) == (0, 0) || group.Artboard is null) return;
        Apply(new MoveEdit(group, dx, dy, Model.Width, Model.Height));
    }

    /// <summary>The Artboard tool's "+": a new artboard of the same size and background beside <paramref name="source"/>.</summary>
    public LayerGroup? AddAdjacentArtboard(LayerGroup source, ArtboardSide side)
    {
        if (source.Artboard is not { } a) return null;
        var r = a.Rect;
        int w = r.Width, h = r.Height;
        var rect = side switch
        {
            ArtboardSide.Left => new PixelRect(r.Left - ArtboardGap - w, r.Top, r.Left - ArtboardGap, r.Bottom),
            ArtboardSide.Top => new PixelRect(r.Left, r.Top - ArtboardGap - h, r.Right, r.Top - ArtboardGap),
            ArtboardSide.Bottom => new PixelRect(r.Left, r.Bottom + ArtboardGap, r.Right, r.Bottom + ArtboardGap + h),
            _ => new PixelRect(r.Right + ArtboardGap, r.Top, r.Right + ArtboardGap + w, r.Bottom),
        };
        // Keep clear of other artboards: step further out while the spot is taken.
        for (int guard = 0; guard < 50 && Artboards.Of(Model).Any(g => !g.Artboard!.Rect.Intersect(rect).IsEmpty); guard++)
        {
            int dx = side switch { ArtboardSide.Left => -(w + ArtboardGap), ArtboardSide.Right => w + ArtboardGap, _ => 0 };
            int dy = side switch { ArtboardSide.Top => -(h + ArtboardGap), ArtboardSide.Bottom => h + ArtboardGap, _ => 0 };
            rect = new PixelRect(rect.Left + dx, rect.Top + dy, rect.Right + dx, rect.Bottom + dy);
        }
        return NewArtboard(rect, null, a.Background, a.Color, a.PresetName);
    }

    /// <summary>The Artboard tool's options bar: a preset or typed size for the selected artboard (anchored at its top left).</summary>
    public void SetSelectedArtboardSize(int width, int height, string? preset = null)
    {
        if (SelectedArtboard is not { Artboard: { } a } group || width < 1 || height < 1) return;
        var rect = GrowCanvasFor(new PixelRect(a.Rect.Left, a.Rect.Top, a.Rect.Left + width, a.Rect.Top + height));
        SetArtboard(group, group.Artboard! with { Rect = rect, PresetName = preset ?? ArtboardPresets.Matching(width, height)?.Name ?? "Custom" }, "Resize Artboard");
    }

    public void SetSelectedArtboardBackground(ArtboardBackground background, (byte R, byte G, byte B)? color = null)
    {
        if (SelectedArtboard is not { Artboard: { } a } group) return;
        SetArtboard(group, a with { Background = background, Color = color ?? a.Color }, "Artboard Background");
    }

    /// <summary>The union of the pixel bounds of <paramref name="nodes"/> and everything in them, clipped to the canvas.</summary>
    private PixelRect ContentBounds(IEnumerable<LayerNode> nodes)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var node in nodes.SelectMany(n => n is LayerGroup g ? g.Descendants().Prepend(n) : [n]))
        {
            if (node is not PixelLayer { Pixels: not null, Bounds.IsEmpty: false } p) continue;
            var rect = Resampler.ContentBounds(p);
            if (rect.IsEmpty) continue;
            (l, t, r, b) = (Math.Min(l, rect.Left), Math.Min(t, rect.Top), Math.Max(r, rect.Right), Math.Max(b, rect.Bottom));
        }
        return l == int.MaxValue ? PixelRect.Empty : new PixelRect(l, t, r, b).Intersect(Model.Bounds);
    }

    /// <summary>The index in the root of the top-level layer or group that holds <paramref name="node"/>.</summary>
    private int TopLevelIndex(LayerNode node)
    {
        var top = node;
        while (top.Parent is { } parent && !ReferenceEquals(parent, Model.Root)) top = parent;
        return Math.Max(0, Model.Root.IndexOf(top));
    }
}
