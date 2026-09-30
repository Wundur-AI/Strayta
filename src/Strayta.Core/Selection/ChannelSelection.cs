namespace Strayta.Core.Selection;

/// <summary>
/// Saved selections: Select › Save Selection (a selection into a new channel, or added to, subtracted from or
/// intersected with an existing one) and Load Selection (a channel as the selection, optionally inverted and combined
/// with the current one). Channels cover the canvas; white is selected unless the channel's color marks selected
/// areas (<see cref="ChannelKind.SelectedAreas"/>), which are stored black.
/// </summary>
public static class ChannelSelection
{
    /// <summary>A channel of the canvas size holding <paramref name="selection"/> (all white for null: everything selected).</summary>
    public static Plane FromSelection(SelectionMask? selection, PixelRect canvas, int bitDepth, bool invert = false)
    {
        int w = canvas.Width, h = canvas.Height;
        var plane = Plane.Create(w, h, bitDepth);
        Parallel.For(0, h, row =>
        {
            var buffer = new byte[w];
            if (selection is null) buffer.AsSpan().Fill(255);
            else selection.CopyRow(canvas.Top + row, canvas.Left, buffer);
            if (invert)
                for (int x = 0; x < w; x++) buffer[x] = (byte)(255 - buffer[x]);
            WriteRow(plane, row * w, buffer);
        });
        return plane;
    }

    /// <summary>The selection a channel holds (inverted for selected-areas channels or when asked); null when nothing is selected.</summary>
    public static SelectionMask? ToSelection(Plane channel, PixelRect canvas, bool invert = false)
    {
        int w = Math.Min(channel.Width, canvas.Width), h = Math.Min(channel.Height, canvas.Height);
        if (w <= 0 || h <= 0) return null;
        var coverage = new byte[(long)canvas.Width * canvas.Height];
        Parallel.For(0, h, y =>
        {
            int src = y * channel.Width, dst = y * canvas.Width;
            for (int x = 0; x < w; x++)
            {
                byte v = ToByte(channel.GetNormalized(src + x));
                coverage[dst + x] = invert ? (byte)(255 - v) : v;
            }
        });
        return SelectionMask.FromCoverage(canvas, coverage, canvas);
    }

    /// <summary>The selection a document channel holds, as Load Selection reads it (<paramref name="invert"/>: its Invert box).</summary>
    public static SelectionMask? ToSelection(DocumentChannel channel, PixelRect canvas, bool invert = false) =>
        ToSelection(channel.Pixels, canvas, invert ^ channel.InvertsSelection);

    /// <summary>
    /// Save Selection into an existing channel: <paramref name="mode"/> replaces it with the selection, adds the
    /// selection (maximum), subtracts it, or intersects with it (minimum), all in terms of what is selected.
    /// </summary>
    public static Plane Combine(Plane channel, SelectionMask? selection, SelectionMode mode, PixelRect canvas, bool channelInverted = false)
    {
        int w = channel.Width, h = channel.Height;
        var plane = Plane.Create(w, h, channel.BitDepth);
        Parallel.For(0, h, row =>
        {
            var sel = new byte[w];
            if (selection is null) sel.AsSpan().Fill(255);
            else selection.CopyRow(canvas.Top + row, canvas.Left, sel);
            for (int x = 0; x < w; x++)
            {
                int i = row * w + x;
                float c = channel.GetNormalized(i);
                if (channelInverted) c = 1f - c;
                float s = sel[x] / 255f;
                float v = mode switch
                {
                    SelectionMode.Add => MathF.Max(c, s),
                    SelectionMode.Subtract => c * (1f - s),
                    SelectionMode.Intersect => MathF.Min(c, s),
                    _ => s,
                };
                Set(plane, i, channelInverted ? 1f - v : v);
            }
        });
        return plane;
    }

    /// <summary>A copy with every value inverted (Channel Options switching between masked and selected areas).</summary>
    public static Plane Invert(Plane channel)
    {
        var plane = Plane.Create(channel.Width, channel.Height, channel.BitDepth);
        int n = channel.Width * channel.Height;
        switch (channel.BitDepth)
        {
            case 8:
                for (int i = 0; i < n; i++) plane.Data[i] = (byte)(255 - channel.Data[i]);
                break;
            case 16:
            {
                var s = channel.AsUInt16();
                var d = plane.AsUInt16();
                for (int i = 0; i < n; i++) d[i] = (ushort)(65535 - s[i]);
                break;
            }
            default:
            {
                var s = channel.AsSingle();
                var d = plane.AsSingle();
                for (int i = 0; i < n; i++) d[i] = 1f - s[i];
                break;
            }
        }
        return plane;
    }

    /// <summary>A channel of one value everywhere (0 black, 1 white).</summary>
    public static Plane Solid(int width, int height, int bitDepth, float value)
    {
        var plane = Plane.Create(width, height, bitDepth);
        switch (bitDepth)
        {
            case 8: plane.Data.AsSpan().Fill((byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f)); break;
            case 16: plane.AsUInt16().Fill((ushort)MathF.Round(Math.Clamp(value, 0f, 1f) * 65535f)); break;
            default: plane.AsSingle().Fill(value); break;
        }
        return plane;
    }

    /// <summary>
    /// A channel as a layer mask over the whole canvas, so the mask tools (brush, gradient, filters, fills) can paint it;
    /// <see cref="FromMask"/> turns their result back into a channel.
    /// </summary>
    public static LayerMask AsMask(Plane channel) =>
        new() { Bounds = PixelRect.FromSize(channel.Width, channel.Height), Pixels = channel, DefaultColor = 0 };

    /// <summary>
    /// The canvas-sized channel a mask describes (its pixels where it has them, its default color elsewhere); the mask's
    /// own plane when it already covers exactly the canvas.
    /// </summary>
    public static Plane FromMask(LayerMask mask, int width, int height, int bitDepth)
    {
        var canvas = PixelRect.FromSize(width, height);
        if (mask.Pixels is { } p && mask.Bounds == canvas && p.BitDepth == bitDepth) return p;
        var plane = Solid(width, height, bitDepth, mask.DefaultColor / 255f);
        if (mask.Pixels is not { } px) return plane;
        var b = mask.Bounds;
        var inside = b.Intersect(canvas);
        if (inside.IsEmpty) return plane;
        Parallel.For(inside.Top, inside.Bottom, y =>
        {
            for (int x = inside.Left; x < inside.Right; x++)
                Set(plane, y * width + x, px.GetNormalized((y - b.Top) * b.Width + (x - b.Left)));
        });
        return plane;
    }

    private static byte ToByte(float v) => v <= 0f ? (byte)0 : v >= 1f ? (byte)255 : (byte)(v * 255f + 0.5f);

    private static void WriteRow(Plane plane, int start, byte[] values)
    {
        int w = values.Length;
        switch (plane.BitDepth)
        {
            case 8: values.CopyTo(plane.Data.AsSpan(start, w)); break;
            case 16:
            {
                var d = plane.AsUInt16().Slice(start, w);
                for (int x = 0; x < w; x++) d[x] = (ushort)(values[x] * 257);
                break;
            }
            default:
            {
                var d = plane.AsSingle().Slice(start, w);
                for (int x = 0; x < w; x++) d[x] = values[x] / 255f;
                break;
            }
        }
    }

    private static void Set(Plane p, int i, float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        switch (p.BitDepth)
        {
            case 8: p.Data[i] = (byte)MathF.Round(v * 255f); break;
            case 16: p.AsUInt16()[i] = (ushort)MathF.Round(v * 65535f); break;
            default: p.AsSingle()[i] = v; break;
        }
    }
}
