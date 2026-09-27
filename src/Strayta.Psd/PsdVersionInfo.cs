using System.Buffers.Binary;
using System.Text;

namespace Strayta.Psd;

/// <summary>
/// Image resource 1057: version (4 bytes), "has real merged data" (1 byte), writer name, reader name
/// (both 4-byte length + UTF-16BE), file version (4 bytes).
/// </summary>
public sealed record PsdVersionInfo(uint Version, bool HasRealMergedData, string Writer, string Reader, uint FileVersion)
{
    public const int ResourceId = 1057;

    public static PsdVersionInfo? Read(byte[]? d)
    {
        if (d is null || d.Length < 13) return null;
        try
        {
            int p = 5;
            string writer = Unicode(d, ref p), reader = Unicode(d, ref p);
            uint fileVersion = p + 4 <= d.Length ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p)) : 1;
            return new PsdVersionInfo(BinaryPrimitives.ReadUInt32BigEndian(d), d[4] != 0, writer, reader, fileVersion);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public byte[] ToBytes()
    {
        var o = new MemoryStream();
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, Version); o.Write(b);
        o.WriteByte(HasRealMergedData ? (byte)1 : (byte)0);
        foreach (var s in new[] { Writer, Reader })
        {
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)s.Length); o.Write(b);
            o.Write(Encoding.BigEndianUnicode.GetBytes(s));
        }
        BinaryPrimitives.WriteUInt32BigEndian(b, FileVersion); o.Write(b);
        return o.ToArray();
    }

    private static string Unicode(byte[] d, ref int p)
    {
        int chars = checked((int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p)));
        p += 4;
        var s = Encoding.BigEndianUnicode.GetString(d.AsSpan(p, chars * 2)).TrimEnd('\0');
        p += chars * 2;
        return s;
    }
}
