using System.Buffers.Binary;
using System.Text;
using Strayta.Core.Paths;

namespace Strayta.Psd;

/// <summary>
/// Photoshop's path records, as vector masks ('vmsk' / 'vsms'), saved paths (image resources 2000–2997), the work path
/// (1025) and the clipping path share them. Each record is 26 bytes and starts with a 2-byte selector:
/// <list type="bullet">
/// <item>0 / 3: a closed / open subpath's length record: the number of knot records that follow (2 bytes), then the
/// path operation (2 bytes: 0 exclude, 1 combine, 2 subtract, 3 intersect; -1 in files from before CS6, meaning
/// combine), then fields Adobe does not document (kept as they were).</item>
/// <item>1, 2 / 4, 5: a knot of a closed / open subpath, linked (smooth) or unlinked: the control point the curve
/// arrives through, the anchor, and the control point it leaves through, each a vertical then a horizontal 8.24
/// fixed-point fraction of the canvas height and width.</item>
/// <item>6: the path fill rule record (24 zero bytes).</item>
/// <item>7: the clipboard record: top, left, bottom, right and resolution as 8.24 fixed values, 4 bytes padding.</item>
/// <item>8: the initial fill rule record: 1 when the area starts filled (an inverted mask), 0 when empty.</item>
/// </list>
/// Coordinates are converted to document pixels as <c>fixed / 2^24 · size</c>, which is exact in doubles, so writing
/// an unchanged path gives back the same bytes.
/// </summary>
public static class PsdPaths
{
    private const int RecordSize = 26;

    /// <summary>
    /// Reads the path records in <paramref name="data"/> from <paramref name="start"/>, with coordinates scaled to a
    /// <paramref name="width"/>×<paramref name="height"/> canvas. Knots beyond the data, or records of unknown kind, are
    /// skipped.
    /// </summary>
    public static VectorPath Decode(ReadOnlySpan<byte> data, int start, int width, int height)
    {
        var subpaths = new List<Subpath>();
        var layout = new StringBuilder();
        bool? initialFill = null;
        PathClipboard? clipboard = null;
        int at = start;
        while (at + RecordSize <= data.Length)
        {
            var r = data.Slice(at, RecordSize);
            int selector = BinaryPrimitives.ReadInt16BigEndian(r);
            at += RecordSize;
            switch (selector)
            {
                case 0 or 3:
                {
                    int count = BinaryPrimitives.ReadUInt16BigEndian(r[2..]);
                    var tail = r[4..].ToArray();
                    short op = BinaryPrimitives.ReadInt16BigEndian(tail);
                    var knots = new List<PathKnot>(count);
                    for (int i = 0; i < count && at + RecordSize <= data.Length; i++, at += RecordSize)
                    {
                        var k = data.Slice(at, RecordSize);
                        int ks = BinaryPrimitives.ReadInt16BigEndian(k);
                        if (ks is not (1 or 2 or 4 or 5))
                        {
                            // A malformed count: stop this subpath and read the record again as its own.
                            break;
                        }
                        knots.Add(new PathKnot(Point(k, 2, width, height), Point(k, 10, width, height), Point(k, 18, width, height), ks is 1 or 4));
                    }
                    subpaths.Add(new Subpath(knots, selector == 0, op is >= 0 and <= 3 ? (PathOperation)op : PathOperation.Combine)
                    {
                        RecordTail = tail,
                        OriginIndex = OriginOf(tail),
                    });
                    layout.Append('S');
                    break;
                }
                case 6:
                    layout.Append('F');
                    break;
                case 7:
                    clipboard = new PathClipboard(Fixed(r[2..]), Fixed(r[6..]), Fixed(r[10..]), Fixed(r[14..]), Fixed(r[18..]));
                    layout.Append('C');
                    break;
                case 8:
                    initialFill = BinaryPrimitives.ReadInt16BigEndian(r[2..]) != 0;
                    layout.Append('I');
                    break;
                default:
                    // Stray knot records outside a subpath, or unknown kinds: ignored.
                    layout.Append('?');
                    break;
            }
        }
        return new VectorPath(subpaths) { InitialFillAll = initialFill, Clipboard = clipboard, RecordLayout = layout.ToString() };
    }

    /// <summary>
    /// The index of the live shape a subpath came from: Photoshop stores it after the operation and two further
    /// short and a zero int (bytes 8..11 of the tail, big-endian), -1 or garbage when there is none.
    /// </summary>
    private static int OriginOf(byte[] tail) => tail.Length >= 12 ? BinaryPrimitives.ReadInt32BigEndian(tail.AsSpan(8)) : -1;

    /// <summary>Encodes <paramref name="path"/> as path records for a <paramref name="width"/>×<paramref name="height"/> canvas.</summary>
    public static byte[] Encode(VectorPath path, int width, int height)
    {
        var o = new MemoryStream();
        string layout = path.RecordLayout is { } l && l.Count(c => c == 'S') == path.Subpaths.Count && !l.Contains('?')
            ? l
            : DefaultLayout(path);
        int next = 0;
        Span<byte> r = stackalloc byte[RecordSize];
        foreach (char c in layout)
        {
            r.Clear();
            switch (c)
            {
                case 'F':
                    BinaryPrimitives.WriteInt16BigEndian(r, 6);
                    o.Write(r);
                    break;
                case 'I':
                    BinaryPrimitives.WriteInt16BigEndian(r, 8);
                    BinaryPrimitives.WriteInt16BigEndian(r[2..], (short)(path.InitialFillAll == true ? 1 : 0));
                    o.Write(r);
                    break;
                case 'C' when path.Clipboard is { } cb:
                    BinaryPrimitives.WriteInt16BigEndian(r, 7);
                    WriteFixed(r[2..], cb.Top); WriteFixed(r[6..], cb.Left); WriteFixed(r[10..], cb.Bottom); WriteFixed(r[14..], cb.Right);
                    WriteFixed(r[18..], cb.Resolution);
                    o.Write(r);
                    break;
                case 'S':
                    WriteSubpath(o, path.Subpaths[next++], width, height);
                    break;
            }
        }
        return o.ToArray();
    }

    private static string DefaultLayout(VectorPath path) =>
        "FI" + (path.Clipboard is null ? "" : "C") + new string('S', path.Subpaths.Count);

    private static void WriteSubpath(MemoryStream o, Subpath s, int width, int height)
    {
        Span<byte> r = stackalloc byte[RecordSize];
        r.Clear();
        BinaryPrimitives.WriteInt16BigEndian(r, (short)(s.Closed ? 0 : 3));
        BinaryPrimitives.WriteUInt16BigEndian(r[2..], (ushort)s.Knots.Count);
        if (s.RecordTail is { Length: 22 } tail)
        {
            tail.CopyTo(r[4..]);
            // Keep a legacy -1 (combine) as it was; write the operation only when it changed.
            short stored = BinaryPrimitives.ReadInt16BigEndian(tail);
            if ((stored is >= 0 and <= 3 ? (PathOperation)stored : PathOperation.Combine) != s.Operation)
                BinaryPrimitives.WriteInt16BigEndian(r[4..], (short)s.Operation);
        }
        else
        {
            // Photoshop's own layout for new subpaths: operation, a 1, four zero bytes, then the live shape's index.
            BinaryPrimitives.WriteInt16BigEndian(r[4..], (short)s.Operation);
            BinaryPrimitives.WriteInt16BigEndian(r[6..], 1);
            BinaryPrimitives.WriteInt32BigEndian(r[12..], Math.Max(0, s.OriginIndex));
        }
        o.Write(r);
        foreach (var k in s.Knots)
        {
            r.Clear();
            BinaryPrimitives.WriteInt16BigEndian(r, (short)(s.Closed ? (k.Linked ? 1 : 2) : (k.Linked ? 4 : 5)));
            WritePoint(r[2..], k.In, width, height);
            WritePoint(r[10..], k.Anchor, width, height);
            WritePoint(r[18..], k.Out, width, height);
            o.Write(r);
        }
    }

    private static PathPoint Point(ReadOnlySpan<byte> r, int at, int width, int height) =>
        new(Fixed(r[(at + 4)..]) * width, Fixed(r[at..]) * height);

    private static void WritePoint(Span<byte> r, PathPoint p, int width, int height)
    {
        WriteFixed(r, p.Y / height);
        WriteFixed(r[4..], p.X / width);
    }

    private static double Fixed(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt32BigEndian(b) / (double)(1 << 24);

    private static void WriteFixed(Span<byte> b, double v) =>
        BinaryPrimitives.WriteInt32BigEndian(b, (int)Math.Clamp(Math.Round(v * (1 << 24)), int.MinValue, int.MaxValue));
}
