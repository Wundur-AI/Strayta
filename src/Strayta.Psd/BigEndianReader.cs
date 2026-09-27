using System.Buffers.Binary;
using System.Text;

namespace Strayta.Psd;

/// <summary>Reads big-endian primitives from a seekable stream.</summary>
internal sealed class BigEndianReader(Stream stream)
{
    private readonly byte[] _scratch = new byte[8];

    public Stream Stream { get; } = stream.CanSeek
        ? stream
        : throw new ArgumentException("PSD reading requires a seekable stream.", nameof(stream));

    public long Position
    {
        get => Stream.Position;
        set => Stream.Position = value;
    }

    public long Length => Stream.Length;

    public byte ReadByte()
    {
        int b = Stream.ReadByte();
        return b >= 0 ? (byte)b : throw Truncated();
    }

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(Fill(2));
    public short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(Fill(2));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(Fill(4));
    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Fill(4));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(Fill(8));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleBigEndian(Fill(8));

    /// <summary>Reads a section length: 4 bytes in PSD, 8 bytes in PSB where the format widens it.</summary>
    public long ReadLength(bool wide)
    {
        ulong value = wide ? ReadUInt64() : ReadUInt32();
        return value <= long.MaxValue ? (long)value : throw new PsdFormatException("Section length out of range", Position);
    }

    public string ReadSignature() => Encoding.ASCII.GetString(Fill(4));

    public string PeekSignature()
    {
        if (Position + 4 > Length) return "";
        var s = ReadSignature();
        Position -= 4;
        return s;
    }

    public byte[] ReadBytes(long count)
    {
        if (count < 0 || count > Array.MaxLength)
            throw new PsdFormatException($"Cannot read block of {count} bytes", Position);
        if (Position + count > Length) throw Truncated();
        var buffer = new byte[count];
        Stream.ReadExactly(buffer);
        return buffer;
    }

    public void Skip(long count) => Position += count;

    /// <summary>Reads a length-prefixed string whose total size (including the length byte) is padded to <paramref name="padTo"/>.</summary>
    public string ReadPascalString(int padTo)
    {
        int len = ReadByte();
        var text = Encoding.Latin1.GetString(ReadBytes(len));
        int total = len + 1;
        int rem = total % padTo;
        if (rem != 0) Skip(padTo - rem);
        return text;
    }

    /// <summary>Reads a 4-byte character count followed by UTF-16BE code units.</summary>
    public string ReadUnicodeString()
    {
        uint chars = ReadUInt32();
        return Encoding.BigEndianUnicode.GetString(ReadBytes(chars * 2L)).TrimEnd('\0');
    }

    private ReadOnlySpan<byte> Fill(int count)
    {
        int read = Stream.ReadAtLeast(_scratch.AsSpan(0, count), count, throwOnEndOfStream: false);
        return read == count ? _scratch.AsSpan(0, count) : throw Truncated();
    }

    private PsdFormatException Truncated() => new("Unexpected end of file", Position);
}
