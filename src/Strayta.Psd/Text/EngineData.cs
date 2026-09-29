using System.Globalization;
using System.Text;

namespace Strayta.Psd.Text;

/// <summary>
/// A value in Photoshop's text engine data (the 'EngineData' raw item of a type layer's text descriptor): a
/// PostScript-like notation of dictionaries (<c>&lt;&lt; /Key value &gt;&gt;</c>), arrays (<c>[ … ]</c>), numbers,
/// booleans, names and UTF-16 strings in parentheses.
/// </summary>
public abstract class EdValue
{
    /// <summary>A deep copy.</summary>
    public abstract EdValue Clone();
}

/// <summary>A dictionary; entries stay in file order, which Photoshop's own writer keeps canonical.</summary>
public sealed class EdDict : EdValue
{
    public List<KeyValuePair<string, EdValue>> Entries { get; } = [];

    public EdValue? this[string key]
    {
        get
        {
            foreach (var (k, v) in Entries)
                if (k == key) return v;
            return null;
        }
    }

    public bool Has(string key) => this[key] is not null;

    public EdDict? Dict(string key) => this[key] as EdDict;
    public EdArray? Array(string key) => this[key] as EdArray;
    public double? Number(string key) => (this[key] as EdNumber)?.Value;
    public bool? Bool(string key) => (this[key] as EdBool)?.Value;
    public string? String(string key) => (this[key] as EdString)?.Value;

    /// <summary>Replaces the value of <paramref name="key"/> in place, or appends it.</summary>
    public void Set(string key, EdValue value)
    {
        for (int i = 0; i < Entries.Count; i++)
            if (Entries[i].Key == key)
            {
                Entries[i] = new(key, value);
                return;
            }
        Entries.Add(new(key, value));
    }

    /// <summary>
    /// Sets <paramref name="key"/>; a new key goes before the first key of <paramref name="canonicalOrder"/> that follows
    /// it there, so added properties sit where Photoshop would write them.
    /// </summary>
    public void Set(string key, EdValue value, string[] canonicalOrder)
    {
        if (Has(key) || System.Array.IndexOf(canonicalOrder, key) is var rank && rank < 0)
        {
            Set(key, value);
            return;
        }
        for (int i = 0; i < Entries.Count; i++)
        {
            int other = System.Array.IndexOf(canonicalOrder, Entries[i].Key);
            if (other > rank)
            {
                Entries.Insert(i, new(key, value));
                return;
            }
        }
        Entries.Add(new(key, value));
    }

    public bool Remove(string key) => Entries.RemoveAll(e => e.Key == key) > 0;

    public override EdDict Clone()
    {
        var d = new EdDict();
        foreach (var (k, v) in Entries) d.Entries.Add(new(k, v.Clone()));
        return d;
    }
}

public sealed class EdArray : EdValue
{
    public List<EdValue> Items { get; } = [];

    public EdArray() { }
    public EdArray(IEnumerable<EdValue> items) => Items.AddRange(items);

    public override EdArray Clone() => new(Items.Select(i => i.Clone()));
}

/// <summary>
/// A number. <see cref="Lexeme"/> keeps its spelling in the file ("1.0", ".98437", "27.16667") so unedited data is
/// written back exactly; <see cref="IsInteger"/> tells integer-valued keys ("/Font 0") from real ones ("/FontSize 12.0").
/// </summary>
public sealed class EdNumber : EdValue
{
    public double Value { get; }
    public bool IsInteger { get; }
    public string Lexeme { get; }

    public EdNumber(double value, bool isInteger, string? lexeme = null)
    {
        Value = value;
        IsInteger = isInteger;
        Lexeme = lexeme ?? Format(value, isInteger);
    }

    public static EdNumber Int(long value) => new(value, true);
    public static EdNumber Real(double value) => new(value, false);

    /// <summary>
    /// Photoshop's spelling: integers plainly, reals with at most five decimals, trailing zeros dropped but at least
    /// one kept ("1.0"), and no leading zero before the point (".5", "-.25").
    /// </summary>
    public static string Format(double value, bool isInteger)
    {
        if (isInteger) return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        double rounded = Math.Round(value, 5);
        if (rounded == 0) return "0.0";
        string s = rounded.ToString("0.0####", CultureInfo.InvariantCulture);
        if (s.StartsWith("0.", StringComparison.Ordinal)) s = s[1..];
        else if (s.StartsWith("-0.", StringComparison.Ordinal)) s = "-" + s[2..];
        return s;
    }

    public override EdNumber Clone() => this;
}

public sealed class EdBool(bool value) : EdValue
{
    public bool Value { get; } = value;
    public override EdBool Clone() => this;
}

/// <summary>A name used as a value (<c>/Name</c>).</summary>
public sealed class EdName(string name) : EdValue
{
    public string Name { get; } = name;
    public override EdName Clone() => this;
}

/// <summary>
/// A string. Photoshop writes a UTF-16BE byte order mark (FE FF) and the text in big-endian UTF-16, escaping the
/// bytes '(' ')' and '\' with a backslash (byte-wise, so they may fall inside a character's two bytes).
/// <see cref="Raw"/> keeps the exact bytes between the parentheses for strings read from a file.
/// </summary>
public sealed class EdString : EdValue
{
    public string Value { get; }
    internal byte[]? Raw { get; }

    public EdString(string value) => Value = value;

    internal EdString(string value, byte[] raw)
    {
        Value = value;
        Raw = raw;
    }

    public override EdString Clone() => this;
}

/// <summary>Reads and writes <see cref="EdValue"/> trees in the exact layout Photoshop uses.</summary>
public static class EngineData
{
    /// <summary>Parses engine data. Throws <see cref="PsdFormatException"/> on malformed input.</summary>
    public static EdDict Parse(ReadOnlySpan<byte> data)
    {
        var p = new Parser(data.ToArray());
        p.SkipWhitespace();
        var root = p.ReadValue(0) as EdDict ?? throw new PsdFormatException("Engine data does not start with a dictionary");
        return root;
    }

    /// <summary>
    /// Writes engine data in Photoshop's layout: two newlines, then the root dictionary; each key on its own line
    /// indented with tabs; dictionaries open on the line after their key at the key's indentation; arrays of scalars
    /// on one line (<c>[ 1.0 0.0 ]</c>), arrays of dictionaries with each element and the closing bracket at the key's
    /// indentation; no newline after the final <c>&gt;&gt;</c>.
    /// </summary>
    public static byte[] Write(EdDict root)
    {
        var o = new MemoryStream();
        o.WriteByte((byte)'\n');
        o.WriteByte((byte)'\n');
        WriteDict(o, root, 0);
        return o.ToArray();
    }

    private static void WriteDict(MemoryStream o, EdDict d, int indent)
    {
        Ascii(o, "<<\n");
        foreach (var (key, value) in d.Entries)
        {
            Tabs(o, indent + 1);
            o.WriteByte((byte)'/');
            Ascii(o, key);
            WriteMember(o, value, indent + 1);
        }
        Tabs(o, indent);
        Ascii(o, ">>");
    }

    /// <summary>A dictionary's value after its key (the key already written at <paramref name="indent"/>).</summary>
    private static void WriteMember(MemoryStream o, EdValue value, int indent)
    {
        switch (value)
        {
            case EdDict dict:
                o.WriteByte((byte)'\n');
                Tabs(o, indent);
                WriteDict(o, dict, indent);
                o.WriteByte((byte)'\n');
                break;
            case EdArray array when array.Items.Any(i => i is EdDict or EdArray):
                Ascii(o, " [");
                foreach (var item in array.Items)
                {
                    o.WriteByte((byte)'\n');
                    Tabs(o, indent);
                    if (item is EdDict child) WriteDict(o, child, indent);
                    else WriteInline(o, item);
                }
                o.WriteByte((byte)'\n');
                Tabs(o, indent);
                Ascii(o, "]\n");
                break;
            default:
                o.WriteByte((byte)' ');
                WriteInline(o, value);
                o.WriteByte((byte)'\n');
                break;
        }
    }

    private static void WriteInline(MemoryStream o, EdValue value)
    {
        switch (value)
        {
            case EdNumber n: Ascii(o, n.Lexeme); break;
            case EdBool b: Ascii(o, b.Value ? "true" : "false"); break;
            case EdName n: o.WriteByte((byte)'/'); Ascii(o, n.Name); break;
            case EdString s:
                o.WriteByte((byte)'(');
                o.Write(s.Raw ?? EncodeString(s.Value));
                o.WriteByte((byte)')');
                break;
            case EdArray a:
                o.WriteByte((byte)'[');
                foreach (var item in a.Items)
                {
                    o.WriteByte((byte)' ');
                    WriteInline(o, item);
                }
                Ascii(o, " ]");
                break;
            case EdDict d:
                WriteDict(o, d, 0);
                break;
        }
    }

    /// <summary>The bytes of a string between its parentheses: BOM, UTF-16BE, and '(' ')' '\' bytes escaped.</summary>
    public static byte[] EncodeString(string value)
    {
        var o = new List<byte>(value.Length * 2 + 2) { 0xFE, 0xFF };
        foreach (byte b in Encoding.BigEndianUnicode.GetBytes(value))
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\') o.Add((byte)'\\');
            o.Add(b);
        }
        return [.. o];
    }

    private static void Tabs(MemoryStream o, int n)
    {
        for (int i = 0; i < n; i++) o.WriteByte((byte)'\t');
    }

    private static void Ascii(MemoryStream o, string s)
    {
        foreach (char c in s) o.WriteByte((byte)c);
    }

    private sealed class Parser(byte[] d)
    {
        private int _pos;

        public void SkipWhitespace()
        {
            while (_pos < d.Length && d[_pos] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0) _pos++;
        }

        public EdValue ReadValue(int depth)
        {
            if (depth > 200) throw new PsdFormatException("Engine data nested too deeply");
            SkipWhitespace();
            if (_pos >= d.Length) throw new PsdFormatException("Engine data ends early");
            byte c = d[_pos];
            if (c == '<' && Peek(1) == '<') return ReadDict(depth);
            if (c == '[') return ReadArray(depth);
            if (c == '(') return ReadString();
            if (c == '/') { _pos++; return new EdName(ReadToken()); }
            string token = ReadToken();
            if (token == "true") return new EdBool(true);
            if (token == "false") return new EdBool(false);
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return new EdNumber(v, !token.Contains('.') && !token.Contains('e') && !token.Contains('E'), token);
            throw new PsdFormatException($"Unexpected engine data token '{token}' at {_pos}");
        }

        private EdDict ReadDict(int depth)
        {
            _pos += 2;
            var dict = new EdDict();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= d.Length) throw new PsdFormatException("Unterminated engine data dictionary");
                if (d[_pos] == '>' && Peek(1) == '>')
                {
                    _pos += 2;
                    return dict;
                }
                if (d[_pos] != '/') throw new PsdFormatException($"Expected a key at {_pos} in engine data");
                _pos++;
                string key = ReadToken();
                dict.Entries.Add(new(key, ReadValue(depth + 1)));
            }
        }

        private EdArray ReadArray(int depth)
        {
            _pos++;
            var array = new EdArray();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= d.Length) throw new PsdFormatException("Unterminated engine data array");
                if (d[_pos] == ']')
                {
                    _pos++;
                    return array;
                }
                array.Items.Add(ReadValue(depth + 1));
            }
        }

        private EdString ReadString()
        {
            _pos++;
            var raw = new List<byte>();
            var bytes = new List<byte>();
            while (true)
            {
                if (_pos >= d.Length) throw new PsdFormatException("Unterminated engine data string");
                byte b = d[_pos++];
                if (b == ')') break;
                raw.Add(b);
                if (b == '\\')
                {
                    if (_pos >= d.Length) throw new PsdFormatException("Unterminated engine data string");
                    b = d[_pos++];
                    raw.Add(b);
                }
                bytes.Add(b);
            }
            string value = bytes.Count >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
                ? Encoding.BigEndianUnicode.GetString(bytes.ToArray(), 2, (bytes.Count - 2) & ~1)
                : Encoding.Latin1.GetString(bytes.ToArray());
            return new EdString(value, [.. raw]);
        }

        private string ReadToken()
        {
            int start = _pos;
            while (_pos < d.Length && d[_pos] is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0
                       or (byte)'/' or (byte)'[' or (byte)']' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'))
                _pos++;
            if (_pos == start) throw new PsdFormatException($"Empty engine data token at {_pos}");
            return Encoding.ASCII.GetString(d, start, _pos - start);
        }

        private int Peek(int ahead) => _pos + ahead < d.Length ? d[_pos + ahead] : -1;
    }
}
