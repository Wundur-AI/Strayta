namespace Strayta.Core.Painting;

/// <summary>
/// Editing with only some color channels targeted (Photoshop's Channels panel with, say, just Red selected): any edit
/// of a layer's pixels (a brush stroke, fill, filter or adjustment) is computed as usual and then only the targeted
/// channels take its result. The other channels and the layer's transparency stay exactly as they were, and the layer
/// keeps its bounds.
/// </summary>
public static class ChannelRestriction
{
    /// <summary>
    /// The layer's pixels after an edit that produced <paramref name="edited"/> over <paramref name="editedBounds"/>,
    /// with only the channels marked in <paramref name="targeted"/> changed. Returns the old pixels unchanged when the
    /// layer is empty (there is no channel data to change) or the edit emptied it.
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds) Keep(Raster? old, PixelRect oldBounds, Raster? edited, PixelRect editedBounds,
        IReadOnlyList<bool> targeted)
    {
        if (old is null || edited is null || oldBounds.IsEmpty) return (old, oldBounds);
        var planes = new Plane[old.ColorPlanes.Count];
        bool changed = false;
        for (int k = 0; k < planes.Length; k++)
        {
            bool target = k < targeted.Count && targeted[k] && k < edited.ColorPlanes.Count;
            planes[k] = target ? Place(old.ColorPlanes[k], oldBounds, edited.ColorPlanes[k], editedBounds) : old.ColorPlanes[k];
            changed |= !ReferenceEquals(planes[k], old.ColorPlanes[k]);
        }
        return changed ? (new Raster(old.ColorMode, planes, old.Alpha), oldBounds) : (old, oldBounds);
    }

    /// <summary>The edited plane's values over the old bounds; where the edit did not reach, the old values.</summary>
    private static Plane Place(Plane old, PixelRect oldBounds, Plane edited, PixelRect editedBounds)
    {
        if (editedBounds == oldBounds && edited.BitDepth == old.BitDepth) return edited;
        var plane = new Plane(old.Width, old.Height, old.BitDepth, (byte[])old.Data.Clone());
        var inside = oldBounds.Intersect(editedBounds);
        if (inside.IsEmpty) return plane;
        Parallel.For(inside.Top, inside.Bottom, y =>
        {
            for (int x = inside.Left; x < inside.Right; x++)
            {
                float v = edited.GetNormalized((y - editedBounds.Top) * editedBounds.Width + (x - editedBounds.Left));
                int i = (y - oldBounds.Top) * oldBounds.Width + (x - oldBounds.Left);
                switch (plane.BitDepth)
                {
                    case 8: plane.Data[i] = (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f); break;
                    case 16: plane.AsUInt16()[i] = (ushort)MathF.Round(Math.Clamp(v, 0f, 1f) * 65535f); break;
                    default: plane.AsSingle()[i] = v; break;
                }
            }
        });
        return plane;
    }
}
