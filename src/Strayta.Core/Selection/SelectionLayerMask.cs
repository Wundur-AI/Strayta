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
}
