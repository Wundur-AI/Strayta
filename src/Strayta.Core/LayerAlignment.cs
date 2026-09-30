namespace Strayta.Core;

/// <summary>Photoshop's Align commands (Layer › Align, the Move tool's options bar).</summary>
public enum AlignEdge
{
    Left,
    HorizontalCenter,
    Right,
    Top,
    VerticalCenter,
    Bottom,
}

/// <summary>Photoshop's Distribute commands: evenly spaced edges or centres, or equal gaps between the boxes.</summary>
public enum DistributeMode
{
    Left,
    HorizontalCenter,
    Right,
    Top,
    VerticalCenter,
    Bottom,

    /// <summary>Equal horizontal space between neighbouring boxes.</summary>
    HorizontalSpacing,

    /// <summary>Equal vertical space between neighbouring boxes.</summary>
    VerticalSpacing,
}

/// <summary>
/// The arithmetic of aligning and distributing layers: given each layer's box, the whole-pixel offset that moves it.
/// Boxes that are empty (layers without pixels) never move.
/// </summary>
public static class LayerAlignment
{
    /// <summary>
    /// Offsets that line every box up with <paramref name="edge"/> of <paramref name="reference"/> (the selected layers'
    /// combined box, the canvas or the selection, per Photoshop's rules). Centres round down, as Photoshop's do.
    /// </summary>
    public static (int Dx, int Dy)[] Align(IReadOnlyList<PixelRect> boxes, AlignEdge edge, PixelRect reference)
    {
        var result = new (int, int)[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            if (b.IsEmpty) continue;
            result[i] = edge switch
            {
                AlignEdge.Left => (reference.Left - b.Left, 0),
                AlignEdge.Right => (reference.Right - b.Right, 0),
                AlignEdge.HorizontalCenter => (CenterOffset(reference.Left, reference.Right, b.Left, b.Right), 0),
                AlignEdge.Top => (0, reference.Top - b.Top),
                AlignEdge.Bottom => (0, reference.Bottom - b.Bottom),
                AlignEdge.VerticalCenter => (0, CenterOffset(reference.Top, reference.Bottom, b.Top, b.Bottom)),
                _ => (0, 0),
            };
        }
        return result;
    }

    /// <summary>The union of the non-empty boxes (empty when there are none).</summary>
    public static PixelRect Union(IEnumerable<PixelRect> boxes)
    {
        PixelRect? u = null;
        foreach (var b in boxes)
        {
            if (b.IsEmpty) continue;
            u = u is { } a ? new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)) : b;
        }
        return u ?? PixelRect.Empty;
    }

    /// <summary>
    /// Offsets that distribute the boxes: the outermost two stay, and the others are spaced evenly between them by the
    /// chosen edge or centre, or so the gaps between neighbours are equal. Needs three or more non-empty boxes.
    /// </summary>
    public static (int Dx, int Dy)[] Distribute(IReadOnlyList<PixelRect> boxes, DistributeMode mode)
    {
        var result = new (int, int)[boxes.Count];
        var items = Enumerable.Range(0, boxes.Count).Where(i => !boxes[i].IsEmpty).ToList();
        if (items.Count < 3) return result;
        bool horizontal = mode is DistributeMode.Left or DistributeMode.HorizontalCenter or DistributeMode.Right or DistributeMode.HorizontalSpacing;

        // The coordinate each box is spaced by.
        double Key(PixelRect b) => mode switch
        {
            DistributeMode.Left => b.Left,
            DistributeMode.Right => b.Right,
            DistributeMode.HorizontalCenter or DistributeMode.HorizontalSpacing => (b.Left + b.Right) / 2.0,
            DistributeMode.Top => b.Top,
            DistributeMode.Bottom => b.Bottom,
            _ => (b.Top + b.Bottom) / 2.0,
        };
        // Order along the axis; ties keep their order in the list.
        var order = items.OrderBy(i => Key(boxes[i])).ThenBy(i => i).ToList();

        if (mode is DistributeMode.HorizontalSpacing or DistributeMode.VerticalSpacing)
        {
            int Start(PixelRect b) => horizontal ? b.Left : b.Top;
            int Size(PixelRect b) => horizontal ? b.Width : b.Height;
            var first = boxes[order[0]];
            var last = boxes[order[^1]];
            double span = (horizontal ? last.Right : last.Bottom) - Start(first);
            double gap = (span - order.Sum(i => Size(boxes[i]))) / (order.Count - 1);
            double pos = Start(first);
            for (int k = 0; k < order.Count; k++)
            {
                var b = boxes[order[k]];
                if (k > 0 && k < order.Count - 1)
                {
                    int d = (int)Math.Round(pos) - Start(b);
                    result[order[k]] = horizontal ? (d, 0) : (0, d);
                }
                pos += Size(b) + gap;
            }
            return result;
        }

        double from = Key(boxes[order[0]]), to = Key(boxes[order[^1]]);
        for (int k = 1; k < order.Count - 1; k++)
        {
            var b = boxes[order[k]];
            double target = from + (to - from) * k / (order.Count - 1);
            int d = (int)Math.Round(target - Key(b), MidpointRounding.AwayFromZero);
            result[order[k]] = horizontal ? (d, 0) : (0, d);
        }
        return result;
    }

    /// <summary>The whole-pixel shift centring [lo, hi) in [refLo, refHi).</summary>
    private static int CenterOffset(int refLo, int refHi, int lo, int hi) =>
        (int)Math.Floor((refLo + refHi - (lo + hi)) / 2.0);
}
