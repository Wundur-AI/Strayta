using Strayta.Core;
using Strayta.Psd;
using Strayta.Psd.Descriptors;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>
/// Smart objects through distort, perspective and warp: the placed-layer descriptor's corners ("Trnf" and
/// "nonAffineTransform") and warp ("warp", or "quiltWarp" for a split mesh) are rewritten so the layer stays a smart
/// object and Photoshop draws the same result from its content.
/// </summary>
internal static class SmartObjectTransform
{
    /// <summary>The map from the smart object's placed size (0..Width × 0..Height) to the document: its corners.</summary>
    public static Projective Placement(PsdSmartObject so) => Projective.RectToQuad(so.Width, so.Height, so.Corners);

    /// <summary>
    /// The record with the smart object's corners set to <paramref name="corners"/> (TL, TR, BR, BL) and, when
    /// <paramref name="warp"/> is given, its warp replaced (a none warp removes it). Null when the record has no placed-layer data.
    /// </summary>
    public static PsdLayerRecord? WithPlacement(PsdLayerRecord record, IReadOnlyList<(double X, double Y)> corners, WarpSpec? warp)
    {
        if (PsdSmartObjects.ReadPlaced(record) is not { } placed) return null;
        var list = new ListValue(corners.SelectMany(c => new DescriptorValue[] { new DoubleValue(c.X), new DoubleValue(c.Y) }).ToList());
        placed = placed.With("Trnf", list).With("nonAffineTransform", list);
        if (warp is not null)
        {
            bool split = warp.Rows != 4 || warp.Columns != 4 || warp.SlicesX is { Count: > 2 } || warp.SlicesY is { Count: > 2 };
            if (split)
            {
                // A split mesh goes in "quiltWarp"; "warp" says none (framing the same bounds), as Photoshop pairs them.
                var quilt = PsdSmartObjects.WarpDescriptor(warp);
                quilt = new Descriptor { Name = quilt.Name, ClassId = "quiltWarp", Items = quilt.Items };
                placed = placed.With("warp", new ObjectValue(PsdSmartObjects.WarpDescriptor(new WarpSpec { Bounds = warp.Bounds })));
                placed = InsertAfter(placed, "warp", "quiltWarp", new ObjectValue(quilt));
            }
            else
            {
                placed = placed.With("warp", new ObjectValue(PsdSmartObjects.WarpDescriptor(warp))).Without("quiltWarp");
            }
        }
        return PsdSmartObjects.WithPlaced(record, placed);
    }

    private static Descriptor InsertAfter(Descriptor d, string after, string key, DescriptorValue value)
    {
        var items = d.Items.Where(kv => kv.Key != key).ToList();
        int at = items.FindIndex(kv => kv.Key == after);
        items.Insert(at < 0 ? items.Count : at + 1, new(key, value));
        return new Descriptor { Name = d.Name, ClassId = d.ClassId, Items = items };
    }

    /// <summary>
    /// Corners for a warp whose control points (in the placed size's frame) are <paramref name="points"/>, placed
    /// through <paramref name="place"/>: Photoshop frames the points' bounding box with the corners and scales the
    /// points onto the placed rectangle, so the box's corners are where that bounding box lands.
    /// </summary>
    public static (double X, double Y)[] CornersForPoints(IReadOnlyList<(double X, double Y)> points, Projective place)
    {
        double x0 = points.Min(p => p.X), x1 = points.Max(p => p.X), y0 = points.Min(p => p.Y), y1 = points.Max(p => p.Y);
        return [place.Apply(x0, y0), place.Apply(x1, y0), place.Apply(x1, y1), place.Apply(x0, y1)];
    }
}
