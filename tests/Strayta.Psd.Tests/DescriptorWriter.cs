using System.Buffers.Binary;
using System.Text;

namespace Strayta.Psd.Tests;

/// <summary>Test helper that encodes Action Descriptors, written from the spec independently of the reader.</summary>
internal sealed class DescriptorWriter
{
    private readonly MemoryStream _o = new();

    public byte[] ToArray() => _o.ToArray();

    public DescriptorWriter Descriptor(string classId, Action<DescriptorWriter> items, int count)
    {
        Unicode("");
        Id(classId);
        U32((uint)count);
        items(this);
        return this;
    }

    public DescriptorWriter Key(string key) { Id(key); return this; }

    public DescriptorWriter Bool(string key, bool v) { Key(key); Type("bool"); _o.WriteByte(v ? (byte)1 : (byte)0); return this; }
    public DescriptorWriter Long(string key, int v) { Key(key); Type("long"); U32((uint)v); return this; }
    public DescriptorWriter Double(string key, double v) { Key(key); Type("doub"); F64(v); return this; }
    public DescriptorWriter Unit(string key, string unit, double v) { Key(key); Type("UntF"); Type(unit); F64(v); return this; }
    public DescriptorWriter Text(string key, string v) { Key(key); Type("TEXT"); Unicode(v); return this; }
    public DescriptorWriter Enum(string key, string type, string v) { Key(key); Type("enum"); Id(type); Id(v); return this; }

    public DescriptorWriter Object(string key, string classId, int count, Action<DescriptorWriter> items)
    {
        Key(key);
        Type("Objc");
        return Descriptor(classId, items, count);
    }

    public DescriptorWriter ListOfObjects(string key, string classId, params (int Count, Action<DescriptorWriter> Items)[] objects)
    {
        Key(key);
        Type("VlLs");
        U32((uint)objects.Length);
        foreach (var (count, items) in objects)
        {
            Type("Objc");
            Descriptor(classId, items, count);
        }
        return this;
    }

    public DescriptorWriter Rgb(string key, double r, double g, double b) =>
        Object(key, "RGBC", 3, w => w.Double("Rd  ", r).Double("Grn ", g).Double("Bl  ", b));

    /// <summary>4-byte IDs are written with a zero length prefix; longer IDs with their length.</summary>
    private void Id(string id)
    {
        if (id.Length == 4) { U32(0); Type(id); }
        else { U32((uint)id.Length); _o.Write(Encoding.ASCII.GetBytes(id)); }
    }

    private void Type(string t) => _o.Write(Encoding.ASCII.GetBytes(t));
    private void Unicode(string s) { U32((uint)s.Length); _o.Write(Encoding.BigEndianUnicode.GetBytes(s)); }
    private void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _o.Write(b); }
    private void F64(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); _o.Write(b); }

    /// <summary>Wraps a descriptor as an 'lfx2' block: object effects version 0, descriptor version 16.</summary>
    public static byte[] EffectsBlock(byte[] descriptor) =>
        [0, 0, 0, 0, 0, 0, 0, 16, .. descriptor];
}
