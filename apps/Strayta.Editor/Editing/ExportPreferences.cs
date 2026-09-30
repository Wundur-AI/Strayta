using System.Text.Json;
using Strayta.Rendering.Export;

namespace Strayta.Editor.Editing;

/// <summary>
/// Export settings remembered between sessions (export.json next to the presets): Quick Export's format and options,
/// Export As's last settings and scale list, and the folder exports last went to.
/// </summary>
public sealed class ExportPreferences
{
    public sealed record Saved(
        int Format = 0, int Quality = 90, bool Transparency = true, bool SmallerFile = false, bool Lossless = false,
        bool ConvertToSrgb = true, int Metadata = 0, (double Scale, string Suffix)[]? Scales = null)
    {
        public ExportOptions ToOptions() => new()
        {
            Format = (ExportFormat)Math.Clamp(Format, 0, 3),
            Quality = Math.Clamp(Quality, 1, 100),
            Transparency = Transparency,
            SmallerFile = SmallerFile,
            Lossless = Lossless,
            ConvertToSrgb = ConvertToSrgb,
            Metadata = (ExportMetadata)Math.Clamp(Metadata, 0, 1),
        };

        public static Saved From(ExportOptions o, IEnumerable<(double, string)>? scales = null) =>
            new((int)o.Format, o.Quality, o.Transparency, o.SmallerFile, o.Lossless, o.ConvertToSrgb, (int)o.Metadata, scales?.ToArray());
    }

    private sealed record File(Saved? QuickExport, Saved? ExportAs, string? LastFolder);

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    private static ExportPreferences? _shared;

    public static ExportPreferences Shared => _shared ??= Load(UserPresets.DefaultDirectory());

    /// <summary>Uses another folder (the self-test's temporary one).</summary>
    public static ExportPreferences UseDirectory(string directory) => _shared = Load(directory);

    private ExportPreferences(string directory) => Directory = directory;

    public string Directory { get; }
    private string FilePath => Path.Combine(Directory, "export.json");

    /// <summary>Quick Export as PNG: PNG with transparency unless changed.</summary>
    public Saved QuickExport { get; private set; } = new();

    public Saved ExportAs { get; private set; } = new(Scales: [(1, "")]);

    public string? LastFolder { get; private set; }

    public void Remember(Saved? quickExport = null, Saved? exportAs = null, string? folder = null)
    {
        if (quickExport is not null) QuickExport = quickExport;
        if (exportAs is not null) ExportAs = exportAs;
        if (folder is not null) LastFolder = folder;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string temp = FilePath + ".tmp";
            System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(new File(QuickExport, ExportAs, LastFolder), Json));
            System.IO.File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience; failing to store them must not fail the export.
        }
    }

    private static ExportPreferences Load(string directory)
    {
        var prefs = new ExportPreferences(directory);
        try
        {
            if (System.IO.File.Exists(prefs.FilePath) && JsonSerializer.Deserialize<File>(System.IO.File.ReadAllText(prefs.FilePath), Json) is { } f)
            {
                prefs.QuickExport = f.QuickExport ?? prefs.QuickExport;
                prefs.ExportAs = f.ExportAs ?? prefs.ExportAs;
                prefs.LastFolder = f.LastFolder;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return prefs;
    }
}
