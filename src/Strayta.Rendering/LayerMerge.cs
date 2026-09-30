using Strayta.Core;

namespace Strayta.Rendering;

/// <summary>
/// A merge worked out but not yet applied: the layers it removes and the pixel layer that replaces them, rendered by the
/// compositor at full resolution (blend modes, masks, clipping, effects and adjustments included). <see cref="Apply"/>
/// changes the document; an editor can instead record the same change as an undoable step.
/// </summary>
/// <param name="Command">Photoshop's name for the command (also its history name).</param>
/// <param name="Result">The new layer, or null when the command only removes layers.</param>
/// <param name="Parent">Where the new layer goes.</param>
/// <param name="Index">Its index in <paramref name="Parent"/> once <paramref name="Removed"/> are gone (0 = bottom).</param>
/// <param name="Removed">Layers taken out of the document.</param>
public sealed record MergePlan(string Command, PixelLayer? Result, LayerGroup Parent, int Index, IReadOnlyList<LayerNode> Removed)
{
    public void Apply()
    {
        foreach (var node in Removed) node.Parent?.Remove(node);
        if (Result is not null) Parent.Insert(Math.Clamp(Index, 0, Parent.Children.Count), Result);
    }
}

/// <summary>A merge Photoshop would refuse, with its reason.</summary>
public sealed class MergeRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// Photoshop's merge commands: Merge Down (and Merge Clipping Mask), Merge Layers, Merge Visible, Merge Group, Flatten
/// Image and Stamp Visible. The merged layers are drawn in isolation (on transparency) exactly as the compositor draws
/// them in the document, so wherever Photoshop's merge keeps the image unchanged (an opaque layer below, normal blending
/// into it), the composite stays pixel-identical.
/// </summary>
public static class LayerMerger
{
    private static void CheckMode(Document doc)
    {
        if (doc.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale))
            throw new MergeRefusedException($"Merging layers in {doc.ColorMode} documents is not supported yet.");
    }

    /// <summary>
    /// ⌘E on one layer: merges it into the layer below, which keeps its name, blend mode and opacity. On the base of a
    /// clipping mask it merges the clipped layers into the base instead (Merge Clipping Mask); on a group, Merge Group.
    /// </summary>
    public static MergePlan MergeDown(Document doc, LayerNode upper)
    {
        CheckMode(doc);
        if (upper is LayerGroup g) return MergeGroup(doc, g);
        var parent = upper.Parent ?? throw new MergeRefusedException("The layer is not in the document.");
        int i = parent.IndexOf(upper);

        // The base of a clipping mask merges the layers clipped to it.
        var clipped = parent.Children.Skip(i + 1).TakeWhile(c => c.Clipped).ToList();
        if (!upper.Clipped && clipped.Count > 0)
        {
            if (upper.IsLocked(LayerLocks.Pixels))
                throw new MergeRefusedException("Could not complete the Merge Clipping Mask command because the layer is locked.");
            return MergeInto(doc, "Merge Clipping Mask", upper, [upper, .. clipped]);
        }

        if (i == 0) throw new MergeRefusedException("Could not complete the Merge Down command because there is no layer below.");
        var lower = parent.Children[i - 1];
        string? problem = lower switch
        {
            LayerGroup => "Could not complete the Merge Down command because the layer below is a group.",
            AdjustmentLayer => "Could not complete the Merge Down command because the layer below is an adjustment layer.",
            { Visible: false } => "Could not complete the Merge Down command because the target layer is hidden.",
            _ when lower.IsLocked(LayerLocks.Pixels) => "Could not complete the Merge Down command because the target layer is locked.",
            _ => null,
        };
        if (problem is not null) throw new MergeRefusedException(problem);
        return MergeInto(doc, "Merge Down", lower, [lower, upper]);
    }

    /// <summary>Renders <paramref name="nodes"/> (siblings, bottom first, the first being <paramref name="target"/>) into a layer taking the target's place and settings.</summary>
    private static MergePlan MergeInto(Document doc, string command, LayerNode target, IReadOnlyList<LayerNode> nodes)
    {
        var clones = nodes.Select(n =>
        {
            var c = Clone(n);
            if (ReferenceEquals(n, target))
            {
                // The target's own blending applies to the merged layer, not inside it.
                c.Opacity = 1f;
                c.BlendMode = BlendMode.Normal;
                c.Clipped = false;
                c.Visible = true;
            }
            return c;
        }).ToList();
        var result = NewLayer(doc, target.Name, clones);
        result.BlendMode = target.BlendMode == BlendMode.PassThrough ? BlendMode.Normal : target.BlendMode;
        result.Opacity = target.Opacity;
        result.Clipped = target.Clipped;
        KeepPanelSettings(target, result);
        var parent = target.Parent!;
        int index = parent.IndexOf(target) - nodes.Count(n => n.Parent == parent && parent.IndexOf(n) < parent.IndexOf(target));
        return new MergePlan(command, result, parent, index, nodes);
    }

    /// <summary>Merge Group: the group becomes one layer with its name, opacity and (unless Pass Through) blend mode.</summary>
    public static MergePlan MergeGroup(Document doc, LayerGroup group)
    {
        CheckMode(doc);
        if (group.Parent is null) throw new MergeRefusedException("The group is not in the document.");
        if (group.IsLocked(LayerLocks.Pixels)) throw new MergeRefusedException("Could not complete the Merge Group command because the group is locked.");
        return MergeInto(doc, "Merge Group", group, [group]);
    }

    /// <summary>
    /// Merge Layers (⌘E with several layers selected): the visible ones are drawn together into a layer in the top-most's
    /// place and with its name; hidden ones are discarded, as in Photoshop.
    /// </summary>
    public static MergePlan MergeLayers(Document doc, IEnumerable<LayerNode> selected)
    {
        CheckMode(doc);
        var nodes = TopLevel(doc, selected);
        if (nodes.Count < 2) throw new MergeRefusedException("Select two or more layers to merge.");
        if (nodes.FirstOrDefault(n => n.IsLocked(LayerLocks.Pixels)) is { } locked)
            throw new MergeRefusedException($"Could not complete the Merge Layers command because \"{locked.Name}\" is locked.");
        var top = nodes[^1];
        var clones = nodes.Where(n => n.Visible).Select(Clone).ToList();
        var result = NewLayer(doc, top.Name, clones);
        KeepPanelSettings(top, result);
        var parent = top.Parent!;
        int index = parent.IndexOf(top) + 1 - nodes.Count(n => n.Parent == parent);
        return new MergePlan("Merge Layers", result, parent, index, nodes);
    }

    /// <summary>
    /// Merge Visible (⇧⌘E): every visible top-level layer and group becomes one layer where the top-most of them was, named
    /// after <paramref name="named"/> when that is one of them; hidden layers stay.
    /// </summary>
    public static MergePlan MergeVisible(Document doc, LayerNode? named = null)
    {
        CheckMode(doc);
        var nodes = doc.Root.Children.Where(n => n.Visible).ToList();
        if (nodes.Count == 0) throw new MergeRefusedException("Could not complete the Merge Visible command because there are no visible layers.");
        if (nodes.FirstOrDefault(n => n.IsLocked(LayerLocks.Pixels)) is { } locked)
            throw new MergeRefusedException($"Could not complete the Merge Visible command because \"{locked.Name}\" is locked.");
        var top = nodes[^1];
        var nameFrom = named is not null && nodes.Contains(TopLevelAncestor(named)) ? TopLevelAncestor(named) : top;
        var result = NewLayer(doc, nameFrom.Name, nodes.Select(Clone).ToList());
        KeepPanelSettings(nameFrom, result);
        int index = doc.Root.IndexOf(top) + 1 - nodes.Count;
        return new MergePlan("Merge Visible", result, doc.Root, index, nodes);
    }

    /// <summary>Flatten Image: one opaque Background layer over white; hidden layers are discarded.</summary>
    public static MergePlan Flatten(Document doc)
    {
        CheckMode(doc);
        var all = doc.Root.Children.ToList();
        var clones = all.Where(n => n.Visible).Select(Clone).ToList();
        var composite = RenderClones(doc, clones);
        // Flattening composites onto white and drops transparency, as Photoshop's Background has none.
        var opaque = OverWhite(composite);
        var result = new PixelLayer { Name = "Background", Bounds = doc.Bounds, Pixels = opaque };
        return new MergePlan("Flatten Image", result, doc.Root, 0, all);
    }

    /// <summary>Stamp Visible (⌥⇧⌘E): a new layer holding everything visible, placed above <paramref name="above"/> (or on top).</summary>
    public static MergePlan StampVisible(Document doc, LayerNode? above, string name)
    {
        CheckMode(doc);
        var clones = doc.Root.Children.Where(n => n.Visible).Select(Clone).ToList();
        if (clones.Count == 0) throw new MergeRefusedException("Could not complete the Stamp Visible command because there are no visible layers.");
        var result = NewLayer(doc, name, clones);
        var parent = above?.Parent ?? doc.Root;
        int index = above?.Parent is { } p ? p.IndexOf(above) + 1 : doc.Root.Children.Count;
        return new MergePlan("Stamp Visible", result, parent, index, []);
    }

    /// <summary>The composite of <paramref name="nodes"/> alone (they are copied into an empty document of the same size), as a straight-alpha raster covering the canvas.</summary>
    public static Raster Render(Document doc, IEnumerable<LayerNode> nodes) => RenderClones(doc, nodes.Select(Clone).ToList());

    // ---- Helpers ----------------------------------------------------------------------------------

    /// <summary>The selected layers without those inside a selected group, in stacking order (bottom first).</summary>
    public static List<LayerNode> TopLevel(Document doc, IEnumerable<LayerNode> selected)
    {
        var set = selected.ToHashSet();
        var order = doc.Root.Descendants().Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i);
        return set.Where(n => order.ContainsKey(n) && !Ancestors(n).Any(set.Contains)).OrderBy(n => order[n]).ToList();
    }

    private static IEnumerable<LayerGroup> Ancestors(LayerNode node)
    {
        for (var g = node.Parent; g is not null; g = g.Parent) yield return g;
    }

    private static LayerNode TopLevelAncestor(LayerNode node)
    {
        while (node.Parent is { Parent: not null } p) node = p;
        return node;
    }

    private static void KeepPanelSettings(LayerNode from, PixelLayer to)
    {
        to.Color = from.Color;
        to.LinkGroup = from.LinkGroup;
        to.Locks = from.Locks & ~LayerLocks.All;
    }

    private static PixelLayer NewLayer(Document doc, string name, List<LayerNode> clones)
    {
        var raster = RenderClones(doc, clones);
        var (pixels, bounds) = Trim(raster);
        return new PixelLayer { Name = name, Pixels = pixels, Bounds = bounds };
    }

    private static Raster RenderClones(Document doc, List<LayerNode> clones)
    {
        var temp = new Document(doc.Width, doc.Height, doc.ColorMode, doc.BitDepth)
        {
            GlobalLightAngle = doc.GlobalLightAngle,
            GlobalLightAltitude = doc.GlobalLightAltitude,
        };
        temp.Patterns.AddRange(doc.Patterns);
        foreach (var c in clones) temp.Root.Add(c);
        return Compositor.Render(temp).ToRaster(doc.ColorMode, doc.BitDepth);
    }

    /// <summary>A copy of a layer or group for rendering elsewhere: same settings, sharing pixels and masks (never changed in place).</summary>
    public static LayerNode Clone(LayerNode node)
    {
        LayerNode copy = node switch
        {
            PixelLayer p => new PixelLayer { Bounds = p.Bounds, Pixels = p.Pixels, Mask = p.Mask },
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
        copy.Locks = node.Locks;
        copy.Color = node.Color;
        foreach (var tag in node.Tags) copy.Tags.Add(tag);
        if (node is LayerGroup group)
            foreach (var child in group.Children) ((LayerGroup)copy).Add(Clone(child));
        return copy;
    }

    /// <summary>Crops a canvas-sized raster to its non-transparent pixels.</summary>
    private static (Raster? Pixels, PixelRect Bounds) Trim(Raster raster)
    {
        var box = raster.Alpha is { } a ? Coverage.NonZeroBounds(a) : PixelRect.FromSize(raster.Width, raster.Height);
        if (box.IsEmpty) return (null, PixelRect.Empty);
        if (box == PixelRect.FromSize(raster.Width, raster.Height)) return (raster, box);
        Plane Crop(Plane p)
        {
            int bpp = p.BitDepth / 8, rowBytes = box.Width * bpp;
            var data = new byte[rowBytes * box.Height];
            for (int y = 0; y < box.Height; y++)
                Buffer.BlockCopy(p.Data, ((box.Top + y) * p.Width + box.Left) * bpp, data, y * rowBytes, rowBytes);
            return new Plane(box.Width, box.Height, p.BitDepth, data);
        }
        return (new Raster(raster.ColorMode, raster.ColorPlanes.Select(Crop).ToArray(), raster.Alpha is { } al ? Crop(al) : null), box);
    }

    /// <summary>The raster composited onto white, without transparency.</summary>
    private static Raster OverWhite(Raster raster)
    {
        if (raster.Alpha is not { } alpha) return raster;
        var planes = raster.ColorPlanes.Select(p =>
        {
            var o = Plane.Create(p.Width, p.Height, p.BitDepth);
            int n = p.Width * p.Height;
            for (int i = 0; i < n; i++)
            {
                float a = alpha.GetNormalized(i), v = p.GetNormalized(i) * a + (1f - a);
                switch (p.BitDepth)
                {
                    case 8: o.Data[i] = RgbaConverter.ToByte(v); break;
                    case 16: o.AsUInt16()[i] = (ushort)Math.Clamp(MathF.Round(v * 65535f), 0f, 65535f); break;
                    default: o.AsSingle()[i] = v; break;
                }
            }
            return o;
        }).ToArray();
        return new Raster(raster.ColorMode, planes, null);
    }
}
