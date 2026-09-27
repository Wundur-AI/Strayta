using System.Buffers.Binary;
using System.Text;

namespace Strayta.Psd.Descriptors;

/// <summary>Reads Action Descriptors from their binary form (big-endian, Adobe "Descriptor structure").</summary>
public sealed class DescriptorReader
{
    private readonly byte[] _data;
    private int _pos;
    private int _depth;

    public DescriptorReader(byte[] data, int offset = 0)
    {
        _data = data;
        _pos = offset;
    }

    public int Position => _pos;

    /// <summary>Reads a block that starts with a 4-byte descriptor version (16) followed by a descriptor.</summary>
    public static Descriptor ReadVersioned(byte[] data, int offset = 0)
    {
        var r = new DescriptorReader(data, offset);
        uint version = r.U32();
        if (version != 16) throw new PsdFormatException($"Unsupported descriptor version {version}");
        return r.ReadDescriptor();
    }

    public Descriptor ReadDescriptor()
    {
        if (++_depth > 64) throw new PsdFormatException("Descriptor nesting too deep");
        try
        {
            string name = UnicodeString();
            string classId = Id();
            uint count = U32();
            if (count > 100_000) throw new PsdFormatException($"Descriptor claims {count} items");
            var items = new List<KeyValuePair<string, DescriptorValue>>((int)count);
            for (uint i = 0; i < count; i++)
            {
                string key = Id();
                items.Add(new(key, Value(Type4())));
            }
            return new Descriptor { Name = name, ClassId = classId, Items = items };
        }
        finally
        {
            _depth--;
        }
    }

    private DescriptorValue Value(string type) => type switch
    {
        "Objc" or "GlbO" => new ObjectValue(ReadDescriptor()),
        "VlLs" => ListOf(U32()),
        "doub" => new DoubleValue(F64()),
        "UntF" => new UnitFloatValue(Type4(), F64()),
        "UnFl" => UnitFloats(),
        "TEXT" => new TextValue(UnicodeString()),
        "enum" => new EnumValue(Id(), Id()),
        "long" => new IntegerValue((int)U32()),
        "comp" => new LargeIntegerValue((long)U64()),
        "bool" => new BoolValue(Bytes(1)[0] != 0),
        "type" or "GlbC" => new ClassValue(UnicodeString(), Id()),
        "obj " => Reference(),
        "alis" or "tdta" or "Pth " => new RawValue(type, BytesArray(checked((int)U32()))),
        _ => throw new PsdFormatException($"Unknown descriptor value type '{type}' at {_pos - 4}"),
    };

    private ListValue ListOf(uint count)
    {
        if (count > 1_000_000) throw new PsdFormatException($"Descriptor list claims {count} items");
        var items = new List<DescriptorValue>((int)Math.Min(count, 1024));
        for (uint i = 0; i < count; i++) items.Add(Value(Type4()));
        return new ListValue(items);
    }

    private UnitFloatsValue UnitFloats()
    {
        string unit = Type4();
        uint count = U32();
        var values = new double[count];
        for (int i = 0; i < count; i++) values[i] = F64();
        return new UnitFloatsValue(unit, values);
    }

    private ReferenceValue Reference()
    {
        uint count = U32();
        var parts = new List<(string, string)>();
        for (uint i = 0; i < count; i++)
        {
            string form = Type4();
            string detail = form switch
            {
                "prop" => $"{UnicodeString()} {Id()} {Id()}",
                "Clss" => $"{UnicodeString()} {Id()}",
                "Enmr" => $"{UnicodeString()} {Id()} {Id()} {Id()}",
                "rele" => $"{UnicodeString()} {Id()} {U32()}",
                "Idnt" or "indx" => U32().ToString(),
                "name" => $"{UnicodeString()} {Id()} {UnicodeString()}",
                _ => throw new PsdFormatException($"Unknown reference form '{form}'"),
            };
            parts.Add((form, detail));
        }
        return new ReferenceValue(parts);
    }

    /// <summary>A class or key ID: a 4-byte length then that many ASCII bytes, or length 0 then a 4-character code.</summary>
    private string Id()
    {
        uint len = U32();
        return len == 0 ? Type4() : Encoding.ASCII.GetString(Bytes(checked((int)len)));
    }

    private string Type4() => Encoding.ASCII.GetString(Bytes(4));

    private string UnicodeString()
    {
        uint chars = U32();
        return Encoding.BigEndianUnicode.GetString(Bytes(checked((int)chars * 2))).TrimEnd('\0');
    }

    private uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Bytes(4));
    private ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Bytes(8));
    private double F64() => BinaryPrimitives.ReadDoubleBigEndian(Bytes(8));

    private ReadOnlySpan<byte> Bytes(int count)
    {
        if (count < 0 || _pos + count > _data.Length)
            throw new PsdFormatException($"Descriptor data truncated (need {count} bytes at {_pos} of {_data.Length})");
        var span = _data.AsSpan(_pos, count);
        _pos += count;
        return span;
    }

    private byte[] BytesArray(int count) => Bytes(count).ToArray();
}
