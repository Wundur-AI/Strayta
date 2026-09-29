using System.Text.Json;
using System.Text.Json.Serialization;
using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Editor.Editing;

/// <summary>
/// The person's own gradient and pattern presets (the Gradient Editor's New / Save, Edit › Define Pattern), kept in a
/// JSON file in the per-user application data folder (on macOS ~/Library/Application Support/Strayta/presets.json).
/// </summary>
/// <remarks>
/// <see cref="Directory"/> can be pointed elsewhere (the self-test uses a temporary folder, and STRAYTA_SETTINGS_DIR
/// overrides it for a whole session), so tests never touch the person's real presets. A missing or unreadable file
/// counts as no presets; saving writes a temporary file and moves it into place.
/// </remarks>
public sealed class UserPresets
{
    private static UserPresets? _shared;

    /// <summary>The presets every picker and dialog shares.</summary>
    public static UserPresets Shared => _shared ??= Load(DefaultDirectory());

    /// <summary>Replaces <see cref="Shared"/> with the presets stored in <paramref name="directory"/> (for tests).</summary>
    public static UserPresets UseDirectory(string directory) => _shared = Load(directory);

    public static string DefaultDirectory() =>
        Environment.GetEnvironmentVariable("STRAYTA_SETTINGS_DIR") is { Length: > 0 } dir ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Strayta");

    private UserPresets(string directory) => Directory = directory;

    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, "presets.json");

    public List<Gradient> Gradients { get; } = [];
    public List<Pattern> Patterns { get; } = [];

    /// <summary>Raised after the presets change, so pickers refresh.</summary>
    public event Action? Changed;

    public void AddGradient(Gradient gradient)
    {
        Gradients.Add(gradient);
        Save();
    }

    public void RemoveGradient(Gradient gradient)
    {
        if (Gradients.Remove(gradient)) Save();
    }

    public void AddPattern(Pattern pattern)
    {
        Patterns.Add(pattern);
        Save();
    }

    private static UserPresets Load(string directory)
    {
        var presets = new UserPresets(directory);
        try
        {
            if (!File.Exists(presets.FilePath)) return presets;
            var file = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(presets.FilePath), Options);
            foreach (var g in file?.Gradients ?? []) presets.Gradients.Add(g.ToGradient());
            foreach (var p in file?.Patterns ?? [])
                if (p.ToPattern() is { } pattern) presets.Patterns.Add(pattern);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine($"Could not read presets from {presets.FilePath}: {ex.Message}");
        }
        return presets;
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var file = new PresetFile(Gradients.Select(GradientDto.From).ToList(), Patterns.Select(PatternDto.From).ToList());
            string temp = FilePath + ".saving";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not save presets to {FilePath}: {ex.Message}");
        }
        Changed?.Invoke();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- File format ---------------------------------------------------------------------------------

    private sealed record PresetFile(List<GradientDto>? Gradients, List<PatternDto>? Patterns);

    private sealed record ColorStopDto(float Location, float Midpoint, float R, float G, float B, GradientStopKind Kind);
    private sealed record OpacityStopDto(float Location, float Midpoint, float Opacity);

    private sealed record GradientDto(string Name, float Smoothness, List<ColorStopDto> Colors, List<OpacityStopDto> Opacities, GradientNoise? Noise)
    {
        public static GradientDto From(Gradient g) => new(g.Name, g.Smoothness,
            g.Colors.Select(c => new ColorStopDto(c.Location, c.Midpoint, c.Color.R, c.Color.G, c.Color.B, c.Kind)).ToList(),
            g.Opacities.Select(o => new OpacityStopDto(o.Location, o.Midpoint, o.Opacity)).ToList(), g.Noise);

        public Gradient ToGradient() => new(
            Colors.Select(c => new GradientColorStop(c.Location, c.Midpoint, new RgbColor(c.R, c.G, c.B)) { Kind = c.Kind }).ToList(),
            Opacities.Select(o => new GradientOpacityStop(o.Location, o.Midpoint, o.Opacity)).ToList())
        {
            Name = Name,
            Smoothness = Smoothness,
            Noise = Noise,
        };
    }

    /// <summary>Patterns are stored as 8-bit RGBA, base64.</summary>
    private sealed record PatternDto(string Id, string Name, int Width, int Height, bool Alpha, string Rgba)
    {
        public static PatternDto From(Pattern p)
        {
            var rgba = new byte[p.Width * p.Height * 4];
            for (int y = 0; y < p.Height; y++)
                for (int x = 0; x < p.Width; x++)
                {
                    var (r, g, b, a) = p.At(x, y);
                    int i = (y * p.Width + x) * 4;
                    (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (B(r), B(g), B(b), B(a));
                }
            return new(p.Id, p.Name, p.Width, p.Height, p.Pixels.Alpha is not null, Convert.ToBase64String(rgba));
        }

        public Pattern? ToPattern()
        {
            var rgba = Convert.FromBase64String(Rgba);
            return Width > 0 && Height > 0 && rgba.Length == Width * Height * 4 ? Pattern.FromRgba(Id, Name, Width, Height, rgba, Alpha) : null;
        }

        private static byte B(float v) => (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
    }
}
