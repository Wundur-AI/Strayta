using Strayta.Core;
using Strayta.Psd;

namespace Strayta.Editor.Editing;

/// <summary>Creates new layers, groups and documents.</summary>
public static class LayerFactory
{
    /// <summary>A name like "Layer 3" that is not used yet in the document.</summary>
    public static string NextName(Document doc, string prefix)
    {
        var used = doc.Root.Descendants().Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        for (int i = 1; ; i++)
            if (!used.Contains($"{prefix} {i}")) return $"{prefix} {i}";
    }

    public static Document NewDocument(int width, int height, bool whiteBackground)
    {
        var doc = new Document(width, height, ColorMode.Rgb, 8);
        if (whiteBackground)
        {
            Plane White() => new(width, height, 8, Enumerable.Repeat((byte)255, width * height).ToArray());
            doc.Root.Add(new PixelLayer
            {
                Name = "Background",
                Bounds = doc.Bounds,
                Pixels = new Raster(ColorMode.Rgb, [White(), White(), White()], null),
            });
        }
        else
        {
            doc.Root.Add(new PixelLayer { Name = "Layer 1" });
        }
        return doc;
    }

    /// <summary>
    /// A copy that shares pixel data (rasters are never modified in place) but not identity, so it can be
    /// edited independently. PSD round-trip data is copied without layer IDs, so duplicated text, effects and
    /// smart objects survive saving.
    /// </summary>
    public static LayerNode Duplicate(LayerNode node)
    {
        LayerNode copy = node switch
        {
            PixelLayer p => new PixelLayer { Bounds = p.Bounds, Pixels = p.Pixels, Mask = p.Mask, TransparencyLocked = p.TransparencyLocked },
            AdjustmentLayer a => new AdjustmentLayer { Adjustment = a.Adjustment, Kind = a.Kind, Mask = a.Mask },
            LayerGroup g => DuplicateGroup(g),
            _ => throw new NotSupportedException(),
        };
        copy.Name = node.Name + " copy";
        copy.Visible = node.Visible;
        copy.Opacity = node.Opacity;
        copy.FillOpacity = node.FillOpacity;
        copy.BlendMode = node.BlendMode;
        copy.Clipped = node.Clipped;
        copy.Effects = node.Effects;
        foreach (var tag in node.Tags) copy.Tags.Add(tag);
        copy.SourceData = node.SourceData switch
        {
            PsdLayerRecord r => r.ForDuplicate(),
            PsdGroupRecords g => new PsdGroupRecords(g.Folder.ForDuplicate(), g.Divider?.ForDuplicate()),
            _ => null,
        };
        return copy;
    }

    private static LayerGroup DuplicateGroup(LayerGroup g)
    {
        var copy = new LayerGroup { Expanded = g.Expanded, Mask = g.Mask };
        foreach (var child in g.Children)
        {
            var c = Duplicate(child);
            c.Name = child.Name;
            copy.Add(c);
        }
        return copy;
    }
}
