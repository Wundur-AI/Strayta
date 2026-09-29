using System.Buffers.Binary;
using System.Text;

namespace Strayta.Psd.Descriptors;

/// <summary>
/// Encodes Action Descriptors in their binary form; the inverse of <see cref="DescriptorReader"/>. The layout
/// follows Adobe's "Descriptor structure" and the choices Photoshop itself makes where the format allows several:
/// strings end with a null character (counted in their length), four-character keys and class IDs are written with
/// a zero length prefix, objects as 'Objc' and classes as 'type'.
/// </summary>
public sealed class DescriptorWriter
{
    private readonly MemoryStream _o = new();

    /// <summary>A descriptor preceded by its 4-byte version (16), as stored in tagged blocks.</summary>
    public static byte[] WriteVersioned(Descriptor descriptor)
    {
        var w = new DescriptorWriter();
        w.U32(16);
        w.WriteObject(descriptor);
        return w._o.ToArray();
    }

    /// <summary>A bare descriptor (name, class ID and items).</summary>
    public static byte[] Write(Descriptor descriptor)
    {
        var w = new DescriptorWriter();
        w.WriteObject(descriptor);
        return w._o.ToArray();
    }

    private void WriteObject(Descriptor d)
    {
        Unicode(d.Name);
        Id(d.ClassId);
        U32((uint)d.Items.Count);
        foreach (var (key, value) in d.Items)
        {
            Id(key);
            Value(value);
        }
    }

    private void Value(DescriptorValue value)
    {
        switch (value)
        {
            case ObjectValue o: Type("Objc"); WriteObject(o.Value); break;
            case ListValue l:
                Type("VlLs");
                U32((uint)l.Items.Count);
                foreach (var item in l.Items) Value(item);
                break;
            case DoubleValue d: Type("doub"); F64(d.Value); break;
            case UnitFloatValue u: Type("UntF"); Type(u.Unit); F64(u.Value); break;
            case UnitFloatsValue u:
                Type("UnFl");
                Type(u.Unit);
                U32((uint)u.Values.Count);
                foreach (var v in u.Values) F64(v);
                break;
            case TextValue t: Type("TEXT"); Unicode(t.Value); break;
            case EnumValue e: Type("enum"); Id(e.Type); Id(e.Value); break;
            case IntegerValue i: Type("long"); U32((uint)i.Value); break;
            case LargeIntegerValue l: Type("comp"); U64((ulong)l.Value); break;
            case BoolValue b: Type("bool"); _o.WriteByte(b.Value ? (byte)1 : (byte)0); break;
            case ClassValue c: Type("type"); Unicode(c.Name); Id(c.ClassId); break;
            case RawValue r: Type(r.Type); U32((uint)r.Data.Length); _o.Write(r.Data); break;
            case ObjectArrayValue a: Type("ObAr"); U32((uint)a.Count); WriteObject(a.Columns); break;
            default:
                // References are only read (they appear in actions, not in the layer data Strayta writes).
                throw new NotSupportedException($"Writing descriptor values of type {value.GetType().Name} is not supported.");
        }
    }

    /// <summary>Four-character IDs as a zero length plus the code; any other length as a length-prefixed string.</summary>
    private void Id(string id)
    {
        if (id.Length == 4)
        {
            U32(0);
            Type(id);
        }
        else
        {
            U32((uint)id.Length);
            _o.Write(Encoding.ASCII.GetBytes(id));
        }
    }

    private void Type(string code)
    {
        if (code.Length != 4) throw new ArgumentException($"'{code}' is not a four-character code.", nameof(code));
        _o.Write(Encoding.ASCII.GetBytes(code));
    }

    /// <summary>A UTF-16 string with its terminating null, which the reader strips again.</summary>
    private void Unicode(string s)
    {
        U32((uint)s.Length + 1);
        _o.Write(Encoding.BigEndianUnicode.GetBytes(s));
        _o.WriteByte(0);
        _o.WriteByte(0);
    }

    private void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _o.Write(b); }
    private void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); _o.Write(b); }
    private void F64(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); _o.Write(b); }
}
