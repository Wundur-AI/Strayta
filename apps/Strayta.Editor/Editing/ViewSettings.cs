using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strayta.Editor.Editing;

/// <summary>
/// The person's view preferences that Photoshop keeps per user rather than in the document: whether rulers, guides and
/// the grid show, snapping and what it snaps to, ruler units and the grid's spacing. Stored as view.json next to the
/// presets (<see cref="UserPresets.DefaultDirectory"/>, so STRAYTA_SETTINGS_DIR moves it too). The self-test neither
/// reads nor writes it, so a person's settings never change its results and it never changes theirs.
/// </summary>
public sealed record ViewSettings
{
    public bool ShowRulers { get; init; }
    public bool ShowGuides { get; init; } = true;
    public bool ShowGrid { get; init; }
    public bool LockGuides { get; init; }
    public bool Snap { get; init; } = true;
    public SnapTargets SnapTo { get; init; } = SnapTargets.All;
    public RulerUnit RulerUnit { get; init; } = RulerUnit.Pixels;

    /// <summary>Photoshop's default grid: a line every inch, four subdivisions.</summary>
    public double GridSpacing { get; init; } = 1;
    public RulerUnit GridUnit { get; init; } = RulerUnit.Inches;
    public int GridSubdivisions { get; init; } = 4;

    private static bool Disabled => Environment.GetEnvironmentVariable("STRAYTA_SELFTEST") is { Length: > 0 };

    private static string FilePath => Path.Combine(UserPresets.DefaultDirectory(), "view.json");

    private static ViewSettings? _loaded;

    /// <summary>The settings saved last time (defaults when there are none or they cannot be read).</summary>
    public static ViewSettings Loaded => _loaded ??= Load();

    private static ViewSettings Load()
    {
        if (Disabled) return new ViewSettings();
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<ViewSettings>(File.ReadAllText(FilePath), Options) ?? new ViewSettings() : new ViewSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"Could not read view settings from {FilePath}: {ex.Message}");
            return new ViewSettings();
        }
    }

    public void Save()
    {
        _loaded = this;
        if (Disabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".saving";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not save view settings to {FilePath}: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
