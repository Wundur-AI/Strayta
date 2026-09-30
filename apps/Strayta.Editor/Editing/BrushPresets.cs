using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Strayta.Core.Painting;

namespace Strayta.Editor.Editing;

/// <summary>
/// A brush preset: tip (a sampled image, or null for the computed round tip), size, hardness (round tips), spacing,
/// angle and roundness, pen-pressure switches and optional Shape Dynamics / Scattering, in a folder of the Brushes panel.
/// </summary>
public sealed record BrushPreset(string Name, string Folder, float Size, float Hardness, float Spacing, float Angle, float Roundness, BrushTip? Tip)
{
    public bool PressureSize { get; init; }
    public bool PressureOpacity { get; init; }
    public BrushDynamics? Dynamics { get; init; }

    /// <summary>Built-in presets ship with Strayta and cannot be deleted or renamed.</summary>
    public bool BuiltIn { get; init; }

    /// <summary>The preset as the brush engine's settings (opacity, flow and mode come from the tool).</summary>
    public BrushSettings ToBrush(float opacity = 1f) => new(Size, Hardness, opacity)
    {
        SpacingPercent = Spacing,
        Angle = Angle,
        Roundness = Roundness,
        Tip = Tip,
        PressureSize = PressureSize,
        PressureOpacity = PressureOpacity,
        Dynamics = Dynamics,
    };
}

/// <summary>
/// The Brushes panel's presets: Strayta's built-in set plus the person's own (New Brush Preset, imported .abr files,
/// folders), which are kept in brushes.json next to the gradient and pattern presets (<see cref="UserPresets"/>).
/// </summary>
/// <remarks>
/// Tips are stored deflated and base64-encoded. The file lives in <see cref="UserPresets.Directory"/>, so tests and
/// STRAYTA_SETTINGS_DIR redirect it together with the other presets. A missing or unreadable file counts as no presets;
/// saving writes a temporary file and moves it into place.
/// </remarks>
public sealed class BrushPresetLibrary
{
    private static BrushPresetLibrary? _shared;
    private readonly List<BrushPreset> _user = [];
    private readonly List<string> _folders = [];

    /// <summary>The library every picker shares; follows <see cref="UserPresets.Shared"/>'s folder.</summary>
    public static BrushPresetLibrary Shared
    {
        get
        {
            string dir = UserPresets.Shared.Directory;
            if (_shared is null || _shared.Directory != dir) _shared = Load(dir);
            return _shared;
        }
    }

    /// <summary>Reads the presets from disk again (for tests: what a new session would see).</summary>
    public static BrushPresetLibrary Reload() => _shared = Load(UserPresets.Shared.Directory);

    private BrushPresetLibrary(string directory) => Directory = directory;

    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, "brushes.json");

    public const string GeneralFolder = "General Brushes";
    public const string SampledFolder = "Sampled Brushes";

    /// <summary>Every preset: the built-in ones first, then the person's.</summary>
    public IEnumerable<BrushPreset> Presets => BuiltIns.Concat(_user);

    /// <summary>Folder names in display order (built-in folders first, then the person's, including empty ones).</summary>
    public IReadOnlyList<string> Folders =>
        [.. new[] { GeneralFolder, SampledFolder }.Concat(_folders).Concat(_user.Select(p => p.Folder)).Distinct()];

    /// <summary>Raised after the presets change, so the panel and pickers refresh.</summary>
    public event Action? Changed;

    public void Add(BrushPreset preset)
    {
        _user.Add(preset with { BuiltIn = false });
        Save();
    }

    public void AddRange(IEnumerable<BrushPreset> presets)
    {
        _user.AddRange(presets.Select(p => p with { BuiltIn = false }));
        Save();
    }

    public void Remove(BrushPreset preset)
    {
        if (_user.Remove(preset)) Save();
    }

    public void Rename(BrushPreset preset, string name)
    {
        int i = _user.IndexOf(preset);
        if (i < 0 || string.IsNullOrWhiteSpace(name)) return;
        _user[i] = preset with { Name = name.Trim() };
        Save();
    }

    public void NewFolder(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || Folders.Contains(name)) return;
        _folders.Add(name);
        Save();
    }

    /// <summary>Deletes a folder of the person's with its presets (built-in folders stay).</summary>
    public void RemoveFolder(string name)
    {
        if (name is GeneralFolder or SampledFolder) return;
        _folders.Remove(name);
        _user.RemoveAll(p => p.Folder == name);
        Save();
    }

    /// <summary>
    /// Imports an .abr file's brushes into a folder named after the file (made unique); returns how many were added.
    /// </summary>
    public int ImportAbr(string path)
    {
        var brushes = Strayta.Psd.AbrReader.Read(path);
        string folder = Path.GetFileNameWithoutExtension(path);
        for (int n = 2; Folders.Contains(folder); n++) folder = $"{Path.GetFileNameWithoutExtension(path)} {n}";
        _folders.Add(folder);
        AddRange(brushes.Select(b => new BrushPreset(b.Name, folder, Math.Clamp(b.Diameter, 1f, 5000f), b.Hardness,
            Math.Clamp(b.SpacingPercent, 1f, 1000f), b.Angle, b.Roundness, b.Tip)));
        return brushes.Count;
    }

    // ---- Built-in presets ------------------------------------------------------------------------------------

    private static IReadOnlyList<BrushPreset>? _builtIns;

    /// <summary>Strayta's own presets: round brushes and a few generated sampled tips.</summary>
    public static IReadOnlyList<BrushPreset> BuiltIns => _builtIns ??= CreateBuiltIns();

    private static List<BrushPreset> CreateBuiltIns()
    {
        var list = new List<BrushPreset>
        {
            new("Soft Round", GeneralFolder, 30, 0f, 25, 0, 1, null) { BuiltIn = true },
            new("Hard Round", GeneralFolder, 30, 1f, 25, 0, 1, null) { BuiltIn = true },
            new("Soft Round Pressure Size", GeneralFolder, 45, 0f, 25, 0, 1, null) { BuiltIn = true, PressureSize = true },
            new("Soft Round Pressure Opacity", GeneralFolder, 45, 0f, 25, 0, 1, null) { BuiltIn = true, PressureOpacity = true },
            new("Hard Round Pressure Size", GeneralFolder, 19, 1f, 25, 0, 1, null) { BuiltIn = true, PressureSize = true },
            new("Flat Calligraphy", GeneralFolder, 25, 1f, 10, 45, 0.2f, null) { BuiltIn = true },
            new("Spatter", SampledFolder, 39, 1f, 25, 0, 1, Generated.Spatter()) { BuiltIn = true },
            new("Chalk", SampledFolder, 36, 1f, 25, 0, 1, Generated.Chalk()) { BuiltIn = true },
            new("Dry Brush", SampledFolder, 50, 1f, 15, 0, 1, Generated.DryBrush()) { BuiltIn = true },
            new("Scattered Dots", SampledFolder, 20, 1f, 60, 0, 1, Generated.Dot()) { BuiltIn = true,
                Dynamics = new BrushDynamics { SizeJitter = 0.8f, Scatter = 2.5f, BothAxes = true, Count = 3, CountJitter = 0.5f } },
        };
        return list;
    }

    /// <summary>Tips generated by Strayta from fixed seeds (no third-party images).</summary>
    private static class Generated
    {
        public static BrushTip Spatter()
        {
            const int s = 64;
            var a = new byte[s * s];
            var rng = new Random(11);
            for (int i = 0; i < 40; i++)
            {
                double ang = rng.NextDouble() * Math.PI * 2, dist = Math.Pow(rng.NextDouble(), 0.7) * 26;
                float cx = s / 2f + (float)(Math.Cos(ang) * dist), cy = s / 2f + (float)(Math.Sin(ang) * dist);
                float r = 1f + (float)rng.NextDouble() * (dist < 10 ? 5f : 2.5f);
                Disc(a, s, cx, cy, r, 255);
            }
            return new BrushTip("Spatter", s, s, a);
        }

        public static BrushTip Chalk()
        {
            const int s = 64;
            var a = new byte[s * s];
            var rng = new Random(23);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float d = MathF.Sqrt((x + 0.5f - s / 2f) * (x + 0.5f - s / 2f) + (y + 0.5f - s / 2f) * (y + 0.5f - s / 2f)) / (s / 2f);
                    float edge = Math.Clamp((1f - d) * 4f, 0f, 1f);
                    float grain = rng.NextSingle() < 0.55f ? 1f : 0.25f * rng.NextSingle();
                    a[y * s + x] = (byte)MathF.Round(255f * edge * grain);
                }
            return new BrushTip("Chalk", s, s, a);
        }

        public static BrushTip DryBrush()
        {
            const int w = 80, h = 24;
            var a = new byte[w * h];
            var rng = new Random(37);
            for (int b = 0; b < 16; b++)
            {
                float y0 = 2f + rng.NextSingle() * (h - 4), thick = 0.6f + rng.NextSingle() * 1.6f, strength = 0.4f + 0.6f * rng.NextSingle();
                float x0 = rng.NextSingle() * 10, x1 = w - rng.NextSingle() * 10;
                for (int x = (int)x0; x < (int)x1; x++)
                    for (int y = 0; y < h; y++)
                    {
                        float d = MathF.Abs(y + 0.5f - y0);
                        if (d > thick + 1) continue;
                        float v = Math.Clamp(thick + 0.5f - d, 0f, 1f) * strength * 255f;
                        a[y * w + x] = (byte)Math.Min(255, a[y * w + x] + v);
                    }
            }
            return new BrushTip("Dry Brush", w, h, a);
        }

        public static BrushTip Dot()
        {
            const int s = 16;
            var a = new byte[s * s];
            Disc(a, s, s / 2f, s / 2f, 7f, 255);
            return new BrushTip("Dot", s, s, a);
        }

        private static void Disc(byte[] a, int s, float cx, float cy, float r, int value)
        {
            for (int y = Math.Max(0, (int)(cy - r - 1)); y < Math.Min(s, (int)(cy + r + 2)); y++)
                for (int x = Math.Max(0, (int)(cx - r - 1)); x < Math.Min(s, (int)(cx + r + 2)); x++)
                {
                    float d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    float v = Math.Clamp(r - d + 0.5f, 0f, 1f) * value;
                    a[y * s + x] = (byte)Math.Max(a[y * s + x], (int)v);
                }
        }
    }

    // ---- Storage ---------------------------------------------------------------------------------------------

    private static BrushPresetLibrary Load(string directory)
    {
        var library = new BrushPresetLibrary(directory);
        try
        {
            if (!File.Exists(library.FilePath)) return library;
            var file = JsonSerializer.Deserialize<LibraryFile>(File.ReadAllText(library.FilePath), Options);
            library._folders.AddRange(file?.Folders ?? []);
            foreach (var p in file?.Presets ?? [])
                if (p.ToPreset() is { } preset) library._user.Add(preset);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or FormatException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"Could not read brush presets from {library.FilePath}: {ex.Message}");
        }
        return library;
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var file = new LibraryFile(_folders.ToList(), _user.Select(PresetDto.From).ToList());
            string temp = FilePath + ".saving";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not save brush presets to {FilePath}: {ex.Message}");
        }
        Changed?.Invoke();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record LibraryFile(List<string>? Folders, List<PresetDto>? Presets);

    private sealed record TipDto(string Name, int Width, int Height, string Deflated);

    private sealed record PresetDto(string Name, string Folder, float Size, float Hardness, float Spacing, float Angle, float Roundness,
        bool PressureSize, bool PressureOpacity, TipDto? Tip, BrushDynamics? Dynamics)
    {
        public static PresetDto From(BrushPreset p) => new(p.Name, p.Folder, p.Size, p.Hardness, p.Spacing, p.Angle, p.Roundness,
            p.PressureSize, p.PressureOpacity, p.Tip is { } t ? new TipDto(t.Name, t.Width, t.Height, Deflate(t.Alpha)) : null, p.Dynamics);

        public BrushPreset? ToPreset()
        {
            BrushTip? tip = null;
            if (Tip is { } t)
            {
                var alpha = Inflate(t.Deflated);
                if (t.Width <= 0 || t.Height <= 0 || alpha.Length != t.Width * t.Height) return null;
                tip = new BrushTip(t.Name, t.Width, t.Height, alpha);
            }
            return new BrushPreset(Name, Folder, Size, Hardness, Spacing, Angle, Roundness, tip)
            {
                PressureSize = PressureSize,
                PressureOpacity = PressureOpacity,
                Dynamics = Dynamics,
            };
        }

        private static string Deflate(byte[] data)
        {
            using var output = new MemoryStream();
            using (var z = new ZLibStream(output, CompressionLevel.Optimal)) z.Write(data);
            return Convert.ToBase64String(output.ToArray());
        }

        private static byte[] Inflate(string base64)
        {
            using var input = new MemoryStream(Convert.FromBase64String(base64));
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            return output.ToArray();
        }
    }
}
