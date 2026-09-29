namespace Strayta.Core.Selection;

/// <summary>Layer masks made from selections (Layer › Layer Mask › Reveal Selection / Hide Selection).</summary>
public static class SelectionLayerMask
{
    /// <summary>
    /// A mask that shows the selected area (<paramref name="reveal"/>) or hides it. Pixels are stored only over the
    /// selection's bounds; the rest of the canvas takes the default color (black when revealing, white when hiding),
    /// the same layout Photoshop writes. Samples are at <paramref name="bitDepth"/> (the document's depth).
    /// </summary>
    public static LayerMask Create(SelectionMask selection, bool reveal, int bitDepth)
    {
        var b = selection.Bounds;
        var plane = Plane.Create(b.Width, b.Height, bitDepth);
        int w = b.Width;
        Parallel.For(0, b.Height, row =>
        {
            var buffer = new byte[w];
            selection.CopyRow(b.Top + row, b.Left, buffer);
            int start = row * w;
            switch (bitDepth)
            {
                case 8:
                    var dst = plane.Data.AsSpan(start, w);
                    for (int x = 0; x < w; x++) dst[x] = reveal ? buffer[x] : (byte)(255 - buffer[x]);
                    break;
                case 16:
                    var d16 = plane.AsUInt16().Slice(start, w);
                    for (int x = 0; x < w; x++) d16[x] = (ushort)((reveal ? buffer[x] : 255 - buffer[x]) * 257);
                    break;
                default:
                    var d32 = plane.AsSingle().Slice(start, w);
                    for (int x = 0; x < w; x++) d32[x] = (reveal ? buffer[x] : 255 - buffer[x]) / 255f;
                    break;
            }
        });
        return new LayerMask { Bounds = b, Pixels = plane, DefaultColor = reveal ? (byte)0 : (byte)255 };
    }

    /// <summary>The selection's coverage over <paramref name="region"/> (row-major, 255 = selected; all 0 for no selection).</summary>
    public static byte[] Coverage(SelectionMask? selection, PixelRect region) => MaskFilters.Extract(selection, region);

    /// <summary>
    /// The selection a mask describes over <paramref name="canvas"/> (white selected, black not), as Select and Mask
    /// starts from when a layer mask is refined. Null when the mask hides everything.
    /// </summary>
    public static SelectionMask? ToSelection(LayerMask mask, PixelRect canvas)
    {
        int w = canvas.Width, h = canvas.Height;
        var coverage = new byte[(long)w * h];
        coverage.AsSpan().Fill(mask.DefaultColor);
        var inside = mask.Bounds.Intersect(canvas);
        if (mask.Pixels is { } p && !inside.IsEmpty)
        {
            var b = mask.Bounds;
            Parallel.For(inside.Top, inside.Bottom, y =>
            {
                int src = (y - b.Top) * b.Width + (inside.Left - b.Left), dst = (y - canvas.Top) * w + (inside.Left - canvas.Left);
                for (int x = 0; x < inside.Width; x++) coverage[dst + x] = (byte)MathF.Round(p.GetNormalized(src + x) * 255f);
            });
        }
        return SelectionMask.FromCoverage(canvas, coverage);
    }
}
