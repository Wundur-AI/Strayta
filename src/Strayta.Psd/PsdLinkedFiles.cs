using System.Buffers.Binary;
using System.Text;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>
/// One entry of a document's linked-files block ('lnk2' / 'lnk3' / 'lnkD'): a file a smart object shows, embedded in
/// the document ('liFD') or linked from outside it ('liFE', 'liFA').
/// </summary>
public sealed record PsdLinkedFile
{
    /// <summary>"liFD" (embedded), "liFE" (external) or "liFA" (alias).</summary>
    public required string Kind { get; init; }

    /// <summary>Entry version (1..7); newer versions carry the trailing fields below.</summary>
    public int Version { get; init; } = 7;

    /// <summary>Matches the smart objects' "Idnt".</summary>
    public required string UniqueId { get; init; }

    /// <summary>The original file name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Mac file type, e.g. "8BPB", "png ", "JPEG"; may be blank.</summary>
    public string FileType { get; init; } = "    ";

    public string Creator { get; init; } = "8BIM";

    /// <summary>The embedded file ('liFD'), or null.</summary>
    public byte[]? Data { get; init; }

    /// <summary>Options for opening the file (Photoshop writes "compInfo"), or null.</summary>
    public Descriptor? OpenDescriptor { get; init; }

    /// <summary>Where an external file is ('liFE': "fullPath", "relPath", "Nm  ", ...), or null.</summary>
    public Descriptor? LinkDescriptor { get; init; }

    /// <summary>An external file's modification date as stored (year, month, day, hour, minute, seconds), or null.</summary>
    public (int Year, int Month, int Day, int Hour, int Minute, double Seconds)? FileDate { get; init; }

    /// <summary>An external file's size in bytes when linked.</summary>
    public long ExternalSize { get; init; }

    /// <summary>Version 5 and later: the child document's ID ("" when none).</summary>
    public string ChildDocumentId { get; init; } = "";

    /// <summary>Version 6 and later: the asset's modification time (Libraries).</summary>
    public double AssetModTime { get; init; }

    /// <summary>Version 7 and later: whether the asset is locked.</summary>
    public byte AssetLocked { get; init; }

    /// <summary>Version 8 and later: a descriptor with the content's own ID ("contentID"), which changes with the content.</summary>
    public Descriptor? ContentDescriptor { get; init; }

    /// <summary>The content ID of version 8 entries, or null.</summary>
    public string? ContentId => ContentDescriptor?.Text("contentID");

    /// <summary>Fields after the known ones (newer versions), kept as read.</summary>
    public byte[] Tail { get; init; } = [];

    /// <summary>The entry's bytes exactly as read (without the length and padding), written back unchanged when set.</summary>
    public byte[]? Raw { get; init; }

    /// <summary>The external file's full path for 'liFE' entries ("fullPath" is a file URL, or "originalPath").</summary>
    public string? ExternalPath
    {
        get
        {
            if (LinkDescriptor is not { } d) return null;
            if (d.Text("fullPath") is { Length: > 0 } full)
                return Uri.TryCreate(full, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : full;
            return d.Text("originalPath") ?? d.Text("relPath");
        }
    }

    /// <summary>A copy with other content and without the stored bytes (so it is encoded from its fields).</summary>
    /// <remarks>Version 8 entries get a new content ID, as Photoshop gives changed content one.</remarks>
    public PsdLinkedFile WithData(byte[] data, string? name = null, string? fileType = null) =>
        this with
        {
            Kind = "liFD", Data = data, Name = name ?? Name, FileType = fileType ?? FileType, LinkDescriptor = null, FileDate = null, ExternalSize = 0,
            Raw = null, ContentDescriptor = Version >= 8 ? NewContentDescriptor(ContentDescriptor) : ContentDescriptor,
        };

    /// <summary>A content descriptor like <paramref name="old"/> with a fresh "contentID".</summary>
    public static Descriptor NewContentDescriptor(Descriptor? old)
    {
        var id = new KeyValuePair<string, DescriptorValue>("contentID", new TextValue(Guid.NewGuid().ToString()));
        if (old is null) return new Descriptor { ClassId = "null", Items = [id] };
        var items = old.Items.Where(kv => kv.Key != "contentID").ToList();
        items.Insert(0, id);
        return new Descriptor { Name = old.Name, ClassId = old.ClassId, Items = items };
    }
}

/// <summary>
/// Reads and writes linked-files blocks. The layout, as Adobe documents it and Photoshop writes it: each entry is an
/// 8-byte length, then the kind ('liFD' / 'liFE' / 'liFA'), a 4-byte version, the unique ID (Pascal string), the
/// original file name (Unicode string), file type and creator (4 bytes each), the data length (8 bytes), a flag and,
/// when set, a versioned descriptor of open-file options; for 'liFE' a versioned descriptor of where the file is, from
/// version 3 its date (year 4 bytes, month, day, hour and minute 1 byte each, seconds a double) and its size (8 bytes);
/// for 'liFD' the file itself; from version 5 the child document ID (Unicode string), from 6 the asset modification
/// time (double) and from 7 the asset lock (1 byte). Entries are padded to a multiple of 4 bytes.
/// </summary>
public static class PsdLinkedFiles
{
    /// <summary>The entries of a block; unreadable entries are skipped (and would be lost on rewrite, so check <see cref="IsFullyReadable"/>).</summary>
    public static IReadOnlyList<PsdLinkedFile> Read(byte[] data)
    {
        var list = new List<PsdLinkedFile>();
        long at = 0;
        while (at + 16 <= data.Length)
        {
            long length = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan((int)at));
            long start = at + 8, end = start + length;
            if (length <= 0 || end > data.Length) break;
            if (ReadEntry(data, (int)start, (int)end) is { } entry) list.Add(entry);
            at = start + ((length + 3) & ~3L);
        }
        return list;
    }

    /// <summary>True when every entry of the block reads back, so rewriting it loses nothing.</summary>
    public static bool IsFullyReadable(byte[] data)
    {
        long at = 0;
        int count = 0;
        while (at + 16 <= data.Length)
        {
            long length = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan((int)at));
            long start = at + 8, end = start + length;
            if (length <= 0 || end > data.Length) return false;
            if (ReadEntry(data, (int)start, (int)end) is null) return false;
            count++;
            at = start + ((length + 3) & ~3L);
        }
        return at >= data.Length - 3;
    }

    /// <summary>Encodes a block; entries that still carry their <see cref="PsdLinkedFile.Raw"/> bytes are written unchanged.</summary>
    public static byte[] Write(IEnumerable<PsdLinkedFile> entries)
    {
        var o = new MemoryStream();
        Span<byte> len = stackalloc byte[8];
        foreach (var e in entries)
        {
            var body = e.Raw ?? Encode(e);
            BinaryPrimitives.WriteInt64BigEndian(len, body.Length);
            o.Write(len);
            o.Write(body);
            for (int pad = (4 - body.Length % 4) % 4; pad > 0; pad--) o.WriteByte(0);
        }
        return o.ToArray();
    }

    private static byte[] Encode(PsdLinkedFile e)
    {
        var o = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); o.Write(b); }
        void I64(long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); o.Write(b); }
        void F64(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); o.Write(b); }
        void Unicode(string s)
        {
            U32((uint)(s.Length + 1));
            o.Write(Encoding.BigEndianUnicode.GetBytes(s));
            o.Write([0, 0]);
        }
        void Four(string s) => o.Write(Encoding.ASCII.GetBytes((s + "    ")[..4]));

        Four(e.Kind);
        U32((uint)e.Version);
        var id = Encoding.ASCII.GetBytes(e.UniqueId);
        o.WriteByte((byte)Math.Min(255, id.Length));
        o.Write(id, 0, Math.Min(255, id.Length));
        Unicode(e.Name);
        Four(e.FileType);
        Four(e.Creator);
        I64(e.Kind == "liFD" ? e.Data?.Length ?? 0 : e.ExternalSize);
        if (e.OpenDescriptor is { } open)
        {
            o.WriteByte(1);
            o.Write(DescriptorWriter.WriteVersioned(open));
        }
        else o.WriteByte(0);
        if (e.Kind == "liFE")
        {
            o.Write(DescriptorWriter.WriteVersioned(e.LinkDescriptor ?? new Descriptor { ClassId = "ExternalFileLink" }));
            if (e.Version > 3)
            {
                var d = e.FileDate ?? (1970, 1, 1, 0, 0, 0);
                U32((uint)d.Year);
                o.WriteByte((byte)d.Month); o.WriteByte((byte)d.Day); o.WriteByte((byte)d.Hour); o.WriteByte((byte)d.Minute);
                F64(d.Seconds);
            }
            I64(e.ExternalSize);
        }
        else if (e.Kind == "liFA") I64(0);
        else if (e.Data is { } data) o.Write(data);
        if (e.Version >= 5) Unicode(e.ChildDocumentId);
        if (e.Version >= 6) F64(e.AssetModTime);
        if (e.Version >= 7) o.WriteByte(e.AssetLocked);
        if (e.Version >= 8) o.Write(DescriptorWriter.WriteVersioned(e.ContentDescriptor ?? PsdLinkedFile.NewContentDescriptor(null)));
        o.Write(e.Tail);
        return o.ToArray();
    }

    private static PsdLinkedFile? ReadEntry(byte[] data, int at, int end)
    {
        int start = at;
        try
        {
            string kind = Encoding.ASCII.GetString(data, at, 4);
            if (kind is not ("liFD" or "liFE" or "liFA")) return null;
            int version = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at + 4));
            at += 8;
            int idLength = data[at];
            string id = Encoding.ASCII.GetString(data, at + 1, idLength);
            at += 1 + idLength;
            int nameChars = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at));
            string name = Encoding.BigEndianUnicode.GetString(data, at + 4, nameChars * 2).TrimEnd('\0');
            at += 4 + nameChars * 2;
            string type = Encoding.ASCII.GetString(data, at, 4), creator = Encoding.ASCII.GetString(data, at + 4, 4);
            at += 8;
            long size = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(at));
            at += 8;
            Descriptor? open = null, link = null;
            if (data[at++] != 0)
            {
                var reader = new DescriptorReader(data, at + 4);
                open = reader.ReadDescriptor();
                at = reader.Position;
            }
            byte[]? content = null;
            (int, int, int, int, int, double)? date = null;
            long externalSize = 0;
            if (kind == "liFE")
            {
                var reader = new DescriptorReader(data, at + 4);
                link = reader.ReadDescriptor();
                at = reader.Position;
                if (version > 3)
                {
                    date = (BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at)), data[at + 4], data[at + 5], data[at + 6], data[at + 7],
                        BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(at + 8)));
                    at += 16;
                }
                externalSize = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(at));
                at += 8;
            }
            else if (kind == "liFA") at += 8;
            else
            {
                if (size < 0 || at + size > end) return null;
                content = data.AsSpan(at, (int)size).ToArray();
                at += (int)size;
            }
            string child = "";
            double modTime = 0;
            byte locked = 0;
            if (version >= 5 && at + 4 <= end)
            {
                int chars = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at));
                child = Encoding.BigEndianUnicode.GetString(data, at + 4, chars * 2).TrimEnd('\0');
                at += 4 + chars * 2;
            }
            if (version >= 6 && at + 8 <= end)
            {
                modTime = BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(at));
                at += 8;
            }
            if (version >= 7 && at < end) locked = data[at++];
            Descriptor? contentDescriptor = null;
            if (version >= 8 && at + 4 < end)
            {
                var reader = new DescriptorReader(data, at + 4);
                contentDescriptor = reader.ReadDescriptor();
                at = reader.Position;
            }
            if (at > end) return null;
            return new PsdLinkedFile
            {
                Kind = kind, Version = version, UniqueId = id, Name = name, FileType = type, Creator = creator, Data = content,
                OpenDescriptor = open, LinkDescriptor = link, FileDate = date, ExternalSize = kind == "liFD" ? 0 : externalSize == 0 ? size : externalSize,
                ChildDocumentId = child, AssetModTime = modTime, AssetLocked = locked, ContentDescriptor = contentDescriptor, Tail = data.AsSpan(at, end - at).ToArray(), Raw = data.AsSpan(start, end - start).ToArray(),
            };
        }
        catch (Exception e) when (e is PsdFormatException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
