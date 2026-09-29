using System.Text;

namespace Strayta.Psd.Descriptors;

/// <summary>
/// An Action Descriptor: Photoshop's general-purpose key/value structure, used for layer effects,
/// text, smart objects and most newer layer properties.
/// </summary>
public sealed class Descriptor
{
    public string Name { get; init; } = "";
    public string ClassId { get; init; } = "";

    /// <summary>Items in file order. Keys are 4-character codes (e.g. "Clr ") or longer string IDs.</summary>
    public IReadOnlyList<KeyValuePair<string, DescriptorValue>> Items { get; init; } = [];

    public DescriptorValue? this[string key] => Items.FirstOrDefault(kv => kv.Key == key).Value;

    public bool Has(string key) => this[key] is not null;

    public Descriptor? Object(string key) => this[key] is ObjectValue o ? o.Value : null;
    public bool? Bool(string key) => this[key] is BoolValue b ? b.Value : null;
    public string? Enum(string key) => this[key] is EnumValue e ? e.Value : null;
    public string? Text(string key) => this[key] is TextValue t ? t.Value : null;
    public IReadOnlyList<DescriptorValue>? List(string key) => this[key] is ListValue l ? l.Items : null;

    /// <summary>Reads a number from a double, integer or unit-float item.</summary>
    public double? Number(string key) => this[key] switch
    {
        DoubleValue d => d.Value,
        UnitFloatValue u => u.Value,
        IntegerValue i => i.Value,
        LargeIntegerValue l => l.Value,
        _ => null,
    };

    public override string ToString()
    {
        var sb = new StringBuilder();
        Dump(sb, 0);
        return sb.ToString();
    }

    internal void Dump(StringBuilder sb, int indent)
    {
        sb.Append(ClassId).AppendLine(" {");
        foreach (var (key, value) in Items)
        {
            sb.Append(' ', indent + 2).Append(key.TrimEnd()).Append(": ");
            value.Dump(sb, indent + 2);
            sb.AppendLine();
        }
        sb.Append(' ', indent).Append('}');
    }
}

public abstract record DescriptorValue
{
    internal virtual void Dump(StringBuilder sb, int indent) => sb.Append(ToString());
}

public sealed record BoolValue(bool Value) : DescriptorValue
{
    internal override void Dump(StringBuilder sb, int indent) => sb.Append(Value ? "true" : "false");
}

public sealed record IntegerValue(int Value) : DescriptorValue
{
    internal override void Dump(StringBuilder sb, int indent) => sb.Append(Value);
}

public sealed record LargeIntegerValue(long Value) : DescriptorValue
{
    internal override void Dump(StringBuilder sb, int indent) => sb.Append(Value);
}

public sealed record DoubleValue(double Value) : DescriptorValue
{
    internal override void Dump(StringBuilder sb, int indent) => sb.Append(Value.ToString("0.###"));
}

/// <summary>A number with a unit: "#Ang" angle, "#Rsl" density, "#Rlt" distance, "#Nne" none, "#Prc" percent, "#Pxl" pixels.</summary>
public sealed record UnitFloatValue(string Unit, double Value) : DescriptorValue
{
    internal override void Dump(StringBuilder sb, int indent) => sb.Append(Value.ToString("0.###")).Append(' ').Append(Unit);
}

public sealed record UnitFloatsValue(string Unit, IReadOnlyList<double> Values) : DescriptorValue;

public sealed record TextValue(string Value) : DescriptorValue
{
    internal override void Dump(StringBuilder sb, int indent) => sb.Append('"').Append(Value).Append('"');
}

public sealed record EnumValue(string Type, string Value) : DescriptorValue
{
    internal override void Dump(StringBuilder sb, int indent) => sb.Append(Type.TrimEnd()).Append('.').Append(Value.TrimEnd());
}

public sealed record ClassValue(string Name, string ClassId) : DescriptorValue;

public sealed record ObjectValue(Descriptor Value) : DescriptorValue
{
    internal override void Dump(StringBuilder sb, int indent) => Value.Dump(sb, indent);
}

public sealed record ListValue(IReadOnlyList<DescriptorValue> Items) : DescriptorValue
{
    internal override void Dump(StringBuilder sb, int indent)
    {
        sb.Append('[');
        foreach (var item in Items)
        {
            sb.AppendLine().Append(' ', indent + 2);
            item.Dump(sb, indent + 2);
        }
        sb.Append(']');
    }
}

/// <summary>'ObAr': <paramref name="Count"/> objects stored field by field in <paramref name="Columns"/> (warp meshes, slices).</summary>
public sealed record ObjectArrayValue(int Count, Descriptor Columns) : DescriptorValue
{
    internal override void Dump(StringBuilder sb, int indent)
    {
        sb.Append('[').Append(Count).Append("] ");
        Columns.Dump(sb, indent);
    }
}

/// <summary>A reference to other objects; kept as a list of its parts.</summary>
public sealed record ReferenceValue(IReadOnlyList<(string Form, string Detail)> Parts) : DescriptorValue;

/// <summary>Opaque bytes ('tdta' raw data, 'alis' aliases, paths).</summary>
public sealed record RawValue(string Type, byte[] Data) : DescriptorValue
{
    internal override void Dump(StringBuilder sb, int indent) => sb.Append($"<{Type.TrimEnd()} {Data.Length} bytes>");
}
