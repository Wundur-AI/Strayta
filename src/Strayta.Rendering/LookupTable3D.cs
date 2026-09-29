using System.Globalization;
using System.Text;

namespace Strayta.Rendering;

/// <summary>
/// A 3D color lookup table read from a .cube (Adobe/Resolve) or .3dl (Autodesk/Lustre) file, applied with
/// tetrahedral interpolation (or trilinear, on request). A .cube file may instead hold a 1D table, applied per channel.
/// </summary>
public sealed class LookupTable3D
{
    private readonly float[] _table;   // N×N×N×3, red fastest
    private readonly float[]? _oneD;   // 1D: N×3
    private readonly float[] _min, _scale;

    private LookupTable3D(int size, float[] table, float[]? oneD, float[] min, float[] max, string title)
    {
        Size = size;
        _table = table;
        _oneD = oneD;
        _min = min;
        _scale = [1f / Math.Max(1e-6f, max[0] - min[0]), 1f / Math.Max(1e-6f, max[1] - min[1]), 1f / Math.Max(1e-6f, max[2] - min[2])];
        Title = title;
    }

    /// <summary>Grid points along each axis (entries for a 1D table).</summary>
    public int Size { get; }

    /// <summary>The file's TITLE, if any.</summary>
    public string Title { get; }

    public bool IsOneDimensional => _oneD is not null;

    /// <summary>
    /// Parses <paramref name="data"/> as the given <paramref name="format"/> ("CUBE" or "3DL"; anything else is
    /// guessed from the content). Returns null when the data is not a lookup table Strayta reads.
    /// </summary>
    public static LookupTable3D? Parse(byte[] data, string? format = null)
    {
        if (data.Length == 0) return null;
        string text;
        try
        {
            text = Encoding.UTF8.GetString(data);
        }
        catch (ArgumentException)
        {
            return null;
        }
        bool looksCube = text.Contains("LUT_3D_SIZE", StringComparison.Ordinal) || text.Contains("LUT_1D_SIZE", StringComparison.Ordinal);
        try
        {
            return string.Equals(format, "3DL", StringComparison.OrdinalIgnoreCase) && !looksCube ? Parse3dl(text)
                : looksCube ? ParseCube(text)
                : Parse3dl(text);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>The format name for a file's extension ("CUBE", "3DL"), or null if unsupported.</summary>
    public static string? FormatOfExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cube" => "CUBE",
        ".3dl" => "3DL",
        _ => null,
    };

    private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static LookupTable3D? ParseCube(string text)
    {
        int size3 = 0, size1 = 0;
        float[] min = [0, 0, 0], max = [1, 1, 1];
        string title = "";
        var values = new List<float>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "TITLE":
                    title = line[5..].Trim().Trim('"');
                    continue;
                case "LUT_3D_SIZE":
                    size3 = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    continue;
                case "LUT_1D_SIZE":
                    size1 = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    continue;
                case "DOMAIN_MIN":
                    min = [F(parts[1]), F(parts[2]), F(parts[3])];
                    continue;
                case "DOMAIN_MAX":
                    max = [F(parts[1]), F(parts[2]), F(parts[3])];
                    continue;
                case "LUT_3D_INPUT_RANGE" or "LUT_1D_INPUT_RANGE":
                    min = [F(parts[1]), F(parts[1]), F(parts[1])];
                    max = [F(parts[2]), F(parts[2]), F(parts[2])];
                    continue;
            }
            if (!(char.IsDigit(parts[0][0]) || parts[0][0] is '-' or '.' or '+')) continue; // other keywords
            if (parts.Length < 3) continue;
            values.Add(F(parts[0]));
            values.Add(F(parts[1]));
            values.Add(F(parts[2]));
        }

        if (size3 is >= 2 and <= 256 && values.Count >= size3 * size3 * size3 * 3)
            return new LookupTable3D(size3, values.Take(size3 * size3 * size3 * 3).ToArray(), null, min, max, title);
        if (size1 is >= 2 and <= 65536 && values.Count >= size1 * 3)
            return new LookupTable3D(size1, [], values.Take(size1 * 3).ToArray(), min, max, title);
        return null;
    }

    /// <summary>
    /// A .3dl: an optional line of N input grid positions, then N³ rows of integer (or float) outputs with blue
    /// changing fastest. Integer outputs are scaled by the bit depth their largest value implies (10, 12 or 16 bits).
    /// </summary>
    private static LookupTable3D? Parse3dl(string text)
    {
        List<float>? mesh = null;
        var rows = new List<float>();
        bool floats = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || !(char.IsDigit(line[0]) || line[0] is '-' or '.')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 3 && mesh is null && rows.Count == 0)
            {
                mesh = parts.Select(F).ToList();
                continue;
            }
            if (parts.Length != 3) continue;
            if (line.Contains('.')) floats = true;
            rows.Add(F(parts[0]));
            rows.Add(F(parts[1]));
            rows.Add(F(parts[2]));
        }
        int count = rows.Count / 3;
        int n = mesh?.Count ?? (int)Math.Round(Math.Cbrt(count));
        if (n < 2 || n * n * n > count) return null;

        float scale = 1f;
        if (!floats)
        {
            float top = rows.Max();
            scale = top <= 1023 ? 1023 : top <= 4095 ? 4095 : top <= 16383 ? 16383 : 65535;
        }
        else if (rows.Max() > 1.5f) scale = rows.Max() <= 1023 ? 1023 : rows.Max() <= 4095 ? 4095 : 65535;

        // Reorder from blue-fastest to red-fastest.
        var table = new float[n * n * n * 3];
        for (int r = 0; r < n; r++)
            for (int g = 0; g < n; g++)
                for (int b = 0; b < n; b++)
                {
                    int src = ((r * n + g) * n + b) * 3, dst = ((b * n + g) * n + r) * 3;
                    table[dst] = rows[src] / scale;
                    table[dst + 1] = rows[src + 1] / scale;
                    table[dst + 2] = rows[src + 2] / scale;
                }
        return new LookupTable3D(n, table, null, [0, 0, 0], [1, 1, 1], "");
    }

    /// <summary>Maps one color (0..1 components); tetrahedral unless <paramref name="trilinear"/>.</summary>
    public (float R, float G, float B) Apply(float r, float g, float b, bool trilinear = false)
    {
        r = Math.Clamp((r - _min[0]) * _scale[0], 0f, 1f);
        g = Math.Clamp((g - _min[1]) * _scale[1], 0f, 1f);
        b = Math.Clamp((b - _min[2]) * _scale[2], 0f, 1f);
        if (_oneD is { } t1) return (Lookup1(t1, r, 0), Lookup1(t1, g, 1), Lookup1(t1, b, 2));

        int n = Size;
        float fr = r * (n - 1), fg = g * (n - 1), fb = b * (n - 1);
        int r0 = Math.Min((int)fr, n - 2), g0 = Math.Min((int)fg, n - 2), b0 = Math.Min((int)fb, n - 2);
        float dr = fr - r0, dg = fg - g0, db = fb - b0;
        var t = _table;
        int sr = 3, sg = n * 3, sb = n * n * 3;
        int c000 = b0 * sb + g0 * sg + r0 * sr;

        if (trilinear)
        {
            (float, float, float) Mix(int ch)
            {
                float At(int o) => t[c000 + o + ch];
                float x00 = At(0) + (At(sr) - At(0)) * dr, x10 = At(sg) + (At(sg + sr) - At(sg)) * dr;
                float x01 = At(sb) + (At(sb + sr) - At(sb)) * dr, x11 = At(sb + sg) + (At(sb + sg + sr) - At(sb + sg)) * dr;
                float y0 = x00 + (x10 - x00) * dg, y1 = x01 + (x11 - x01) * dg;
                return (y0 + (y1 - y0) * db, 0, 0);
            }
            return (Mix(0).Item1, Mix(1).Item1, Mix(2).Item1);
        }

        // Tetrahedral: split the cell into six tetrahedra by the order of the fractional parts.
        int c111 = c000 + sr + sg + sb;
        int a, bIdx;
        float w0, w1, w2, w3; // weights for c000, a, bIdx, c111
        if (dr >= dg)
        {
            if (dg >= db) { a = c000 + sr; bIdx = c000 + sr + sg; (w0, w1, w2, w3) = (1 - dr, dr - dg, dg - db, db); }
            else if (dr >= db) { a = c000 + sr; bIdx = c000 + sr + sb; (w0, w1, w2, w3) = (1 - dr, dr - db, db - dg, dg); }
            else { a = c000 + sb; bIdx = c000 + sr + sb; (w0, w1, w2, w3) = (1 - db, db - dr, dr - dg, dg); }
        }
        else
        {
            if (db >= dg) { a = c000 + sb; bIdx = c000 + sg + sb; (w0, w1, w2, w3) = (1 - db, db - dg, dg - dr, dr); }
            else if (db >= dr) { a = c000 + sg; bIdx = c000 + sg + sb; (w0, w1, w2, w3) = (1 - dg, dg - db, db - dr, dr); }
            else { a = c000 + sg; bIdx = c000 + sr + sg; (w0, w1, w2, w3) = (1 - dg, dg - dr, dr - db, db); }
        }
        return (
            w0 * t[c000] + w1 * t[a] + w2 * t[bIdx] + w3 * t[c111],
            w0 * t[c000 + 1] + w1 * t[a + 1] + w2 * t[bIdx + 1] + w3 * t[c111 + 1],
            w0 * t[c000 + 2] + w1 * t[a + 2] + w2 * t[bIdx + 2] + w3 * t[c111 + 2]);
    }

    private float Lookup1(float[] t, float v, int ch)
    {
        float x = v * (Size - 1);
        int i = Math.Min((int)x, Size - 2);
        float f = x - i;
        return t[i * 3 + ch] + (t[(i + 1) * 3 + ch] - t[i * 3 + ch]) * f;
    }

    /// <summary>
    /// Writes an identity-based .cube built from <paramref name="map"/> sampled on a <paramref name="size"/>³ grid, for
    /// tests and for built-in looks.
    /// </summary>
    public static byte[] WriteCube(string title, int size, Func<float, float, float, (float R, float G, float B)> map)
    {
        var sb = new StringBuilder();
        sb.Append("TITLE \"").Append(title).Append("\"\n");
        sb.Append("LUT_3D_SIZE ").Append(size).Append('\n');
        for (int b = 0; b < size; b++)
            for (int g = 0; g < size; g++)
                for (int r = 0; r < size; r++)
                {
                    var (R, G, B) = map(r / (float)(size - 1), g / (float)(size - 1), b / (float)(size - 1));
                    sb.Append(CultureInfo.InvariantCulture, $"{R:0.000000} {G:0.000000} {B:0.000000}\n");
                }
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
